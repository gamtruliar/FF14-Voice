using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using Lumina.Excel;

// Only reads installed SqPacks. Reports and samples go to the specified output folder.
if (args.Length is < 2 or > 3 || (args.Length == 3 && args[2] != "--samples-only"))
{
    Console.Error.WriteLine("Usage: VoiceScan <game/sqpack> <output-directory> [--samples-only]");
    return 1;
}
var input = Path.GetFullPath(args[0]);
var output = Path.GetFullPath(args[1]);
if (output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || output == input)
    throw new ArgumentException("Output must be outside game/sqpack.");
Directory.CreateDirectory(output);
using var game = new GameData(input);
var sheets = game.Excel.SheetNames.Where(n => n.StartsWith("cut_scene/", StringComparison.OrdinalIgnoreCase)
    && n.Contains("VoiceMan_", StringComparison.OrdinalIgnoreCase)).OrderBy(n => n).ToArray();
var expansions = Directory.GetDirectories(input).Select(Path.GetFileName).Where(n => n == "ffxiv" || n!.StartsWith("ex")).ToArray();
var languages = new Dictionary<string, Language> { ["en"] = Language.English, ["ja"] = Language.Japanese, ["de"] = Language.German, ["fr"] = Language.French };
var names = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("ENpcResident", Language.English))
    .Select(row => row.ReadColumn(0)?.ToString() ?? "").Where(n => n.Length > 0).Distinct()
    .GroupBy(Normalize).ToDictionary(g => g.Key, g => g.ToArray());
