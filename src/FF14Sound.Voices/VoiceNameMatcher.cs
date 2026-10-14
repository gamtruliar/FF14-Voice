using System.Text.Json;
using System.Text.RegularExpressions;
using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace FF14Sound.Voices;

public sealed record VoiceNameCandidate(string Token, string Evidence, string[] Names, uint[] RowIds, string Quest, bool Applied);

public static class VoiceNameMatcher
{
    public static VoiceCatalog Build(string sqpack, string catalogPath)
    {
        var catalog = VoiceCatalog.Load(catalogPath);
        using var game = new GameData(sqpack);
        var battle = CatalogBuilder.ReadNames(game, "BNpcName");
        var npcs = CatalogBuilder.ReadNames(game);
        var quests = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("Quest", Language.English));
        // Current client layout: 50 script names followed by their 50 UInt32 arguments.
        // Validate the layout instead of interpreting arbitrary numeric fields as actors.
        if (quests.Columns.Count < 150 || Enumerable.Range(50, 50).Any(i => quests.Columns[i].Type.ToString() != "String") ||
            Enumerable.Range(100, 50).Any(i => quests.Columns[i].Type.ToString() != "UInt32"))
            throw new InvalidDataException("Quest script layout changed; speaker matching needs an updated reader.");
        var actors = quests.SelectMany(q => Enumerable.Range(0, 50).Select(i => new
        {
            QuestId = q.ReadColumn(1)?.ToString() ?? "", QuestName = q.ReadColumn(0)?.ToString() ?? "",
            Variable = q.ReadColumn(50 + i)?.ToString() ?? "", Value = Convert.ToUInt32(q.ReadColumn(100 + i))
        }).Where(a => (a.Variable.StartsWith("ACTOR") || a.Variable.StartsWith("LOC_ACTOR")) && npcs.ContainsKey(a.Value))).ToArray();
        var report = new List<VoiceNameCandidate>();
        var updated = new List<GameVoice>();
        foreach (var voice in catalog.Voices)
        {
            var names = voice.Names.ToList();
            if (voice.Names.Length == 0)
            {
                var exact = battle.Where(p => p.Value.TryGetValue("English", out var english) && VoiceResolver.Normalize(english) == VoiceResolver.Normalize(voice.Token)).ToArray();
                if (exact.Length > 0)
                {
                    var aliases = exact.SelectMany(p => p.Value.Values).Distinct().ToArray();
                    names.AddRange(aliases);
                    report.Add(new(voice.Token, "BNpcName 英文名稱完整匹配", aliases, exact.Select(p => p.Key).ToArray(), "", true));
                }
                else
                {
                    // Partial names and quest actors are evidence for review, never global aliases.
                    foreach (var table in new[] { (Name: "ENpcResident", Rows: npcs), (Name: "BNpcName", Rows: battle) })
                    foreach (var candidate in table.Rows.Where(p => p.Value.TryGetValue("English", out var english) &&
                        Regex.Split(english.ToUpperInvariant(), @"[^\p{L}\p{N}]+").Contains(voice.Token)))
                        report.Add(new(voice.Token, table.Name + " 名稱包含代號（候選，未套用）", candidate.Value.Values.Distinct().ToArray(), [candidate.Key], "", false));
                    var suffix = Regex.Match(voice.Token, @"^(.*?)(\d{5})$");
                    var stem = suffix.Success ? suffix.Groups[1].Value : voice.Token;
                    var scoped = actors.Where(a => !suffix.Success || a.QuestId.EndsWith("_" + suffix.Groups[2].Value)).ToArray();
                    var explicitActors = scoped.Where(a => a.Variable == "LOC_ACTOR_" + stem || a.Variable == "ACTOR_" + stem).ToArray();
                    var candidates = explicitActors.Length > 0 ? explicitActors : suffix.Success ? scoped : [];
                    foreach (var candidate in candidates.DistinctBy(a => (a.QuestId, a.Value)))
                        report.Add(new(voice.Token, explicitActors.Length > 0 ? "任務具名 actor（須確認場景，未套用）" : "同任務出場 NPC（未證實係說話者）",
                            npcs[candidate.Value].Values.Distinct().ToArray(), [candidate.Value], candidate.QuestId + " · " + candidate.QuestName + " · " + candidate.Variable, false));
                }
            }
            updated.Add(voice with { Names = names.Distinct().ToArray() });
        }
        var result = catalog with { Voices = updated.ToArray() };
        var folder = Path.GetDirectoryName(Path.GetFullPath(catalogPath))!;
        // Preserve previous applied evidence when rerunning against an enriched catalog.
        var reportPath = Path.Combine(folder, "voice-name-candidates.json");
        if (File.Exists(reportPath))
            report.AddRange((JsonSerializer.Deserialize<VoiceNameCandidate[]>(File.ReadAllText(reportPath)) ?? []).Where(c => c.Applied && !report.Any(r => r.Token == c.Token && r.Applied)));
        File.WriteAllText(reportPath + ".tmp", JsonSerializer.Serialize(report, VoiceCatalog.JsonOptions));
        File.Move(reportPath + ".tmp", reportPath, true);
        result.Save(catalogPath);
        NpcVoiceIndex.Build(sqpack, result, folder);
        return result;
    }
}
