using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Data.Files;
using Lumina.Data.Parsing.Scd;
using Lumina.Excel;

namespace FF14Sound.Voices;

public static class CatalogBuilder
{
    public static async Task<VoiceCatalog> Build(string sqpack, string manifest, string output, IProgress<string>? progress,
        CancellationToken token, string ffmpeg = "ffmpeg")
    {
        sqpack = Path.GetFullPath(sqpack); output = Path.GetFullPath(output);
        if (output.Equals(sqpack, StringComparison.OrdinalIgnoreCase) || output.StartsWith(sqpack + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Voice library must be outside the game archive directory.");
        var clips = JsonSerializer.Deserialize<List<GameClip>>(await File.ReadAllTextAsync(manifest, token))
            ?? throw new InvalidDataException("Empty voice manifest.");
        using var game = new GameData(sqpack);
        var npcNames = ReadNames(game);
        var voices = new List<GameVoice>();
        var errors = new List<string>();
        Directory.CreateDirectory(output);
        var groups = clips.GroupBy(c => c.SpeakerToken).OrderBy(g => g.Key).ToArray();
        foreach (var group in groups)
        {
            token.ThrowIfCancellationRequested();
            if (group.Key.Length == 0 || group.Key.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
                throw new InvalidDataException("Invalid speaker token in manifest.");
            var references = new List<GameReference>();
            foreach (var locale in group.GroupBy(c => c.Language))
            {
                if (locale.Key is not ("en" or "ja" or "de" or "fr")) continue;
                // Prefer a complete 5–12 s utterance from the newest available chapter.
                // Shorter references remain available for NPCs with very little dialogue.
                var choices = new List<(GameClip Clip, double Duration)>();
                foreach (var clip in locale.DistinctBy(c => c.GamePath).OrderByDescending(c => c.Sheet))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var audio = game.GetFile<ScdFile>(clip.GamePath)?.GetAudio(0);
                        if (audio?.AudioDataHeader is not AdpcmWaveFormat fmt || fmt.BlockAlign == 0 || fmt.SamplesPerSec == 0) continue;
                        var seconds = (double)(audio.AudioData.Length / fmt.BlockAlign) * fmt.SamplesPerBlock / fmt.SamplesPerSec;
                        if (seconds <= 0.1) continue;
                        choices.Add((clip, seconds));
                        if (seconds is >= 5 and <= 12) break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add(clip.GamePath + ": " + ex.Message); }
                }
                var best = choices.FirstOrDefault(c => c.Duration is >= 5 and <= 12);
                if (best.Clip is null) best = choices.OrderBy(c => Math.Abs(c.Duration - 8)).FirstOrDefault();
                if (best.Clip is null) { errors.Add(group.Key + "/" + locale.Key + ": no decodable reference"); continue; }
                var relative = Path.Combine("references", group.Key, locale.Key + ".wav");
                var destination = Path.Combine(output, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                var temporary = destination + ".source.wav";
                var staging = destination + ".new.wav";
                try
                {
                    WriteAdpcm(game.GetFile<ScdFile>(best.Clip.GamePath)!.GetAudio(0), temporary);
                    var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                    foreach (var arg in new[] { "-v", "error", "-nostdin", "-y", "-i", temporary, "-t", "12", "-ac", "1", "-ar", "24000", "-c:a", "pcm_s16le", staging }) start.ArgumentList.Add(arg);
                    using var process = Process.Start(start) ?? throw new IOException("Cannot start ffmpeg.");
                    var stderr = process.StandardError.ReadToEndAsync(token);
                    try { await process.WaitForExitAsync(token); }
                    catch { if (!process.HasExited) process.Kill(true); throw; }
                    var message = await stderr;
                    if (process.ExitCode != 0 || new FileInfo(staging).Length < 100) throw new IOException("Audio conversion failed: " + message);
                    File.Move(staging, destination, true);
                    references.Add(new(locale.Key, relative, Math.Min(12, best.Duration),
                        best.Duration <= 12 ? best.Clip.Text : "", best.Clip.GamePath, best.Clip.Sheet));
                }
                finally { File.Delete(temporary); File.Delete(staging); }
            }
            var aliases = ResolveNames(group.Key, npcNames);
            voices.Add(new(group.Key, aliases, references.ToArray()));
            progress?.Report($"{voices.Count}/{groups.Length} {group.Key}: {references.Count} 個語言版本");
        }
        var catalog = new VoiceCatalog(1, sqpack, DateTimeOffset.Now, voices.ToArray(), errors.ToArray());
        catalog.Save(Path.Combine(output, "catalog.json"));
        NpcVoiceIndex.Build(sqpack, catalog, output);
        return catalog;
    }

    internal static Dictionary<uint, Dictionary<string, string>> ReadNames(GameData game, string sheetName = "ENpcResident")
    {
        var result = new Dictionary<uint, Dictionary<string, string>>();
        // The installed client decides which localized sheets are available.
        foreach (var language in Enum.GetValues<Language>().Where(l => l != Language.None))
        {
            try
            {
                var raw = game.Excel.GetRawSheet(sheetName, language);
                if (raw.Language != language) continue;
                foreach (var row in new ExcelSheet<RawRow>(raw))
                {
                    var name = row.ReadColumn(0)?.ToString() ?? "";
                    if (name.Length == 0) continue;
                    if (!result.TryGetValue(row.RowId, out var names)) result[row.RowId] = names = [];
                    names[language.ToString()] = name;
                }
            }
            catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { }
        }
        return result;
    }

    private static string[] ResolveNames(string token, Dictionary<uint, Dictionary<string, string>> names)
    {
        var key = VoiceResolver.Normalize(token);
        var english = names.Where(p => p.Value.ContainsKey("English")).ToArray();
        var matched = english.Where(p => VoiceResolver.Normalize(p.Value["English"]) == key).ToArray();
        if (matched.Length == 0)
        {
            var candidates = english.Where(p => VoiceResolver.Normalize(p.Value["English"].Split(' ')[0]) == key).ToArray();
            // Full names sharing a given name are ambiguous; keep the token for explicit mapping.
            if (candidates.Select(p => VoiceResolver.Normalize(p.Value["English"])).Distinct().Count() == 1) matched = candidates;
        }
        return matched.SelectMany(p => p.Value.Values).Distinct().ToArray();
    }

    internal static void WriteAdpcm(ScdFile.Audio audio, string destination)
    {
        var format = (AdpcmWaveFormat)audio.AudioDataHeader!;
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AdpcmWaveFormat>());
        byte[] header;
        try
        {
            header = new byte[18 + format.Size];
            if (header.Length > Marshal.SizeOf<AdpcmWaveFormat>()) throw new InvalidDataException("Unexpected ADPCM header.");
            Marshal.StructureToPtr(format, memory, false); Marshal.Copy(memory, header, 0, header.Length);
        }
        finally { Marshal.FreeHGlobal(memory); }
        using var writer = new BinaryWriter(File.Create(destination));
        writer.Write("RIFF"u8); writer.Write(4 + 8 + header.Length + 8 + audio.AudioData.Length);
        writer.Write("WAVEfmt "u8); writer.Write(header.Length); writer.Write(header);
        writer.Write("data"u8); writer.Write(audio.AudioData.Length); writer.Write(audio.AudioData);
    }
}
