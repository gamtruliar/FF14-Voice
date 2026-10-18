using System.IO;
using FF14Sound.Voices;

namespace FF14Sound;

public sealed record VoiceAssignment(VoiceProfile Profile, string Voice, string Basis, string ReferenceLanguage)
{
    public string Label => $"{Voice} · {Basis}" + (ReferenceLanguage.Length > 0 ? $" · {ReferenceLanguage}" : "");
}

public sealed class GameVoiceLibrary
{
    public VoiceResolver? Resolver { get; private set; }
    public string CatalogPath { get; private set; } = "";
    public NativeDialogue? NativeAudio { get; private set; }
    public bool HasNativeAudio(string speaker, string original, string language, string? source = null, string? speakerToken = null)
        => (NativeAudio ?? throw new InvalidOperationException("未載入遊戲原錄音索引；請先建立 native-voiced-lines.json。"))
            .Contains(speaker, original, language, source, speakerToken);
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
        NativeAudio = null;
        var catalog = VoiceCatalog.Load(file);
        var resolver = new VoiceResolver(catalog, Path.GetDirectoryName(Path.GetFullPath(file))!);
        Resolver = resolver; CatalogPath = Path.GetFullPath(file);
        var nativePath = Path.Combine(Path.GetDirectoryName(CatalogPath)!, NativeDialogue.FileName);
        NativeAudio = File.Exists(nativePath) ? NativeDialogue.Load(nativePath) : null;
    }
    public VoiceProfile Resolve(string speaker, string targetLanguage, Settings settings, string? fallbackDescription = null, string? npcIdentity = null)
        => ResolveAssignment(speaker, targetLanguage, settings, fallbackDescription, npcIdentity).Profile;

    public VoiceAssignment ResolveAssignment(string speaker, string targetLanguage, Settings settings, string? fallbackDescription = null, string? npcIdentity = null)
    {
        // Explicit user voice profiles override automatic game voice matching.
        var custom = settings.Voices.FirstOrDefault(v => VoiceResolver.Normalize(v.Speaker) == VoiceResolver.Normalize(speaker));
        if (custom is not null)
        {
            custom.Seed ??= VoiceSeed.ForSpeaker(custom.Speaker);
            return new(VoiceSeed.ApplyOverride(settings, custom), Path.GetFileName(custom.ReferenceAudio), "自訂聲音覆蓋", "");
        }
        var match = Resolver?.Resolve(speaker, settings.ReferenceLanguage, settings.SpeakerAliases);
        if (match is null)
        {
            var description = string.IsNullOrWhiteSpace(fallbackDescription) ? settings.DefaultVoiceDescription : fallbackDescription;
            var fallback = new VoiceProfile
            {
                Speaker = speaker, Language = SpeechLanguageFor(targetLanguage),
                Control = description, Seed = VoiceSeed.ForNpc(speaker, npcIdentity),
                VoiceIdentity = string.IsNullOrWhiteSpace(npcIdentity) || npcIdentity.StartsWith("live:", StringComparison.Ordinal)
                    ? "name:" + VoiceResolver.Normalize(speaker) : "npc:" + npcIdentity,
                IsFallback = true
            };
            return new(VoiceSeed.ApplyOverride(settings, fallback), "default", "文字生成（無 clone / 無 reference）", "");
        }
        var profile = new VoiceProfile
        {
            Speaker = speaker, ReferenceAudio = match.FullPath, Language = SpeechLanguageFor(targetLanguage),
            // Translation crosses languages: use timbre cloning, not reference-text continuation.
            Transcript = "", Seed = VoiceSeed.For("voice:" + match.Voice.Token), VoiceIdentity = "voice:" + match.Voice.Token
        };
        return new(VoiceSeed.ApplyOverride(settings, profile), match.Voice.Token, match.MatchedBy == "user alias" ? "手動配對原聲" : "自動配對原聲", match.Reference.Language);
    }

    public void DescribeRow(NpcVoiceEntry row, Settings settings)
    {
        var name = settings.SourceLanguage switch { "ja" => row.Japanese, "de" => row.German, "fr" => row.French, _ => row.English };
        row.MappingSpeaker = name;
        row.MappingBasis = "目前設定／遊戲文字語言：" + settings.SourceLanguage;
        row.MappedReference = "";
        if (string.IsNullOrWhiteSpace(name)) { row.VoiceMapping = "此文字語言無名稱"; return; }
        try
        {
            var assignment = ResolveAssignment(name, settings.SpeechMode == "original" ? settings.SourceLanguage : settings.TargetLanguage, settings, row.VoiceDescription, row.Key);
            row.VoiceMapping = assignment.Label;
            row.MappedReference = assignment.Profile.ReferenceAudio;
        }
        catch (Exception ex) { row.VoiceMapping = "無法使用：" + ex.Message; }
    }
    public static string SpeechLanguageFor(string language) => language switch
    {
        "zh-TW" or "zh-CN" or "zh" => "zh", "yue" => "yue", "en" or "ja" or "de" or "fr" or "ko" => language,
        _ => throw new InvalidOperationException("未設定翻譯語言嘅 TTS 對應：" + language)
    };
}
