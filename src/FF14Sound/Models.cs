using System.IO;
using System.Text.Json;

namespace FF14Sound;

public sealed class VoiceProfile
{
    public string Speaker { get; set; } = "";
    public string ReferenceAudio { get; set; } = "";
    public string Transcript { get; set; } = "";
    public string Language { get; set; } = "zh";
    public string Control { get; set; } = "";
    public override string ToString() => Speaker;
}

public sealed class Settings
{
    public string Server { get; set; } = "http://127.0.0.1:8808";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "zh-TW";
    public bool AutoSpeak { get; set; } = true;
    public string ReferenceLanguage { get; set; } = "ja";
    public string CatalogPath { get; set; } = "";
    public Dictionary<string, string> SpeakerAliases { get; set; } = new();
    public List<VoiceProfile> Voices { get; set; } = [];
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FF14Sound");
    public static Settings Load()
    {
        var file = Path.Combine(Folder, "settings.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new() : new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var file = Path.Combine(Folder, "settings.json");
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}
