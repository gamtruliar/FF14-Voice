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
        Speaker = voice.Speaker, ReferenceAudio = voice.ReferenceAudio, Control = voice.Control, UtteranceTone = voice.UtteranceTone,
        Language = Language, Transcript = "", IsFallback = voice.IsFallback, IsGroup = voice.IsGroup, Seed = voice.EffectiveSeed, VoiceIdentity = voice.VoiceIdentity
    };
}

public static class SpeechWorkflow
{
    public static async Task Run(Func<Task> translate, Func<Task>? speak, bool original, bool nativeAudio = false, Func<bool>? canSpeak = null,
        bool waitForTranslation = false)
    {
        if (nativeAudio || speak is null) { await translate(); return; }
        Task SpeakIfCurrent() => canSpeak?.Invoke() == false ? Task.CompletedTask : speak();
        if (original && !waitForTranslation) await Task.WhenAll(translate(), SpeakIfCurrent());
        else if (original)
        {
            // A failed subtitle translation still permits original speech and its own tone analysis.
            try { await translate(); }
            finally { await SpeakIfCurrent(); }
        }
        else { await translate(); await SpeakIfCurrent(); }
    }
}
