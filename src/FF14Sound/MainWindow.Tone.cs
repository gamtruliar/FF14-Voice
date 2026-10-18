using System.Text.Json;
using System.Windows.Controls;

namespace FF14Sound;

public partial class MainWindow
{
    private string? toneContext;
    private string utteranceTone = "";
    private readonly DialogueContext dialogueContext = new();
    private bool applyingGameDialogue;
    private bool manualDialogueContext;
    private DialogueContextLine[] CurrentPreviousDialogue => dialogueContext.PreviousFor(
        Speaker.Text, Original.Text, SourceLanguage.SelectedValue?.ToString() ?? "en");
    private bool ToneEnabled => AnalyzeSpeechTone.IsChecked == true &&
        TranslationEngine.SelectedValue?.ToString() is "ollama" or "gemini";

    private TranslationOptions CurrentTranslationOptions()
    {
        var engine = TranslationEngine.SelectedValue?.ToString() ?? "ollama";
        var options = engine == "gemini"
            ? new(engine, Server.Text.Trim(), GeminiModel.Text.Trim(), GeminiKeyFile.Text.Trim())
            : new TranslationOptions(engine, OllamaUrl.Text.Trim(), OllamaModel.Text.Trim(), Access: CurrentOllamaAccess());
        return options with { PreviousDialogue = ToneEnabled ? CurrentPreviousDialogue : [] };
    }

    // Bind a result to the exact dialogue and LLM settings, including manual text edits.
    private string ToneContext(bool originalOnly = false) => JsonSerializer.Serialize(new[]
    {
        Original.Text.Trim(), originalOnly ? "" : Translated.Text.Trim(), Speaker.Text.Trim(),
        SourceLanguage.SelectedValue?.ToString(), TargetLanguage.SelectedValue?.ToString(),
        SpeechMode.SelectedValue?.ToString(), TranslationBaseMode.SelectedValue?.ToString(),
        TranslationEngine.SelectedValue?.ToString(), OllamaUrl.Text.Trim(), OllamaModel.Text.Trim(),
        Server.Text.Trim(), GeminiModel.Text.Trim(), GeminiKeyFile.Text.Trim(), dialogueContext.Revision.ToString()
    });
    private string CurrentTone => ToneEnabled && toneContext == ToneContext() ? utteranceTone : "";

    private void InitializeToneControls()
    {
        AnalyzeSpeechTone.IsChecked = settings.AnalyzeSpeechTone;
        AnalyzeSpeechTone.Checked += (_, _) => { Cancel(); InvalidateTone(); QueueSettingsSave(); };
        AnalyzeSpeechTone.Unchecked += (_, _) => { Cancel(); InvalidateTone(); QueueSettingsSave(); };
        foreach (var box in new[] { Original, Speaker })
            box.TextChanged += (_, _) =>
            {
                if (!applyingGameDialogue) { manualDialogueContext = true; ResetDialogueContext(); }
            };
        SourceLanguage.SelectionChanged += (_, _) => ResetDialogueContext();
        foreach (var box in new[] { Original, Translated, Speaker, OllamaUrl, Server, GeminiKeyFile })
            box.TextChanged += (_, _) => InvalidateTone();
        foreach (var combo in new[] { SourceLanguage, TargetLanguage, SpeechMode, TranslationBaseMode, TranslationEngine })
            combo.SelectionChanged += (_, _) => InvalidateTone();
        foreach (var model in new[] { OllamaModel, GeminiModel })
            model.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent,
                new TextChangedEventHandler((_, _) => InvalidateTone()));
        UpdateToneControls();
    }

    private void UpdateToneControls()
    {
        AnalyzeSpeechTone.IsEnabled = TranslationEngine.SelectedValue?.ToString() is "ollama" or "gemini";
        if (toneContext != ToneContext()) InvalidateTone();
    }

    private void InvalidateTone()
    {
        toneContext = null;
        utteranceTone = "";
        ToneStatus.Text = !AnalyzeSpeechTone.IsEnabled ? "語氣分析需要 Ollama 或 Gemini"
            : ToneEnabled ? "語氣：等候分析當句" : "語氣分析已關閉";
        if (settingsReady) RefreshVoiceMapping();
        UpdateDialogueContextStatus();
    }

    private void UpdateDialogueContextStatus()
    {
        var previous = CurrentPreviousDialogue;
        DialogueContextStatus.Text = ToneEnabled
            ? $"語境：前 {previous.Length} 句（關框未滿 2 分鐘保留）"
            : "語境：語氣分析關閉時不傳送";
        DialogueContextStatus.ToolTip = previous.Length == 0 ? "冇前文；手動輸入只分析當句。"
            : string.Join("\n", previous.Select(line => $"[{line.Language}] {line.Speaker}：{line.Text}"));
    }

    private void ResetDialogueContext()
    {
        dialogueContext.Reset();
        InvalidateTone();
    }

    private void NewDialogue_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Cancel(); ResetDialogueContext();
        // Keep the currently displayed game line as the first line of the new conversation.
        if (game is not null && lastLine == Speaker.Text + "\n" + Original.Text)
        {
            manualDialogueContext = false;
            dialogueContext.Observe(Speaker.Text, Original.Text, SourceLanguage.SelectedValue?.ToString() ?? "en",
                game.DialogueSurface, DateTimeOffset.UtcNow);
        }
        UpdateDialogueContextStatus();
    }

    private void SetTone(string tone, string warning = "")
    {
        toneContext = ToneContext();
        utteranceTone = tone;
        ToneStatus.Text = warning.Length > 0 ? warning : "本句語氣：" + tone;
        RefreshVoiceMapping();
    }

    private async Task<string> ToneForSpeech(SpeechSelection selection, string name, CancellationToken token, int version)
    {
        if (!ToneEnabled) return "";
        if (toneContext == ToneContext()) return utteranceTone;
        // An arriving subtitle translation must not cancel explicit original-language speech.
        var context = ToneContext(selection.Original);
        ToneStatus.Text = "分析本句語氣中…";
        string tone = "", warning = "";
        try { tone = await translator.AnalyzeTone(selection.Text, selection.Language, name, CurrentTranslationOptions(), token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { warning = "語氣分析失敗，今句沿用原本描述：" + ex.Message; }
        token.ThrowIfCancellationRequested();
        if (version != speechGeneration || context != ToneContext(selection.Original) || !ToneEnabled)
            throw new OperationCanceledException(token);
        SetTone(tone, warning);
        return tone;
    }
}
