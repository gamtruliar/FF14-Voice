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
    public bool IsGroup { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string UtteranceTone { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveControl => UtteranceTone.Length == 0 ? Control
        : string.Join("；", new[] { Control.Trim(), "本句語氣：" + UtteranceTone }.Where(s => s.Length > 0));
    public uint? Seed { get; set; }
    public string VoiceIdentity { get; set; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveVoiceIdentity => VoiceIdentity.Length > 0 ? VoiceIdentity : "name:" + FF14Sound.Voices.VoiceResolver.Normalize(Speaker);
    [System.Text.Json.Serialization.JsonIgnore]
    public uint EffectiveSeed => Seed ?? VoiceSeed.ForSpeaker(Speaker);
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsFallback { get; set; }
    public override string ToString() => Speaker;
}

public sealed class Settings
{
    public string Server { get; set; } = "http://127.0.0.1:8808";
    public string SourceLanguage { get; set; } = "en";
    public string TargetLanguage { get; set; } = "zh-TW";
    public string TranslationEngine { get; set; } = "ollama";
    public string TranslationBaseMode { get; set; } = "original";
    public string OllamaUrl { get; set; } = "http://localhost:11434";
    public string OllamaModel { get; set; } = "qwen3.5:9b";
    public string OllamaBrowserSessionProtected { get; set; } = "";
    public string GeminiModel { get; set; } = "gemini-3.8-flash";
    public string GeminiKeyFile { get; set; } = "APIKey/qqDragon.txt";
    public bool AutoSpeak { get; set; } = true;
    public bool AnalyzeSpeechTone { get; set; }
    public bool StreamSpeech { get; set; } = true;
    public double SpeechVolumePercent { get; set; } = 100;
    public double SpeechSpeed { get; set; } = 1;
    public PlaybackLevelSettings PlaybackLevels { get; set; } = new();
    public string SpeechMode { get; set; } = "translated";
    public bool MainTopmost { get; set; }
    public SubtitleSettings Subtitles { get; set; } = new();
    public string ReferenceLanguage { get; set; } = "ja";
    public string CatalogPath { get; set; } = "";
    public string SpeechCacheFolder { get; set; } = "";
    public string DefaultVoiceDescription { get; set; } = "自然、清晰、親切，中等語速，平穩敘述。";
    public Dictionary<string, string> SpeakerAliases { get; set; } = new();
    public Dictionary<string, uint> VoiceSeedOverrides { get; set; } = new();
    public List<VoiceProfile> Voices { get; set; } = [];
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FF14Sound");
    public static Settings Load(string? path = null)
    {
        var file = path ?? Path.Combine(Folder, "settings.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file)) ?? new() : new();
    }
    public void Save(string? path = null)
    {
        var file = path ?? Path.Combine(Folder, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(file + ".tmp", file, true);
    }
}

public sealed class SubtitleSettings
{
    public bool Enabled { get; set; }
    public bool Topmost { get; set; } = true;
    public string ContentMode { get; set; } = "translated";
    public double FontSize { get; set; } = 28;
    public double BackgroundOpacity { get; set; } = 0.8;
    public bool Compact { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 960;
    public double Height { get; set; } = 230;
}
