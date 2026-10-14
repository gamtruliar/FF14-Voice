using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;

internal static class OllamaChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run()
    {
        var handler = new Handler();
        using var translator = new OllamaTranslation(handler);
        var options = new TranslationOptions("ollama", "http://localhost:11434", "qwen3.5:9b");
        var result = await translator.Translate("こんにちは", "ja", "yue", "NPC", options, CancellationToken.None);
        Check(result == "你好呀。", "Reasoning must never be spoken");
        using var request = JsonDocument.Parse(handler.Payload!);
        var sent = request.RootElement;
        Check(sent.GetProperty("model").GetString() == options.Model && !sent.GetProperty("think").GetBoolean() && !sent.GetProperty("stream").GetBoolean(), "Selected local model and no thinking");
        Check(sent.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("香港廣東話口語"), "Cantonese prompt is explicit");
        Check(sent.GetProperty("messages")[1].GetProperty("content").GetString()!.Contains("こんにちは"), "Model must see Japanese text, not literal Unicode escape sequences");
        var data = JsonDocument.Parse(sent.GetProperty("messages")[1].GetProperty("content").GetString()!);
        Check(data.RootElement.GetProperty("speaker").GetString() == "NPC" && data.RootElement.GetProperty("text").GetString() == "こんにちは", "Speaker/source are dialogue data");
        await translator.Translate("スチールヴィジルへ行く。", "ja", "yue", "ウジェノア", options, CancellationToken.None);
        using (var terms = JsonDocument.Parse(handler.Payload!))
            Check(terms.RootElement.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("鋼衛塔"), "Ollama request includes matched FF14 glossary");
        handler.Response = JsonSerializer.Serialize(new { done = true, message = new { content = "{\"speaker\":\"NPC\",\"text\":\"你好。\\r再見。\"}" } });
        Check(await translator.Translate("text", "ja", "yue", "NPC", options, CancellationToken.None) == "你好。\n再見。", "Ollama cleans JSON and escaped newlines before returning to UI/TTS");
        foreach (var response in new[] {
            "{\"done\":true,\"done_reason\":\"length\",\"message\":{\"content\":\"半句\"}}",
            "{\"done\":true,\"message\":{\"content\":\"<think>unfinished\"}}",
            "{\"done\":false,\"message\":{\"content\":\"partial\"}}" })
        {
            handler.Response = response;
            var rejected = false;
            try { await translator.Translate("text", "ja", "yue", "", options, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Incomplete/reasoning-only translation rejected");
        }
        foreach (var address in new[] { "https://example.com", "https://example.com/", "http://192.168.1.10:11434", "https://example.com/ollama", "https://example.com/ollama/" })
        {
            var expectedBase = address.TrimEnd('/');
            handler.Response = "{\"models\":[{\"name\":\"test-model\"}]}";
            var models = await translator.Models(address, CancellationToken.None);
            Check(models.SequenceEqual(new[] { "test-model" }) && handler.Address == expectedBase + "/api/tags", "Custom URL model list, including proxy path");
            handler.Response = "{\"done\":true,\"message\":{\"content\":\"你好\"}}";
            Check(await translator.Translate("text", "ja", "yue", "", options with { Endpoint = address }, CancellationToken.None) == "你好" && handler.Address == expectedBase + "/api/chat", "Custom URL translation");
        }
        var calls = handler.Calls;
        foreach (var address in new[] { "ftp://example.com", "not a URL", "https://user:password@example.com", "https://example.com/?token=secret", "https://example.com/#fragment" })
        {
            try { await translator.Models(address, CancellationToken.None); throw new Exception("Invalid URL accepted"); }
            catch (InvalidOperationException) { }
        }
        Check(handler.Calls == calls, "Invalid endpoint rejected before HTTP");
        handler.Status = HttpStatusCode.Found;
        handler.Location = new Uri("https://test.cloudflareaccess.com/cdn-cgi/access/login/test");
        foreach (var models in new[] { true, false })
        {
            try
            {
                if (models) await translator.Models("https://example.com", CancellationToken.None);
                else await translator.Translate("text", "ja", "yue", "", options, CancellationToken.None);
                throw new Exception("Login redirect accepted");
            }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("Cloudflare Access"), "Actionable login error"); }
        }
        Check(handler.Calls == calls + 2, "Login redirects are not followed");
        handler.Status = HttpStatusCode.OK;
        handler.Location = null;
        var access = CloudflareAccess.FromToken("https://example.com", TestToken());
        handler.Response = "{\"models\":[{\"name\":\"test-model\"}]}";
        await translator.Models("https://example.com", CancellationToken.None, access);
        Check(handler.SessionToken == access.Token && handler.ClientId is null && handler.Secret is null, "Browser session header on model list; no service token headers");
        handler.Response = "{\"done\":true,\"message\":{\"content\":\"你好\"}}";
        await translator.Translate("text", "ja", "yue", "", options with { Endpoint = "https://example.com", Access = access }, CancellationToken.None);
        Check(handler.SessionToken == access.Token && !handler.Payload!.Contains(access.Token), "Browser session on translation, never in body");
        await translator.Translate("text", "ja", "yue", "", options, CancellationToken.None);
        Check(handler.SessionToken is null && handler.ClientId is null && handler.Secret is null, "No credentials retained on following unauthenticated request");
        calls = handler.Calls;
        foreach (var pair in new[] { ("http://example.com", access), ("https://other.example.com", access), ("https://example.com:8443", access),
            ("https://example.com", CloudflareAccess.FromToken("https://example.com", TestToken(-60))),
            ("https://example.com/other", CloudflareAccess.FromToken("https://example.com/ollama", TestToken())) })
        {
            try { await translator.Models(pair.Item1, CancellationToken.None, pair.Item2); throw new Exception("Unsafe auth accepted"); }
            catch (InvalidOperationException) { }
        }
        Check(handler.Calls == calls, "Expired/wrong host/port/path/plaintext auth rejected before HTTP");
        handler.Status = HttpStatusCode.Forbidden;
        handler.Response = access.Token;
        try { await translator.Models("https://example.com", CancellationToken.None, access); throw new Exception("Auth failure accepted"); }
        catch (InvalidOperationException ex) { Check(!ex.ToString().Contains(access.Token) && ex.Message.Contains("網頁登入"), "Auth error does not expose echoed session"); }
        handler.Status = HttpStatusCode.Found;
        handler.Location = new Uri("https://other.example.com/login");
        calls = handler.Calls;
        try { await translator.Models("https://example.com", CancellationToken.None, access); throw new Exception("Authenticated redirect accepted"); }
        catch (InvalidOperationException) { }
        Check(handler.Calls == calls + 1, "No forwarding of credentials to redirect target");
        Check(!(options with { Access = access }).ToString().Contains(access.Token), "Diagnostic options redact session");
        var encrypted = access.Protect();
        var restored = CloudflareAccess.Restore(encrypted)!;
        Check(!encrypted.Contains(access.Token) && restored.Token == access.Token && restored.BaseUrl == access.BaseUrl && restored.ExpiresAt == access.ExpiresAt, "Windows user encryption preserves session scope and expiry");
        Check(restored.Matches("https://example.com/") && !restored.Matches("https://other.example.com/"), "GUI URL binding");
        foreach (var invalid in new[] { "bad", "a.b.c\r\nInjected: header", "a.e30.c" })
        {
            try { CloudflareAccess.FromToken("https://example.com", invalid); throw new Exception("Invalid token accepted"); }
            catch (InvalidOperationException ex) { Check(ex.Message.Contains("未回傳有效登入狀態"), "Invalid token rejected without echo"); }
        }
        var start = CloudflareBrowserLogin.StartInfo("cloudflared.exe", "https://example.com/ollama");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardOutput && start.RedirectStandardError &&
            start.ArgumentList.SequenceEqual(new[] { "access", "login", "--no-verbose", "https://example.com/ollama/" }), "Browser login uses private pipes and separate arguments");
        try { SecretStorage.Unprotect("invalid-ciphertext"); throw new Exception("Invalid stored secret accepted"); }
        catch (InvalidOperationException) { }
        handler.Wait = true;
        using var cancel = new CancellationTokenSource(50);
        try { await translator.Translate("text", "ja", "yue", "", options, cancel.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Ollama model/prompt, cancellation, custom URLs, Cloudflare browser sessions/scope/expiry/errors and Windows encryption.");
    }
    public static string TestToken(int expiresInSeconds = 3600)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { exp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresInSeconds }))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "eyJhbGciOiJSUzI1NiJ9." + payload + ".testSignature";
    }
    public static async Task Live(string folder)
    {
        using var translator = new Translator();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var options = new TranslationOptions("ollama", "http://localhost:11434", "qwen3.5:9b");
        var samples = new[] {
            ("ウジェノア", "異端者め、スチールヴィジルが再建されぬよう、\nドラゴン族と協力し、見張っていたな。\nクソ……奴らの好きにはさせるものか！"),
            ("イベント案内窓口", "ゴールドソーサー協賛イベントを開催します。\nぜひ、参加してくださいね！"),
            ("旅人", "私は彼を助けたのではない。彼が私を助けてくれたんだ。")
        };
        var results = new List<object>();
        foreach (var (speaker, text) in samples)
        {
            var watch = Stopwatch.StartNew();
            var translation = await translator.Translate(text, "ja", "yue", timeout.Token, options, speaker);
            Check(!string.IsNullOrWhiteSpace(translation) && translation != text, "Live Ollama translation");
            var record = new { speaker, text, translation, seconds = watch.Elapsed.TotalSeconds, model = options.Model };
            results.Add(record); Console.WriteLine(JsonSerializer.Serialize(record));
            await File.WriteAllTextAsync(Path.Combine(folder, "ollama-translation-results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public string? Payload;
        public string? Address;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public Uri? Location;
        public string? ClientId;
        public string? Secret;
        public string? SessionToken;
        public int Calls;
        public bool Wait;
        public string Response = "{\"done\":true,\"done_reason\":\"stop\",\"message\":{\"content\":\"<think>private analysis</think>你好呀。\"}}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Wait) await Task.Delay(Timeout.Infinite, token);
            Address = request.RequestUri!.AbsoluteUri;
            ClientId = request.Headers.TryGetValues("CF-Access-Client-Id", out var ids) ? ids.Single() : null;
            Secret = request.Headers.TryGetValues("CF-Access-Client-Secret", out var secrets) ? secrets.Single() : null;
            SessionToken = request.Headers.TryGetValues("cf-access-token", out var tokens) ? tokens.Single() : null;
            Payload = request.Content is null ? null : await request.Content.ReadAsStringAsync(token);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
            response.Headers.Location = Location;
            return response;
        }
    }
}
