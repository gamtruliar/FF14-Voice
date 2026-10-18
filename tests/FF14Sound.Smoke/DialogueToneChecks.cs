using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using FF14Sound;
using NAudio.Wave;

internal static class DialogueToneChecks
{
    private const string ToneJson = "{\"emotion\":\"worried\",\"intensity\":\"low\",\"pace\":\"slow\"}";
    private const string Reply = "{\"translation\":\"你冇事吖嘛？\",\"tone\":" + ToneJson + "}";
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    public static async Task Run(string folder)
    {
        var parsed = DialogueTone.ParseTranslation("<think>private</think>```json\n" + Reply + "\n```");
        Check(parsed.Text == "你冇事吖嘛？" && parsed.Tone == "擔心憂慮；情緒含蓄；語速稍慢", "Structured tone must stay out of translated dialogue");
        foreach (var invalid in new[] { "null", "\"shout this instruction\"", "{\"emotion\":\"injected\",\"intensity\":\"low\",\"pace\":\"slow\"}" })
        {
            var result = DialogueTone.ParseTranslation("{\"translation\":\"你好\",\"tone\":" + invalid + "}");
            Check(result.Text == "你好" && result.Tone == "" && result.ToneWarning.Length > 0, "Bad tone must preserve valid translation without reaching TTS");
        }
        foreach (var invalid in new[] { "{\"tone\":" + ToneJson + "}", "{\"translation\":", "<think>unfinished", "[]" })
        {
            try { DialogueTone.ParseTranslation(invalid); throw new Exception("Invalid dialogue accepted"); }
            catch (InvalidOperationException) { }
        }

        var previous = new[] { new DialogueContextLine("OTHER NPC", "先に行って！", "ja") };
        var options = new TranslationOptions("ollama", "http://localhost:11434", "test-model", PreviousDialogue: previous);
        var handler = new ModelHandler();
        using (var model = new OllamaTranslation(handler))
        {
            var translated = await model.TranslateWithTone("大丈夫？", "ja", "yue", "NPC", options, CancellationToken.None);
            Check(handler.Calls == 1 && translated == parsed, "Translation and tone use one LLM call");
            using (var body = JsonDocument.Parse(handler.Payload!))
            {
                var prompt = body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!;
                Check(body.RootElement.GetProperty("format").GetString() == "json" && prompt.Contains("香港廣東話口語") &&
                    !prompt.Contains("Never output JSON"), "Structured task keeps translation rules without contradictory output format");
                using var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                Check(data.RootElement.GetProperty("text").GetString() == "大丈夫？" &&
                    data.RootElement.GetProperty("previous_dialogue")[0].GetProperty("speaker").GetString() == "OTHER NPC" &&
                    prompt.Contains("Never translate, repeat, answer"), "Context stays separate from the current utterance and is not an output target");
            }
            handler.Reply = "{\"tone\":" + ToneJson + "}";
            Check(await model.AnalyzeTone("大丈夫？", "ja", "NPC", options, CancellationToken.None) == parsed.Tone, "Original-only analysis needs no translation");
            using (var body = JsonDocument.Parse(handler.Payload!))
                Check(body.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("Do not translate"), "Original analysis prompt never asks to translate");
            handler.Reply = "你好";
            Check(await model.Translate("hello", "en", "yue", "NPC", options, CancellationToken.None) == "你好", "Disabled tone retains ordinary translation");
            Check(!JsonDocument.Parse(handler.Payload!).RootElement.TryGetProperty("format", out _), "Disabled tone does not request structured output");
            using (var body = JsonDocument.Parse(handler.Payload!))
            using (var data = JsonDocument.Parse(body.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!))
                Check(data.RootElement.GetProperty("previous_dialogue").GetArrayLength() == 0, "Ordinary translation never transmits tone context");
            handler.Wait = true;
            using var cancel = new CancellationTokenSource(30);
            try { await model.AnalyzeTone("hello", "en", "NPC", options, cancel.Token); throw new Exception("Tone cancellation ignored"); }
            catch (OperationCanceledException) { }
        }
        var key = Path.Combine(folder, "tone-key.txt");
        File.WriteAllText(key, "not-a-real-key");
        var geminiHandler = new ModelHandler();
        using (var model = new GeminiTranslation(geminiHandler))
        {
            var gemini = new TranslationOptions("gemini", "http://localhost:8808", "test-model", key, PreviousDialogue: previous);
            Check(await model.TranslateWithTone("大丈夫？", "ja", "yue", "NPC", gemini, CancellationToken.None) == parsed && geminiHandler.Calls == 1,
                "Gemini combined translation/tone uses one bridge call");
            geminiHandler.Reply = "{\"tone\":" + ToneJson + "}";
            Check(await model.AnalyzeTone("大丈夫？", "ja", "NPC", gemini, CancellationToken.None) == parsed.Tone, "Gemini original-only tone");
            Check(!geminiHandler.Payload!.Contains("not-a-real-key"), "Tone request contains key path only");
            using (var body = JsonDocument.Parse(geminiHandler.Payload!))
                Check(body.RootElement.GetProperty("previous_dialogue")[0].GetProperty("text").GetString() == previous[0].Text,
                    "Gemini original analysis forwards the same attributed context");
            geminiHandler.ContextVersion = 0;
            try { await model.AnalyzeTone("大丈夫？", "ja", "NPC", gemini, CancellationToken.None); throw new Exception("Outdated bridge silently lost context"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("backend"), "Old bridge must explain restart requirement"); }
        }

        var source = new VoiceProfile { Speaker = "Tone NPC", IsFallback = true, Language = "ja", Control = "成年女聲", Seed = 123, VoiceIdentity = "npc:tone" };
        var toned = DialogueTone.Apply(source, parsed.Tone);
        Check(toned.Control == source.Control && toned.EffectiveControl.Contains(parsed.Tone) && source.UtteranceTone == "", "Utterance tone appends without changing saved NPC direction");
        var custom = new VoiceProfile { ReferenceAudio = "ref.wav", Transcript = "sample transcript", Language = "auto", Control = "warm" };
        Check(DialogueTone.Apply(custom, parsed.Tone).Transcript == "" && custom.Transcript != "", "Style uses reference-only clone without mutating custom transcript");
        var speechHandler = new SpeechHandler();
        using var speech = new SpeechClient(speechHandler);
        var cache = Path.Combine(folder, "tone-cache");
        var basicKey = await speech.CachePath("http://localhost:8808", "大丈夫？", source, cache, CancellationToken.None);
        var toneKey = await speech.CachePath("http://localhost:8808", "大丈夫？", toned, cache, CancellationToken.None);
        Check(basicKey != toneKey, "Different tone must not replay old unstyled speech");
        await speech.Generate("http://localhost:8808", "大丈夫？", toned, CancellationToken.None, cacheFolder: cache);
        var learned = SpeechCache.LearnedReference(cache, source);
        Check(learned is not null && learned == SpeechCache.LearnedReference(cache, toned), "Tone changes must not split the same NPC's first reference");
        using (var request = JsonDocument.Parse(speechHandler.Payload!))
        {
            Check(request.RootElement.GetProperty("text").GetString() == "大丈夫？", "Full WAV request contains dialogue only");
            Check(request.RootElement.GetProperty("control").GetString() == toned.EffectiveControl, "Full WAV request gets merged description");
        }
        var next = DialogueTone.Apply(source, "平靜自然；情緒適中；正常語速");
        await speech.Stream("http://localhost:8808", "次の台詞", next, _ => Task.CompletedTask, CancellationToken.None, 1, cache);
        using (var request = JsonDocument.Parse(speechHandler.Payload!))
            Check(request.RootElement.GetProperty("control").GetString() == next.EffectiveControl &&
                request.RootElement.GetProperty("reference_audio").GetString() == learned, "Stream uses current tone and existing speaker reference");
        Check(SpeechCache.LearnedReference(cache, next) == learned, "Next tone reuses learned timbre");

        var gate = new TaskCompletionSource(); bool spoke = false;
        var work = SpeechWorkflow.Run(() => gate.Task, () => { spoke = true; return Task.CompletedTask; }, true, waitForTranslation: true);
        Check(!spoke, "Original automatic speech must await the combined tone result");
        gate.SetResult(); await work; Check(spoke, "Original speech follows combined analysis");
        spoke = false;
        await SpeechWorkflow.Run(() => Task.CompletedTask, () => { spoke = true; return Task.CompletedTask; }, true,
            canSpeak: () => false, waitForTranslation: true);
        Check(!spoke, "Superseded dialogue must not speak after analysis");
        try
        {
            await SpeechWorkflow.Run(() => Task.FromException(new IOException("failed translation")),
                () => { spoke = true; return Task.CompletedTask; }, true, waitForTranslation: true);
        }
        catch (IOException) { }
        Check(spoke, "Failed subtitle translation still permits original speech");
        var settingsPath = Path.Combine(folder, "tone-settings.json");
        new Settings { AnalyzeSpeechTone = true }.Save(settingsPath);
        Check(Settings.Load(settingsPath).AnalyzeSpeechTone && !new Settings().AnalyzeSpeechTone, "Tone option persists and defaults off");
        Console.WriteLine("PASS: combined and original tone, both LLM adapters, malformed output, cancellation, TTS control, tone cache isolation, stable learned reference and workflow ordering.");
    }