var sampleSpeakers = new HashSet<string> { "ALPHINAUD", "ALISAIE", "THANCRED", "URIANGER", "YSHTOLA", "TATARU" };
var samples = new HashSet<string>();
var samplesOnly = args.Length == 3;
var clips = samplesOnly ? JsonSerializer.Deserialize<List<Clip>>(File.ReadAllText(Path.Combine(output, "voice-clips.json")))! : new List<Clip>();
var errors = new List<string>();
int keyedLines = 0, explicitUnvoiced = 0;
foreach (var sheetName in samplesOnly ? [] : sheets)
{
    var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet(sheetName, Language.English));
    var localized = languages.ToDictionary(p => p.Key, p => new ExcelSheet<RawRow>(game.Excel.GetRawSheet(sheetName, p.Value)));
    foreach (var row in sheet)
    {
        var key = row.ReadColumn(0)?.ToString() ?? "";
        if (key.EndsWith("_NONE_VOICE", StringComparison.OrdinalIgnoreCase)) { explicitUnvoiced++; continue; }
        var parts = key.Split('_');
        if (parts.Length != 5 || parts[0] != "TEXT" || parts[1] != "VOICEMAN" || !uint.TryParse(parts[3], out _)) continue;
        keyedLines++;
        var speaker = parts[4];
        var group = $"voiceman_{parts[2]}";
        var stem = $"vo_{group}_{parts[3]}";
        foreach (var language in languages.Keys)
        foreach (var expansion in expansions)
        foreach (var variant in new[] { "", "_m", "_f" })
        {
            var path = $"cut/{expansion}/sound/voicem/{group}/{stem}{variant}_{language}.scd";
            if (!game.FileExists(path)) continue;
            try
            {
                var file = game.GetFile<ScdFile>(path);
                if (file is null || file.AudioDataCount == 0) continue;
                var audio = file.GetAudio(0);
                if (audio.AudioData.Length == 0 || audio.AudioBasicDesc.Channel == 0) continue;
                string? sample = null;
                var text = localized[language].GetRow(row.RowId).ReadColumn(1)?.ToString() ?? "";
                clips.Add(new Clip(speaker, names.GetValueOrDefault(Normalize(speaker)) ?? [], language, sheetName,
                    row.RowId, key, text, path, file.Data.Length, audio.AudioBasicDesc.Rate, audio.AudioBasicDesc.Channel, sample));
            }
            catch (Exception ex) { errors.Add(path + ": " + ex.Message); }
        }
    }
    Console.WriteLine($"{sheetName}: {clips.Count} verified audio variants so far");
}
// Choose useful reference lengths, preferring the latest installed expansion.
for (var i = 0; i < clips.Count; i++) clips[i] = clips[i] with { SampleFile = null };
foreach (var speaker in sampleSpeakers)
foreach (var lang in new[] { "en", "ja" })
{
    var candidates = clips.Select((clip, index) => (clip, index))
        .Where(c => c.clip.SpeakerToken == speaker && c.clip.Language == lang)
        .OrderByDescending(c => c.clip.Sheet);
    foreach (var (clip, index) in candidates)
    {
        var audio = game.GetFile<ScdFile>(clip.GamePath)!.GetAudio(0);
        if (audio.AudioDataHeader is not AdpcmWaveFormat fmt) continue;
        var duration = (double)(audio.AudioData.Length / fmt.BlockAlign) * fmt.SamplesPerBlock / fmt.SamplesPerSec;
        if (duration is < 5 or > 12) continue;
        var folder = Path.Combine(output, "samples", speaker);
        Directory.CreateDirectory(folder);
        var sample = SaveAudio(audio, Path.Combine(folder, lang + "-source"));
        clips[index] = clip with { SampleFile = sample };
        samples.Add(speaker + "_" + lang);
        Console.WriteLine($"SAMPLE {speaker} {lang} {duration:F2}s {clip.Sheet}");
        break;
    }
}
var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(output, "voice-clips.json"), JsonSerializer.Serialize(clips, options), Encoding.UTF8);
var characters = clips.GroupBy(c => c.SpeakerToken).OrderBy(g => g.Key).Select(g => new
{
    SpeakerToken = g.Key, NameCandidates = g.SelectMany(c => c.NameCandidates).Distinct().ToArray(),
    Languages = g.GroupBy(c => c.Language).ToDictionary(l => l.Key, l => l.Count()),
    Samples = g.Where(c => c.SampleFile != null).Select(c => new { c.Language, c.SampleFile, c.Text, c.GamePath }).ToArray()
}).ToArray();
File.WriteAllText(Path.Combine(output, "characters.json"), JsonSerializer.Serialize(characters, options), Encoding.UTF8);
if (!samplesOnly) File.WriteAllText(Path.Combine(output, "scan-summary.json"), JsonSerializer.Serialize(new
{
    Sqpack = input, ScannedAt = DateTimeOffset.Now, VoiceSheets = sheets.Length, KeyedLines = keyedLines,
    ExplicitUnvoicedKeys = explicitUnvoiced, VerifiedAudioVariants = clips.Count, SpeakerTokens = characters.Length,
    UniqueAudioPaths = clips.Select(c => c.GamePath).Distinct().Count(),
    NameMatchedSpeakerTokens = clips.Where(c => c.NameCandidates.Length > 0).Select(c => c.SpeakerToken).Distinct().Count(),
    LanguageCounts = clips.GroupBy(c => c.Language).ToDictionary(g => g.Key, g => g.Count()),
    SampleCount = samples.Count, Errors = errors,
    Scope = "VoiceMan cutscene sheets only; not all game speech. Missing paths do not prove an NPC has no voice."
}, options), Encoding.UTF8);
Console.WriteLine($"DONE: {characters.Length} speaker tokens, {clips.Count} audio variants, {samples.Count} samples, {errors.Count} errors. {output}");
return 0;

static string Normalize(string name) => new(name.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

static string? SaveAudio(ScdFile.Audio audio, string destination)
{
    if (audio.AudioData.AsSpan().StartsWith("OggS"u8))
    {
        File.WriteAllBytes(destination + ".ogg", audio.AudioData);
        return destination + ".ogg";
    }
    if (audio.AudioDataHeader is not AdpcmWaveFormat format) return null;
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<AdpcmWaveFormat>());
    byte[] header;
    try
    {
        Marshal.StructureToPtr(format, pointer, false);
        header = new byte[18 + format.Size];
        if (header.Length > Marshal.SizeOf<AdpcmWaveFormat>()) throw new InvalidDataException("Unexpected ADPCM format size.");
        Marshal.Copy(pointer, header, 0, header.Length);
    }
    finally { Marshal.FreeHGlobal(pointer); }
    using var writer = new BinaryWriter(File.Create(destination + ".wav"));
    writer.Write("RIFF"u8); writer.Write(4 + 8 + header.Length + 8 + audio.AudioData.Length);
    writer.Write("WAVEfmt "u8); writer.Write(header.Length); writer.Write(header);
    writer.Write("data"u8); writer.Write(audio.AudioData.Length); writer.Write(audio.AudioData);
    return destination + ".wav";
}

record Clip(string SpeakerToken, string[] NameCandidates, string Language, string Sheet, uint RowId,
    string TextKey, string Text, string GamePath, int ScdBytes, uint SampleRate, uint Channels, string? SampleFile);

