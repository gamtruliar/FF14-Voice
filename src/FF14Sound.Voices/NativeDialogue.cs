using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace FF14Sound.Voices;

public sealed record NativeVoiceLine(string Token, string Language, string Source, string Text, bool Static);
public sealed record NativeVoiceIndex(int Version, Dictionary<string, string[]> Names, NativeVoiceLine[] Lines);

/// <summary>Verified native audio belongs to the game. Voice selection overrides never change this policy.</summary>
public sealed class NativeDialogue
{
    public const string FileName = "native-voiced-lines.json";
    private readonly HashSet<(string Language, string Source)> sources;
    private readonly HashSet<(string Language, string Token, string Text)> lines;
    private readonly Dictionary<string, string> names;
    public NativeDialogue(NativeVoiceIndex index)
    {
        if (index.Version != 1) throw new InvalidDataException("Unsupported native voice index.");
        sources = index.Lines.Select(l => (l.Language, l.Source)).ToHashSet();
        lines = index.Lines.Where(l => l.Static && !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (l.Language, l.Token, NormalizeText(l.Text))).ToHashSet();
        names = index.Names.SelectMany(p => p.Value.Append(p.Key).Select(n => (Name: VoiceResolver.Normalize(n), Token: p.Key)))
            .Where(p => p.Name.Length > 0).GroupBy(p => p.Name)
            .Where(g => g.Select(p => p.Token).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Token);
    }
    public static NativeDialogue Load(string path) => new(JsonSerializer.Deserialize<NativeVoiceIndex>(File.ReadAllText(path))
        ?? throw new InvalidDataException("Empty native voice index."));
    // Keep punctuation and case: broad fuzzy matching can suppress a different, unvoiced line.
    private static string NormalizeText(string text) => Regex.Replace(text, @"\s+", " ").Trim();
    public bool Contains(string speaker, string original, string language, string? source = null, string? speakerToken = null)
    {
        if (source is not null && sources.Contains((language, source))) return true;
        var text = NormalizeText(original);
        if (!string.IsNullOrEmpty(speakerToken) && lines.Contains((language, speakerToken, text))) return true;
        return names.TryGetValue(VoiceResolver.Normalize(speaker), out var actor) && lines.Contains((language, actor, text));
    }

    public static NativeVoiceIndex Build(string catalogPath, string clipsPath)
    {
        var catalog = VoiceCatalog.Load(catalogPath);
        var clips = JsonSerializer.Deserialize<GameClip[]>(File.ReadAllText(clipsPath)) ?? throw new InvalidDataException("Empty clip scan.");
        using var game = new GameData(catalog.Sqpack);
        var result = new List<NativeVoiceLine>();
        foreach (var group in clips.Where(c => c.ScdBytes > 0 && c.Channels > 0 && c.SampleRate > 0).GroupBy(c => (c.Sheet, c.Language)))
        {
            var language = group.Key.Language switch { "ja" => Language.Japanese, "en" => Language.English, "de" => Language.German, "fr" => Language.French, _ => throw new InvalidDataException("Unsupported clip language.") };
            var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet(group.Key.Sheet, language));
            foreach (var clip in group)
            {
                var row = sheet.GetRow(clip.RowId);
                if (row.ReadStringColumn(0).ExtractText() != clip.TextKey) throw new InvalidDataException("Client changed; rescan native audio first.");
                var value = row.ReadStringColumn(1);
                // Do not match macro-stripped fragments against live dialogue.
                result.Add(new(clip.SpeakerToken, clip.Language, clip.Sheet + ":" + clip.RowId, value.ExtractText(), DialogueIndex.IsStatic(value)));
            }
        }
        var index = new NativeVoiceIndex(1, catalog.Voices.ToDictionary(v => v.Token, v => v.Names), result.Distinct().ToArray());
        var path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, FileName);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(index, DialogueIndex.Json));
        File.Move(path + ".tmp", path, true);
        return index;
    }
}
