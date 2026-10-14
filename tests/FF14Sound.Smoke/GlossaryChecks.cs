using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FF14Sound;

internal static class GlossaryChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static GlossaryEntry Term(string source, string target) => new(source, "test", new() { ["ja"] = source, ["en"] = source, ["zh-TW"] = target });
    public static void Run(string folder)
    {
        var customFile = Path.Combine(folder, "glossary-overrides.json");
        var glossary = new TranslationGlossary(new[] { Term("Steel Vigil", "鋼衛塔"), Term("Vigil", "警戒"), Term("Al", "阿爾"), Term("Shared", "甲"), Term("Shared", "乙"), Term("NPC", "角色") }, customFile);
        var selected = glossary.Select("Steel Vigil belongs to Alice.", "NPC", "en", "yue");
        Check(selected.Matches.Length == 2 && selected.Matches.Any(m => m.Target == "鋼衛塔") && selected.Matches.Any(m => m.Target == "角色"), "Longest term and exact speaker included, substring names excluded");
        Check(glossary.Select("Shared", "", "en", "yue").Matches.Length == 0, "Conflicting labels are not guessed");
        File.WriteAllText(customFile, JsonSerializer.Serialize(new[] { Term("Steel Vigil", "自訂塔名") }));
        Check(glossary.Select("Steel Vigil", "", "en", "yue").Matches.Single().Target == "自訂塔名", "User term overrides built-in and Cantonese inherits traditional names");
        var explicitYue = Term("Steel Vigil", "繁中塔名"); explicitYue.Names["yue"] = "粵語塔名";
        File.WriteAllText(customFile, JsonSerializer.Serialize(new[] { explicitYue })); File.SetLastWriteTimeUtc(customFile, DateTime.UtcNow.AddSeconds(2));
        Check(glossary.Select("Steel Vigil", "", "en", "yue").Matches.Single().Target == "粵語塔名", "Edited override reloads and explicit Cantonese has priority");
        File.Delete(customFile);
        Check(glossary.Select("Steel Vigil", "", "en", "yue").Matches.Single().Target == "鋼衛塔", "Removing overrides restores built-in terms");
        var many = new TranslationGlossary(Enumerable.Range(0, 100).Select(i => Term("Name" + i, new string('甲', 90))));
        var limited = many.Select(string.Join(" ", Enumerable.Range(0, 100).Select(i => "Name" + i)), "", "en", "yue");
        Check(limited.Matches.Length <= 20 && limited.Prompt.Length < 3000, "Glossary stays inside LLM context budget");
        var watch = Stopwatch.StartNew();
        var real = TranslationGlossary.Context("スチールヴィジルの再建を阻止する。", "ウジェノア", "ja", "yue");
        Check(real.Matches.Any(m => m.Source == "スチールヴィジル" && m.Target == "鋼衛塔") && real.Matches.Any(m => m.Source == "ウジェノア" && m.Target == "厄讓努瓦"), "Real client-aligned location and NPC translations");
        Check(!real.Prompt.Contains("リムサ・ロミンサ"), "Unrelated glossary is not sent");
        var lore = TranslationGlossary.Context("暁の血盟に戻ろう。", "", "ja", "yue");
        Check(lore.Matches.Any(m => m.Target == "拂曉血盟"), "Core organization term included");
        var saucer = TranslationGlossary.Context("ゴールドソーサー協賛イベント", "", "ja", "yue");
        Check(saucer.Matches.Any(m => m.Source == "ゴールドソーサー" && m.Target == "金碟游樂場"), "Untranslated placeholder rows must not hide the localized term");
        Check(OllamaTranslation.SystemPrompt("ja", "yue", "スチールヴィジル").Length < 8000, "SDK bridge prompt limit respected");
        Console.WriteLine($"PASS: FF14 glossary {TranslationGlossary.Default.Value.Count:N0} entries, exact/longest/ambiguous matches, bounded prompts, overrides; cold load+lookup {watch.Elapsed.TotalSeconds:F2}s.");
    }
}
