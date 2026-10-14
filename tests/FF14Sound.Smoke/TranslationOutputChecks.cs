using System.Text.Json;
using FF14Sound;

internal static class TranslationOutputChecks
{
    public static void Run()
    {
        const string expected = "偏偏啲海賊出身嘅人，\n竟然連嗰份恩情都忘晒。\n真係對唔住橙果啊！";
        var plain = expected.Replace("\n", "\\r");
        var json = JsonSerializer.Serialize(new { speaker = "ビルドレーク", text = plain });
        foreach (var input in new[] { expected, plain, expected.Replace("\n", "\r\n"), expected.Replace("\n", "\\r\\n"),
            expected.Replace("\n", "\\n"), json, "```json\n" + json + "\n```", JsonSerializer.Serialize(json),
            "<think>reasoning</think>" + json, JsonSerializer.Serialize(new { translation = expected }) })
            if (TranslationOutput.Normalize(input) != expected) throw new Exception("Model output must become dialogue only, with real line breaks");
        foreach (var plainText in new[] { "普通對白。", "佢話：『你好。』", "{魔法}係咩？", "\"Hello,\" she said.", @"C:\FF14\game", @"\\n" })
            if (TranslationOutput.Normalize(plainText) != plainText) throw new Exception("Plain dialogue must be preserved");
        foreach (var invalid in new[] { "", "<think>unfinished", "{\"speaker\":\"NPC\",\"text\":\"unfinished", "{\"speaker\":\"NPC\"}",
            "{\"text\":null}", "{\"text\":\"\"}", "{\"text\":\"one\",\"translation\":\"two\"}", "[{\"text\":\"one\"}]",
            "```json\n" + json, JsonSerializer.Serialize(new { text = "<think>unfinished" }), "\\r" })
        {
            try { TranslationOutput.Normalize(invalid); throw new Exception("Invalid model output accepted: " + invalid); }
            catch (InvalidOperationException) { }
        }
        Console.WriteLine("PASS: translation JSON/speaker removal, fences, escaped line breaks, plain dialogue and malformed output rejection.");
    }
}
