using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace FF14Sound.Voices;

public sealed record TraditionalDialogueLine(string Sheet, uint RowId, string Key, string Japanese,
    string JapaneseSpeaker, string? TraditionalChinese, string TraditionalSpeaker, string Status);
public sealed record TraditionalDialogueReport(string JapaneseIndex, string Sqpack, string ClientVersion,
    DateTimeOffset BuiltAt, int Rows, Dictionary<string, int> StatusCounts, string[] Errors);
public sealed record TraditionalDialogueMatch(string Text, string Speaker, string Source, int CandidateCount);

/// <summary>Japanese client IDs remain authoritative; TC is an optional translation source.</summary>
public sealed class TraditionalDialogue
{
    public const string FileName = "dialogue-ja-tc.jsonl";
    private readonly Dictionary<string, TraditionalDialogueLine[]> lines;
    public TraditionalDialogue(IEnumerable<TraditionalDialogueLine> entries) => lines = entries
        .GroupBy(l => Normalize(l.Japanese)).ToDictionary(g => g.Key, g => g.ToArray());
    private static string Normalize(string value) => Regex.Replace(value, @"\s+", " ").Trim();
    public static TraditionalDialogue Load(string path) => new(File.ReadLines(path)
        .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => JsonSerializer.Deserialize<TraditionalDialogueLine>(s, DialogueIndex.Json)
            ?? throw new InvalidDataException("Invalid Japanese/TC dialogue row.")));

    public TraditionalDialogueMatch? Match(string japanese, string speaker)
    {
        if (!lines.TryGetValue(Normalize(japanese), out var candidates)) return null;
        var name = Normalize(speaker);
        if (name.Length > 0) candidates = candidates.Where(l => l.JapaneseSpeaker.Length == 0 || Normalize(l.JapaneseSpeaker) == name).ToArray();
        // Missing, dynamic and ambiguous rows must not disappear before uniqueness is checked.
        if (candidates.Length == 0 || candidates.Any(l => l.Status != "matched" || string.IsNullOrWhiteSpace(l.TraditionalChinese))) return null;
        var targets = candidates.Select(l => l.TraditionalChinese!).Distinct().ToArray();
        if (targets.Length != 1) return null;
        var names = candidates.Select(l => l.TraditionalSpeaker).Where(s => s.Length > 0).Distinct().ToArray();
        return new(targets[0], names.Length == 1 ? names[0] : speaker,
            candidates[0].Sheet + ":" + candidates[0].RowId + " / " + candidates[0].Key, candidates.Length);
    }

    public static TraditionalDialogueReport Build(string japaneseIndex, string tcSqpack, string destination, IProgress<string>? progress = null)
    {
        var input = Path.GetFullPath(tcSqpack).TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(destination);
        if (output.Equals(Path.GetFullPath(japaneseIndex), StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(input + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Output must be separate from the Japanese index and game archives.");
        using var game = new GameData(input);
        var jaReport = JsonSerializer.Deserialize<DialogueScanReport>(File.ReadAllText(japaneseIndex + ".report.json"), DialogueIndex.Json)
            ?? throw new InvalidDataException("Japanese index report missing.");
        using var japaneseGame = new GameData(jaReport.Sqpack);
        var jaNames = CatalogBuilder.ReadNames(japaneseGame);
        var tcNames = CatalogBuilder.ReadNames(game);
        var speakers = jaNames.Where(p => p.Value.ContainsKey("Japanese") && tcNames.ContainsKey(p.Key))
            .GroupBy(p => p.Value["Japanese"]).ToDictionary(g => g.Key, g => g
                .Select(p => tcNames[p.Key].GetValueOrDefault("TraditionalChinese", "")).Where(n => n.Length > 0).Distinct().ToArray());
        var counts = new Dictionary<string, int>();
        var errors = new List<string>();
        int rows = 0, tables = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            using (var writer = new StreamWriter(output + ".tmp", false, new UTF8Encoding(false)))
            foreach (var group in DialogueIndex.Read(japaneseIndex).Where(l => l.Language == "ja" && l.Text.Length > 0).GroupBy(l => l.Sheet))
            {
                var keyed = new Dictionary<string, (string Text, bool Static)>();
                ExcelSheet<RawRow>? tcSheet = null;
                ExcelSheet<RawRow>? jaSheet = null;
                string? failure = null;
                var hasKeys = group.First().Key.StartsWith("TEXT_", StringComparison.Ordinal);
                try
                {
                    if (!game.Excel.SheetNames.Contains(group.Key)) failure = "missing-sheet";
                    else
                    {
                        var raw = game.Excel.GetRawSheet(group.Key, Language.TraditionalChinese);
                        if (raw.Language != Language.TraditionalChinese) throw new InvalidDataException("Not a TC localized sheet.");
                        tcSheet = new(raw);
                        jaSheet = new(japaneseGame.Excel.GetRawSheet(group.Key, Language.Japanese));
                        if (hasKeys)
                        {
                            if (tcSheet.Columns.Count < 2 || tcSheet.Columns[0].Type.ToString() != "String" || tcSheet.Columns[1].Type.ToString() != "String")
                                throw new InvalidDataException("Text key schema changed.");
                            foreach (var row in tcSheet)
                            {
                                var key = row.ReadStringColumn(0).ExtractText();
                                var value = row.ReadStringColumn(1);
                                if (!keyed.TryAdd(key, (value.ExtractText().Trim(), DialogueIndex.IsStatic(value))))
                                    keyed[key] = ("", false); // Duplicate IDs are not safe matches.
                            }
                        }
                        else if (!tcSheet.Columns.Select(c => (c.Offset, c.Type)).SequenceEqual(jaSheet.Columns.Select(c => (c.Offset, c.Type))))
                            failure = "schema-mismatch";
                    }
                }
                catch (Exception ex) { failure = "sheet-error"; errors.Add(group.Key + ": " + ex.Message); }
                foreach (var line in group)
                {
                    var status = failure ?? "missing-id";
                    string? target = null;
                    try
                    {
                        if (failure is null)
                        {
                            var sourceRow = jaSheet!.GetRow(line.RowId);
                            var column = hasKeys ? 1 : int.Parse(line.Key.Split(':').Last());
                            if ((hasKeys && sourceRow.ReadStringColumn(0).ExtractText() != line.Key) || sourceRow.ReadStringColumn(column).ExtractText().Trim() != line.Text)
                                status = "stale-japanese-index";
                            else
                            {
                                (string Text, bool Static)? translated = null;
                                if (hasKeys) { if (keyed.TryGetValue(line.Key, out var found)) translated = found; }
                                else if (tcSheet!.TryGetRow(line.RowId, out var tcRow))
                                {
                                    var value = tcRow.ReadStringColumn(column);
                                    translated = (value.ExtractText().Trim(), DialogueIndex.IsStatic(value));
                                }
                                if (translated is { } pair)
                                {
                                    target = pair.Text;
                                    status = target.Length == 0 ? "empty-tc" : !pair.Static || !DialogueIndex.IsStatic(sourceRow.ReadStringColumn(column)) ? "dynamic-text"
                                        : line.Status is "ready" or "unresolved-speaker" ? "matched" : "non-dialogue";
                                }
                            }
                        }
                    }
                    catch (Exception ex) { status = "row-error"; errors.Add(group.Key + ":" + line.RowId + ": " + ex.Message); }
                    var tcSpeaker = speakers.TryGetValue(line.Speaker, out var names) && names.Length == 1 ? names[0] : "";
                    writer.WriteLine(JsonSerializer.Serialize(new TraditionalDialogueLine(line.Sheet, line.RowId, line.Key, line.Text,
                        line.Speaker, target, tcSpeaker, status), DialogueIndex.Json));
                    counts[status] = counts.GetValueOrDefault(status) + 1; rows++;
                }
                if (++tables % 250 == 0) progress?.Report($"Japanese → TC: {tables:N0} tables, {rows:N0} rows.");
            }
            File.Move(output + ".tmp", output, true);
        }
        finally { File.Delete(output + ".tmp"); }
        var versionPath = Path.Combine(input, "..", "ffxivgame.ver");
        var report = new TraditionalDialogueReport(Path.GetFullPath(japaneseIndex), input, File.Exists(versionPath) ? File.ReadAllText(versionPath).Trim() : "",
            DateTimeOffset.Now, rows, counts, errors.ToArray());
        File.WriteAllText(output + ".report.json", JsonSerializer.Serialize(report, new JsonSerializerOptions(DialogueIndex.Json) { WriteIndented = true }));
        return report;
    }
}
