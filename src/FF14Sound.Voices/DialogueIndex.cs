using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using Lumina.Text.Payloads;
using Lumina.Text.ReadOnly;

namespace FF14Sound.Voices;

public sealed record DialogueLine(string Sheet, uint RowId, string Key, string Language, string Text,
    string MacroText, string SpeakerToken, string Speaker, string SpeakerBasis, string Status);
public sealed record DialogueScanReport(string Sqpack, string Language, DateTimeOffset ScannedAt, int Sheets,
    int TextTables, int Rows, Dictionary<string, int> StatusCounts, string[] Errors, string[] OtherSchemas);

public static class DialogueIndex
{
    public static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly Regex DialogueKey = new(@"^TEXT_.+?_\d{5}_(?<actor>[A-Z][A-Z0-9_]*)_\d{1,5}_\d{1,5}$", RegexOptions.Compiled);
    private static readonly Regex SimpleKey = new(@"^TEXT_[A-Z0-9]+_(?<actor>[A-Z][A-Z0-9]*)_\d{1,5}_\d{1,5}$", RegexOptions.Compiled);
    private static readonly Regex VoiceKey = new(@"^TEXT_VOICEMAN_\d+_\d+_(?<actor>[A-Z][A-Z0-9]*)$", RegexOptions.Compiled);
    private static readonly HashSet<string> NonActors = new(StringComparer.OrdinalIgnoreCase)
        { "SYSTEM", "SYS", "SEQ", "TODO", "QST", "QUEST", "CHOICE", "SELECT", "SELECTION", "MENU", "TITLE", "NAME", "ITEM", "LOG", "TUTORIAL", "HOWTO", "UI" };
    public static string ActorToken(string key)
    {
        var match = VoiceKey.Match(key);
        if (!match.Success) match = DialogueKey.Match(key);
        if (!match.Success) match = SimpleKey.Match(key);
        var actor = match.Success ? match.Groups["actor"].Value : "";
        return NonActors.Contains(actor) || Regex.IsMatch(actor, @"^[AQ]\d+$") ? "" : actor;
    }
    public static bool IsStatic(ReadOnlySeString text) => text.All(p => p.Type == ReadOnlySePayloadType.Text ||
        p.Type == ReadOnlySePayloadType.Macro && p.MacroCode is MacroCode.NewLine or MacroCode.Color or MacroCode.EdgeColor
        or MacroCode.ShadowColor or MacroCode.ColorType or MacroCode.EdgeColorType or MacroCode.Bold or MacroCode.Italic
        or MacroCode.SoftHyphen or MacroCode.NonBreakingSpace or MacroCode.Hyphen or MacroCode.Wait or MacroCode.Sound);

