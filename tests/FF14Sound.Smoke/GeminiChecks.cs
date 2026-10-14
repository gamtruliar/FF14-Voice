using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;

internal static class GeminiChecks
{
    public static async Task Run(string folder)
    {
        var keyFile = Path.Combine(folder, "gemini-test-key.txt");
        await File.WriteAllTextAsync(keyFile, "must-never-enter-http-body");
        var handler = new Handler();
        using var translator = new GeminiTranslation(handler);
        var options = new TranslationOptions("gemini", "http://localhost:8808", "gemini-3.8-flash", keyFile);
        var result = await translator.Translate("こんにちは", "ja", "yue", "NPC", options, CancellationToken.None);
        if (result != "你好呀。" || handler.Payload!.Contains("must-never-enter-http-body")) throw new Exception("Gemini bridge leaked key or lost response");
        using var body = JsonDocument.Parse(handler.Payload);
        if (body.RootElement.GetProperty("key_file").GetString() != keyFile || body.RootElement.GetProperty("model").GetString() != options.Model) throw new Exception("Gemini key path/model missing");
        if (!body.RootElement.GetProperty("system_prompt").GetString()!.Contains("香港廣東話口語")) throw new Exception("Gemini Cantonese prompt missing");
        await translator.Translate("スチールヴィジルへ行く。", "ja", "yue", "ウジェノア", options, CancellationToken.None);
        using (var terms = JsonDocument.Parse(handler.Payload!))
            if (!terms.RootElement.GetProperty("system_prompt").GetString()!.Contains("鋼衛塔")) throw new Exception("Gemini SDK prompt lacks matched glossary");
        handler.Translation = "```json\n{\"speaker\":\"NPC\",\"text\":\"你好。\\r再見。\"}\n```";
        if (await translator.Translate("text", "ja", "yue", "NPC", options, CancellationToken.None) != "你好。\n再見。") throw new Exception("Gemini must clean JSON/newlines before UI/TTS");
        handler.Wait = true;
        using var cancel = new CancellationTokenSource(50);
        try { await translator.Translate("text", "ja", "yue", "", options, cancel.Token); throw new Exception("Gemini HTTP cancellation ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Gemini local bridge routing, key path only, Cantonese prompt, cancellation.");
    }
    public static async Task Live(string folder)
    {
        using var translator = new Translator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var options = new TranslationOptions("gemini", "http://localhost:8808", "gemini-3.8-flash", "APIKey/qqDragon.txt");
        var samples = new[] {
            ("ウジェノア", "異端者め、スチールヴィジルが再建されぬよう、\nドラゴン族と協力し、見張っていたな。\nクソ……奴らの好きにはさせるものか！"),
            ("旅人", "私は彼を助けたのではない。彼が私を助けてくれたんだ。")
        };
        var results = new List<object>();
        foreach (var (speaker, text) in samples)
        {
            var watch = Stopwatch.StartNew();
            var translation = await translator.Translate(text, "ja", "yue", timeout.Token, options, speaker);
            results.Add(new { speaker, text, translation, seconds = watch.Elapsed.TotalSeconds, model = options.Model, sdk = "google-genai" });
            Console.WriteLine($"Gemini live translation completed in {watch.Elapsed.TotalSeconds:F2}s.");
            await File.WriteAllTextAsync(Path.Combine(folder, "gemini-translation-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Payload;
        public bool Wait;
        public string Translation = "你好呀。";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (!request.RequestUri!.IsLoopback || request.Headers.Contains("x-goog-api-key")) throw new Exception("Credentials sent outside Python SDK");
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            Payload = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { text = Translation }), Encoding.UTF8, "application/json") };
        }
    }
}
