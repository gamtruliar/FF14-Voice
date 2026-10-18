using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace FF14Sound;

/// <summary>Local bridge only; Google credentials and google-genai live in the Python backend.</summary>
public sealed class GeminiTranslation : IDisposable
{
    private readonly HttpClient client;
    public GeminiTranslation(HttpMessageHandler? handler = null)
    {
        client = handler is null ? new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) : new(handler);
        client.Timeout = TimeSpan.FromSeconds(100);
    }
    private async Task<JsonElement> Send(string server, string path, object payload, CancellationToken token)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new InvalidOperationException("Gemini SDK bridge 必須係本機 HTTP backend。");
        using var response = await client.PostAsJsonAsync(new Uri(uri, path), payload, token);
        var result = await response.Content.ReadFromJsonAsync<JsonElement>(token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = result.TryGetProperty("detail", out var message) && message.ValueKind == JsonValueKind.String ? message.GetString() : "本機 Gemini bridge 未就緒，請重啟 backend。";
            throw new InvalidOperationException($"Gemini HTTP {(int)response.StatusCode}：{detail}");
        }
        return result;
    }
    public async Task<string[]> Models(string server, string keyFile, CancellationToken token)
    {
        var result = await Send(server, "/api/gemini/models", new { key_file = ApiKeyFile.Resolve(keyFile) }, token);
        return result.GetProperty("models").EnumerateArray().Select(m => m.GetString()!).ToArray();
    }
    public async Task<string> Translate(string text, string from, string to, string speaker, TranslationOptions options, CancellationToken token) =>
        TranslationOutput.Normalize(await Complete(text, speaker, OllamaTranslation.SystemPrompt(from, to, text, speaker), options, token));

    public async Task<DialogueTranslation> TranslateWithTone(string text, string from, string to, string speaker, TranslationOptions options, CancellationToken token) =>
        DialogueTone.ParseTranslation(await Complete(text, speaker, OllamaTranslation.SystemPrompt(from, to, text, speaker, true), options, token, true));

    public async Task<string> AnalyzeTone(string text, string language, string speaker, TranslationOptions options, CancellationToken token) =>
        DialogueTone.ParseAnalysis(await Complete(text, speaker, DialogueTone.AnalysisPrompt(language), options, token, true));

    private async Task<string> Complete(string text, string speaker, string prompt, TranslationOptions options, CancellationToken token, bool withTone = false)
    {
        var result = await Send(options.Endpoint, "/api/gemini/translate", new
        {
            key_file = ApiKeyFile.Resolve(options.KeyFile), model = options.Model.Trim(), text, speaker,
            system_prompt = prompt, previous_dialogue = withTone ? options.PreviousDialogue ?? [] : []
        }, token);
        token.ThrowIfCancellationRequested();
        if (withTone && options.PreviousDialogue is { Count: > 0 } &&
            (!result.TryGetProperty("dialogue_context_version", out var contextVersion) || contextVersion.GetInt32() != 1))
            throw new InvalidOperationException("請重啟新版 VoxCPM backend，先可以將前幾句交畀 Gemini 分析。");
        var translation = result.GetProperty("text").GetString();
        return translation ?? "";
    }
    public void Dispose() => client.Dispose();
}
