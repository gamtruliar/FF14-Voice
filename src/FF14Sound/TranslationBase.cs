using System.IO;
using FF14Sound.Voices;

namespace FF14Sound;

public sealed record TranslationInput(string Text, string Language, string Speaker, string Note, TraditionalDialogueMatch? Match = null);

public static class TranslationBase
{
    public const string OfficialTraditional = "official-tc";
    private static readonly Lazy<Task<TraditionalDialogue?>> Default = new(() => Task.Run(() =>
    {
        var path = FindIndex();
        return path is null ? null : TraditionalDialogue.Load(path);
    }));
    public static string? FindIndex()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            var path = Path.Combine(folder.FullName, "data", TraditionalDialogue.FileName);
            if (File.Exists(path)) return path;
        }
        return null;
    }
    public static TranslationInput Resolve(TraditionalDialogue? index, string mode, string text, string from, string to, string speaker)
    {
        if (mode != OfficialTraditional) return new(text, from, speaker, "原文直接翻譯");
        if (from != "ja" || to != "yue") return new(text, from, speaker, "繁中模式只適用於日文 → 廣東話；本句用原文");
        var match = index?.Match(text, speaker);
        return match is null
            ? new(text, from, speaker, index is null ? "未有日文／繁中索引；本句用日文" : "未有唯一可用繁中配對；本句用日文")
            : new(match.Text, "zh-TW", match.Speaker, "官方繁中 → 廣東話", match);
    }
    public static async Task<TranslationInput> ResolveAsync(string mode, string text, string from, string to, string speaker, CancellationToken token)
    {
        if (mode != OfficialTraditional || from != "ja" || to != "yue") return Resolve(null, mode, text, from, to, speaker);
        try { return Resolve(await Default.Value.WaitAsync(token), mode, text, from, to, speaker); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException or UnauthorizedAccessException)
        { return new(text, from, speaker, "繁中索引讀取失敗；本句用日文"); }
    }
}
