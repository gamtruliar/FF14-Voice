using System.Text;
using System.Text.Json;

namespace FF14Sound.Voices;

public sealed record GameClip(string SpeakerToken, string[] NameCandidates, string Language, string Sheet, uint RowId,
    string TextKey, string Text, string GamePath, int ScdBytes, uint SampleRate, uint Channels, string? SampleFile);
public sealed record GameReference(string Language, string RelativePath, double Seconds, string Transcript, string GamePath, string Sheet);
public sealed record GameVoice(string Token, string[] Names, GameReference[] References)
{
    public string Label => Names.FirstOrDefault() is { } name ? $"{name} · {Token}" : $"{Token} · 需配對名稱";
    public override string ToString() => Label;
}
public sealed record VoiceCatalog(int Version, string Sqpack, DateTimeOffset BuiltAt, GameVoice[] Voices, string[] Errors)
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public static VoiceCatalog Load(string path)
    {
        var result = JsonSerializer.Deserialize<VoiceCatalog>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty voice catalog.");
        if (result.Version != 1) throw new InvalidDataException("Unsupported voice catalog version.");
        return result;
    }
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, JsonOptions));
        File.Move(path + ".tmp", path, true);
    }
}

public sealed record VoiceMatch(GameVoice Voice, GameReference Reference, string FullPath, string MatchedBy);

/// <summary>Exact normalized aliases only. Ambiguous names never pick an arbitrary actor.</summary>
public sealed class VoiceResolver
{
    public VoiceCatalog Catalog { get; }
    public string DirectoryPath { get; }
    private readonly Dictionary<string, GameVoice[]> names;
    public VoiceResolver(VoiceCatalog catalog, string directory)
    {
        Catalog = catalog; DirectoryPath = Path.GetFullPath(directory);
        names = catalog.Voices.SelectMany(v => v.Names.Append(v.Token).Select(n => (Name: Normalize(n), Voice: v)))
            .Where(p => p.Name.Length > 0).GroupBy(p => p.Name)
            .ToDictionary(g => g.Key, g => g.Select(p => p.Voice).DistinctBy(v => v.Token).ToArray());
    }
    public VoiceMatch? Resolve(string speaker, string language, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var key = Normalize(speaker);
        if (key.Length == 0) return null;
        GameVoice? voice;
        var mapped = overrides?.FirstOrDefault(p => Normalize(p.Key) == key).Value;
        if (!string.IsNullOrEmpty(mapped)) voice = Catalog.Voices.FirstOrDefault(v => v.Token == mapped);
        else voice = names.TryGetValue(key, out var matches) && matches.Length == 1 ? matches[0] : null;
        // Never silently swap a Japanese voice for an English actor.
        var reference = voice?.References.FirstOrDefault(r => r.Language == language);
        if (voice is null || reference is null) return null;
        var path = Path.GetFullPath(Path.Combine(DirectoryPath, reference.RelativePath));
        if (!path.StartsWith(DirectoryPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Voice reference path is outside its catalog.");
        return File.Exists(path) ? new(voice, reference, path, mapped is null ? "catalog" : "user alias") : null;
    }
    public static string Normalize(string text) => new(text.Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