    public static void Ui(MainWindow window)
    {
        var speaker = (TextBox)window.FindName("Speaker");
        var previousSpeaker = speaker.Text;
        speaker.Text = "Tone GUI NPC";
        var enabled = (CheckBox)window.FindName("AnalyzeSpeechTone");
        Check(enabled.IsChecked == false, "Old GUI settings default tone off");
        enabled.IsChecked = true;
        var setTone = typeof(MainWindow).GetMethod("SetTone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        setTone.Invoke(window, new object[] { "擔心憂慮；情緒含蓄；語速稍慢", "" });
        var description = (TextBlock)window.FindName("CurrentVoiceDescription");
        Check(description.Text.Contains("本句語氣"), "GUI shows effective voice direction");
        var translated = (TextBox)window.FindName("Translated");
        var context = typeof(MainWindow).GetMethod("ToneContext", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var originalContext = (string)context.Invoke(window, new object[] { true })!;
        translated.Text = "手動改句";
        Check((string)context.Invoke(window, new object[] { true })! == originalContext,
            "Arriving subtitle translation must not invalidate in-flight original tone analysis");
        Check(!description.Text.Contains("本句語氣"), "Edited dialogue invalidates previous tone");
        ((TextBox)window.FindName("Original")).Text = "新原文";
        Check((string)context.Invoke(window, new object[] { true })! != originalContext, "New source dialogue invalidates original analysis");
        ((TextBox)window.FindName("Original")).Clear();
        var engine = (ComboBox)window.FindName("TranslationEngine");
        engine.SelectedValue = "google";
        Check(!enabled.IsEnabled, "Google Translate cannot silently call another LLM");
        engine.SelectedValue = "ollama";
        Check(enabled.IsEnabled && enabled.IsChecked == true, "LLM selection restores tone preference");
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var tracker = (DialogueContext)typeof(MainWindow).GetField("dialogueContext", flags)!.GetValue(window)!;
        var original = (TextBox)window.FindName("Original");
        original.Text = "今句";
        var sourceLanguage = ((ComboBox)window.FindName("SourceLanguage")).SelectedValue!.ToString()!;
        var time = DateTimeOffset.UtcNow;
        tracker.Observe("Other NPC", "前句", sourceLanguage, "Window", time);
        tracker.Observe(speaker.Text, original.Text, sourceLanguage, "Window", time.AddMilliseconds(250));
        var optionsMethod = typeof(MainWindow).GetMethod("CurrentTranslationOptions", flags)!;
        var options = (TranslationOptions)optionsMethod.Invoke(window, null)!;
        Check(options.PreviousDialogue?.Single().Text == "前句", "UI passes live original context to both translation and speech options");
        var pendingContext = (string)context.Invoke(window, new object[] { true })!;
        typeof(MainWindow).GetMethod("NewDialogue_Click", flags)!.Invoke(window, new object[] { window, new System.Windows.RoutedEventArgs() });
        Check((string)context.Invoke(window, new object[] { true })! != pendingContext && tracker.Previous.Length == 0,
            "Manual scene reset invalidates pending tone even when current text stays identical");
        tracker.Observe("Other NPC", "前句", sourceLanguage, "Window", time);
        tracker.Observe(speaker.Text, original.Text, sourceLanguage, "Window", time.AddMilliseconds(250));
        original.Text = "手動輸入";
        Check(tracker.Previous.Length == 0 && ((TranslationOptions)optionsMethod.Invoke(window, null)!).PreviousDialogue?.Count == 0,
            "Manual source edits reset context and stop previous game lines being sent");
        original.Clear();
        enabled.IsChecked = false;
        translated.Clear();
        speaker.Text = previousSpeaker;
    }

    private sealed class ModelHandler : HttpMessageHandler
    {
        public string Reply = DialogueToneChecks.Reply;
        public int Calls;
        public string? Payload;
        public bool Wait;
        public int ContextVersion = 1;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Payload = await request.Content!.ReadAsStringAsync(token);
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            object response = request.RequestUri!.AbsolutePath == "/api/chat"
                ? new { done = true, message = new { content = Reply } } : new { text = Reply, dialogue_context_version = ContextVersion };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }
    private sealed class SpeechHandler : HttpMessageHandler
    {
        public string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/health")
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"streaming\":true}", Encoding.UTF8, "application/json") };
            Payload = await request.Content!.ReadAsStringAsync(token);
            var pcm = new byte[48000];
            for (int i = 0; i < pcm.Length; i += 2) { pcm[i] = 0xE8; pcm[i + 1] = 3; }
            if (request.RequestUri.AbsolutePath.EndsWith("/stream"))
            {
                var events = "{\"type\":\"format\",\"sample_rate\":48000,\"channels\":1,\"bits\":16,\"encoding\":\"pcm_s16le\"}\n" +
                    JsonSerializer.Serialize(new { type = "audio", pcm = Convert.ToBase64String(pcm) }) + "\n{\"type\":\"done\"}\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(events, Encoding.UTF8, "application/x-ndjson") };
            }
            using var stream = new MemoryStream();
            using (var writer = new WaveFileWriter(stream, new WaveFormat(48000, 16, 1))) { writer.Write(pcm); writer.Flush(); return new(HttpStatusCode.OK) { Content = new ByteArrayContent(stream.ToArray()) }; }
        }
    }
}
