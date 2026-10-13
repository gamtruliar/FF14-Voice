using System.IO;
using FF14Sound.Voices;

namespace FF14Sound;

public sealed class GameVoiceLibrary
{
    public VoiceResolver? Resolver { get; private set; }
    public string CatalogPath { get; private set; } = "";
    public static string FindCatalog()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var file = Path.Combine(folder.FullName, "data", "game-voices", "catalog.json");
            if (File.Exists(file)) return file;
        }
        return "";
    }
    public void Load(string file)
    {
        var catalog = VoiceCatalog.Load(file);
        var resolver = new VoiceResolver(catalog, Path.GetDirectoryName(Path.GetFullPath(file))!);
        Resolver = resolver; CatalogPath = Path.GetFullPath(file);
    }
    public VoiceProfile Resolve(string speaker, string targetLanguage, Settings settings)
    {
        // Explicit user voice profiles override automatic game voice matching.
        var custom = settings.Voices.FirstOrDefault(v => VoiceResolver.Normalize(v.Speaker) == VoiceResolver.Normalize(speaker));
        if (custom is not null) return custom;
        var match = Resolver?.Resolve(speaker, settings.ReferenceLanguage, settings.SpeakerAliases);
        if (match is null) throw new InvalidOperationException($"未能配對「{speaker}」嘅 {settings.ReferenceLanguage} 原聲。請喺遊戲聲音庫揀角色，按「配對目前 NPC」。");
        return new VoiceProfile
        {
            Speaker = speaker, ReferenceAudio = match.FullPath, Language = SpeechLanguageFor(targetLanguage),
            // Translation crosses languages: use timbre cloning, not reference-text continuation.
            Transcript = ""
        };
    }
    public static string SpeechLanguageFor(string language) => language switch
    {
        "zh-TW" or "zh-CN" => "zh", "yue" => "yue", "en" or "ja" or "de" or "fr" or "ko" => language,
        _ => throw new InvalidOperationException("未設定翻譯語言嘅 TTS 對應：" + language)
    };
}
