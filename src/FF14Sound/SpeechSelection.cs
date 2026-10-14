namespace FF14Sound;

public sealed record SpeechSelection(string Text, string Language, bool Original)
{
    public static SpeechSelection Choose(string mode, string original, string translated, string sourceLanguage, string targetLanguage)
    {
        var useOriginal = mode == "original";
        var text = (useOriginal ? original : translated).Trim();
        if (text.Length == 0) throw new InvalidOperationException(useOriginal ? "請先輸入原文。" : "請先翻譯或輸入配音文字。");
        return new(text, GameVoiceLibrary.SpeechLanguageFor(useOriginal ? sourceLanguage : targetLanguage), useOriginal);
    }

    public VoiceProfile Apply(VoiceProfile voice) => !Original ? voice : new VoiceProfile
    {
        Speaker = voice.Speaker, ReferenceAudio = voice.ReferenceAudio, Control = voice.Control,
        Language = Language, Transcript = "", IsFallback = voice.IsFallback, Seed = voice.EffectiveSeed, VoiceIdentity = voice.VoiceIdentity
    };
}

public static class SpeechWorkflow
{
    public static async Task Run(Func<Task> translate, Func<Task>? speak, bool original, bool nativeAudio = false)
    {
        if (nativeAudio || speak is null) { await translate(); return; }
        if (original) await Task.WhenAll(translate(), speak());
        else { await translate(); await speak(); }
    }
}