    public static DialogueScanReport Build(string sqpack, string destination, string language, IProgress<string>? progress, CancellationToken token)
    {
        var lang = language switch { "ja" => Language.Japanese, "en" => Language.English, "de" => Language.German, "fr" => Language.French, _ => throw new ArgumentException("Unsupported client language") };
        var output = Path.GetFullPath(destination);
        var input = Path.GetFullPath(sqpack).TrimEnd(Path.DirectorySeparatorChar);
        if (output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Output must be outside game SqPack.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var game = new GameData(input);
        var names = CatalogBuilder.ReadNames(game);
        var english = names.Where(p => p.Value.ContainsKey("English")).GroupBy(p => VoiceResolver.Normalize(p.Value["English"]))
            .ToDictionary(g => g.Key, g => g.Select(p => p.Key).ToArray());
        var questActors = ReadQuestActors(game);
        var links = ReadNpcLinks(game);
        var battleNames = CatalogBuilder.ReadNames(game, "BNpcName");
        var statuses = new Dictionary<string, int>(); var errors = new List<string>(); var other = new List<string>();
        int tables = 0, rows = 0, visited = 0;
        var sheets = game.Excel.SheetNames.Order(StringComparer.Ordinal).ToArray();
        try
        {
            using (var writer = new StreamWriter(output + ".tmp", false, new UTF8Encoding(false)))
            foreach (var name in sheets)
            {
                token.ThrowIfCancellationRequested();
                if (++visited % 250 == 0) progress?.Report($"掃描資料表 {visited:N0}/{sheets.Length:N0} · 已索引 {rows:N0} 行");
                try
                {
                    var raw = game.Excel.GetRawSheet(name, lang);
                    if (raw.Language != lang) continue;
                    if (name is "NpcYell" or "Balloon" or "DefaultTalk" or "ContentTalk" or "PublicContentTextData")
                    {
                        var special = new ExcelSheet<RawRow>(raw);
                        if (name == "NpcYell" && !special.Columns.Any(c => c.Offset == 12 && c.Type.ToString() == "UInt32")) throw new InvalidDataException("NpcYell name layout changed.");
                        foreach (var row in special)
                        foreach (var column in special.Columns.Select((c, i) => (c, i)).Where(p => p.c.Type.ToString() == "String"))
                        {
                            var value = row.ReadStringColumn(column.i);
                            uint[] ids = name == "NpcYell" ? [row.ReadUInt32(12)] : links.GetValueOrDefault(name + "/" + row.RowId, []);
                            var speakers = ids.SelectMany(id => new[] { names.GetValueOrDefault(id), name == "NpcYell" ? battleNames.GetValueOrDefault(id) : null })
                                .Where(n => n is not null).Select(n => n!.GetValueOrDefault(lang.ToString(), "")).Where(n => n.Length > 0).Distinct().ToArray();
                            if (speakers.Length == 0) speakers = [""];
                            foreach (var speaker in speakers)
                            {
                                var text = value.ExtractText().Trim();
                                var status = Classify(text, value, speaker);
                                if (name == "PublicContentTextData" || name == "ContentTalk") status = text.Length == 0 ? "empty" : "unresolved-speaker";
                                var line = new DialogueLine(name, row.RowId, $"{name}:{row.RowId}:{column.i}", language, text, value.ToMacroString(), "", speaker,
                                    speaker.Length > 0 ? name == "NpcYell" ? "NpcYell Name ID" : "ENpcBase dialogue link" : "", status);
                                writer.WriteLine(JsonSerializer.Serialize(line, Json)); statuses[status] = statuses.GetValueOrDefault(status) + 1; rows++;
                            }
                        }
                        tables++; continue;
                    }
                    if (raw.Columns.Count < 2 || raw.Columns[0].Type.ToString() != "String" || raw.Columns[1].Type.ToString() != "String")
                    { other.Add(name); continue; }
                    var sheet = new ExcelSheet<RawRow>(raw);
                    var found = false;
                    foreach (var row in sheet)
                    {
                        token.ThrowIfCancellationRequested();
                        var key = row.ReadStringColumn(0).ExtractText();
                        if (!key.StartsWith("TEXT_", StringComparison.Ordinal)) continue;
                        found = true;
                        var value = row.ReadStringColumn(1);
                        var text = value.ExtractText().Trim();
                        var actor = ActorToken(key);
                        var speaker = ""; var basis = "";
                        var quest = name.Split('/').Last().ToUpperInvariant();
                        if (actor.Length > 0)
                        {
                            uint[] ids = [];
                            if (questActors.TryGetValue(quest + "/" + actor, out var scoped)) { ids = scoped; basis = "Quest actor ID"; }
                            else if (english.TryGetValue(VoiceResolver.Normalize(actor), out var exact)) { ids = exact; basis = "ENpcResident exact English name"; }
                            var localized = ids.Where(names.ContainsKey).Select(id => names[id].GetValueOrDefault(lang.ToString(), "")).Where(s => s.Length > 0).Distinct().ToArray();
                            if (localized.Length == 1) speaker = localized[0]; else basis = "";
                        }
                        var status = text.Length == 0 ? "empty" : actor.Length == 0 ? "non-dialogue-or-unclassified" : Classify(text, value, speaker);
                        var line = new DialogueLine(name, row.RowId, key, language, text, value.ToMacroString(), actor, speaker, basis, status);
                        writer.WriteLine(JsonSerializer.Serialize(line, Json));
                        statuses[status] = statuses.GetValueOrDefault(status) + 1; rows++;
                    }
                    if (found) tables++; else other.Add(name);
                }
                catch (Lumina.Excel.Exceptions.UnsupportedLanguageException) { }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { errors.Add(name + ": " + ex.Message); }
            }
            File.Move(output + ".tmp", output, true);
        }
        finally { File.Delete(output + ".tmp"); }
        var report = new DialogueScanReport(input, language, DateTimeOffset.Now, sheets.Length, tables, rows, statuses, errors.ToArray(), other.ToArray());
        File.WriteAllText(output + ".report.json", JsonSerializer.Serialize(report, new JsonSerializerOptions(Json) { WriteIndented = true }));
        return report;
    }

    private static string Classify(string text, ReadOnlySeString value, string speaker) => text.Length == 0 ? "empty"
        : !IsStatic(value) ? "dynamic-text" : text.Length > 1800 ? "long-text" : !text.Any(char.IsLetter) ? "non-speech"
        : text.Contains("テスト用") || text.StartsWith("NpcYellテスト") || text.Contains("★未使用") || text.Contains("★削除予定") || text.StartsWith("_rsv_")
            ? "test-data" : speaker.Length == 0 ? "unresolved-speaker" : "ready";

    private static Dictionary<string, uint[]> ReadNpcLinks(GameData game)
    {
        // Pinned EXDSchema e773c41: ENpcData[32] at offsets 0..124, Balloon at offset 200.
        var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("ENpcBase", Language.None));
        if (sheet.Columns.Count != 110 || Enumerable.Range(0, 32).Any(i => !sheet.Columns.Any(c => c.Offset == i * 4 && c.Type.ToString() == "UInt32")) ||
            !sheet.Columns.Any(c => c.Offset == 200 && c.Type.ToString() == "UInt16")) throw new InvalidDataException("ENpcBase dialogue link layout changed.");
        return sheet.SelectMany(r => Enumerable.Range(0, 32).Select(i => (Key: "DefaultTalk/" + r.ReadUInt32((nuint)(i * 4)), Npc: r.RowId))
            .Append((Key: "Balloon/" + r.ReadUInt16(200), Npc: r.RowId))).Where(p => !p.Key.EndsWith("/0"))
            .GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.Select(p => p.Npc).Distinct().ToArray());
    }

    private static Dictionary<string, uint[]> ReadQuestActors(GameData game)
    {
        var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("Quest", Language.English));
        if (sheet.Columns.Count < 150 || Enumerable.Range(50, 50).Any(i => sheet.Columns[i].Type.ToString() != "String") ||
            Enumerable.Range(100, 50).Any(i => sheet.Columns[i].Type.ToString() != "UInt32")) throw new InvalidDataException("Quest actor schema changed.");
        return sheet.SelectMany(q => Enumerable.Range(0, 50).Select(i => (Quest: q.ReadStringColumn(1).ExtractText().ToUpperInvariant(),
            Actor: q.ReadStringColumn(50 + i).ExtractText(), Id: Convert.ToUInt32(q.ReadColumn(100 + i)))))
            .Where(a => a.Id != 0 && (a.Actor.StartsWith("ACTOR_") || a.Actor.StartsWith("LOC_ACTOR_")))
            .GroupBy(a => a.Quest + "/" + (a.Actor.StartsWith("LOC_") ? a.Actor[10..] : a.Actor[6..]))
            .ToDictionary(g => g.Key, g => g.Select(a => a.Id).Distinct().ToArray());
    }

    public static IEnumerable<DialogueLine> Read(string path) => File.ReadLines(path).Where(s => !string.IsNullOrWhiteSpace(s))
        .Select(s => JsonSerializer.Deserialize<DialogueLine>(s, Json) ?? throw new InvalidDataException("Invalid dialogue index row."));
}
