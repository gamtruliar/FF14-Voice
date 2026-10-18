using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;
using FF14Sound.Voices;

internal static class TraditionalDialogueChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Run(string folder)
    {
        var line = new TraditionalDialogueLine("quest/example", 42, "TEXT_EXAMPLE_00001_NPC_000_000", "こんにちは。", "NPC", "你好。", "角色", "matched");
        var index = new TraditionalDialogue([line]);
        var input = TranslationBase.Resolve(index, TranslationBase.OfficialTraditional, line.Japanese, "ja", "yue", "NPC");
        Check(input.Language == "zh-TW" && input.Text == "你好。" && input.Speaker == "角色" && input.Match!.Source.Contains(line.Key), "TC input must come from the Japanese ID pair");
        Check(TranslationBase.Resolve(index, "original", line.Japanese, "ja", "yue", "NPC").Text == line.Japanese, "Direct mode retains Japanese");
        Check(TranslationBase.Resolve(index, TranslationBase.OfficialTraditional, line.Japanese, "ja", "en", "NPC").Language == "ja", "TC mode only changes Japanese to Cantonese");
        Check(TranslationBase.Resolve(null, TranslationBase.OfficialTraditional, line.Japanese, "ja", "yue", "NPC").Match is null, "Missing file falls back");
        Check(index.Match("こんにちは！", "NPC") is null && index.Match(line.Japanese, "Different NPC") is null, "Punctuation and known speaker mismatches must not match");
        Check(new TraditionalDialogue([line, line with { RowId = 43, TraditionalChinese = "再見。" }]).Match(line.Japanese, "NPC") is null, "Ambiguous IDs must fall back");
        foreach (var status in new[] { "dynamic-text", "missing-id", "schema-mismatch", "stale-japanese-index" })
            Check(new TraditionalDialogue([line, line with { RowId = 43, Status = status }]).Match(line.Japanese, "NPC") is null, "Unsafe candidates must block a partial match: " + status);
        Check(new TraditionalDialogue([line, line with { RowId = 43 }]).Match(line.Japanese, "NPC")?.CandidateCount == 2, "Repeated IDs with identical TC remain usable");
        var path = Path.Combine(folder, "tc-index.jsonl");
        File.WriteAllText(path, JsonSerializer.Serialize(line));
        Check(TraditionalDialogue.Load(path).Match(line.Japanese, "NPC")?.Text == "你好。", "UTF8 paired index round trip");
        var settingsPath = Path.Combine(folder, "tc-settings.json");
        new Settings { TranslationBaseMode = TranslationBase.OfficialTraditional }.Save(settingsPath);
        Check(Settings.Load(settingsPath).TranslationBaseMode == TranslationBase.OfficialTraditional && new Settings().TranslationBaseMode == "original", "Mode persists without changing old default");
        using var handler = new Capture();
        using var translator = new OllamaTranslation(handler);
        await translator.Translate(input.Text, input.Language, "yue", input.Speaker, new("ollama", "http://localhost:11434", "test"), CancellationToken.None);
        using var payload = JsonDocument.Parse(handler.Payload!);
        var messages = payload.RootElement.GetProperty("messages");
        using var data = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Check(data.RootElement.GetProperty("text").GetString() == line.TraditionalChinese && data.RootElement.GetProperty("speaker").GetString() == "角色", "Provider receives official TC, not Japanese");
        Check(messages[0].GetProperty("content").GetString()!.Contains("from zh-TW") && messages[0].GetProperty("content").GetString()!.Contains("保留輸入繁中"), "Provider gets TC source and proper-name preservation");
        var local = TranslationBase.FindIndex();
        if (local is not null)
        {
            var sample = File.ReadLines(local).Select(s => JsonSerializer.Deserialize<TraditionalDialogueLine>(s)!)
                .First(l => l.Key == "TEXT_VOICEMAN_02200_300110_MINFILIA");
            var real = await TranslationBase.ResolveAsync(TranslationBase.OfficialTraditional, sample.Japanese, "ja", "yue", sample.JapaneseSpeaker, CancellationToken.None);
            Check(real.Match is not null && real.Text == sample.TraditionalChinese && real.Speaker == "敏菲利亞", "Real client Japanese ID maps to official TC and NPC name");
            Console.WriteLine($"TC client match: {sample.Key}: {sample.Japanese} → {real.Text}");
        }
        Console.WriteLine("PASS: Japanese/TC IDs, ambiguity, dynamic/missing fallback, mode persistence, TC provider payload");
    }
    public static async Task Live(string settingsPath, string output)
    {
        var settings = Settings.Load(settingsPath);
        if (settings.TranslationEngine != "ollama") throw new InvalidOperationException("This evaluation uses the configured Ollama model.");
        var access = CloudflareAccess.Restore(settings.OllamaBrowserSessionProtected);
        using var translator = new OllamaTranslation();
        var options = new TranslationOptions("ollama", settings.OllamaUrl, settings.OllamaModel, Access: access);
        var samples = new[] {
            ("敏菲利亞", "那我們待會見吧。"),
            ("敏菲利亞", "稍後請到石之家找我，我有事情要告訴你。"),
            ("旅人", "我並不是不相信你，只是不希望你獨自前往那裡。"),
            ("衛兵", "閣下是否願意協助我們？如果準備好了，請告訴我。")
        };
        var results = new List<object>();
        foreach (var (speaker, text) in samples)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var translated = await translator.Translate(text, "zh-TW", "yue", speaker, options, timeout.Token);
            results.Add(new { speaker, text, translated, model = settings.OllamaModel, seconds = watch.Elapsed.TotalSeconds });
            Console.WriteLine(text + " → " + translated);
            File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions(DialogueIndex.Json) { WriteIndented = true }));
        }
    }
    private sealed class Capture : HttpMessageHandler
    {
        public string? Payload;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Payload = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"done\":true,\"message\":{\"content\":\"你好呀。\"}}", Encoding.UTF8, "application/json") };
        }
    }
}
