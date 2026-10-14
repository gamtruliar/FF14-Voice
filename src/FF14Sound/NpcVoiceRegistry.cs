using System.IO;
using System.Text.Json;
using FF14Sound.Voices;

namespace FF14Sound;

public sealed class NpcVoiceRegistry
{
    private readonly string file;
    private Dictionary<string, NpcVoiceEntry> saved = new();
    private readonly Dictionary<string, NpcVoiceEntry> rows = new();
    private Dictionary<string, NpcVoiceEntry[]> byName = new();
    private readonly HashSet<string> seenThisSession = [];
    public IEnumerable<NpcVoiceEntry> Rows => rows.Values;
    public NpcVoiceRegistry(string file) => this.file = file;

    public void Load(string? indexPath)
    {
        // Parse both before replacing state; preserve the last usable registry on error.
        var overrides = File.Exists(file) ? JsonSerializer.Deserialize<NpcVoiceEntry[]>(File.ReadAllText(file)) ?? [] : [];
        var index = File.Exists(indexPath) ? JsonSerializer.Deserialize<NpcVoiceEntry[]>(File.ReadAllText(indexPath!)) ?? [] : [];
        saved = overrides.ToDictionary(r => r.Key);
        rows.Clear();
        foreach (var row in index) rows[row.Key] = row;
        foreach (var item in overrides)
        {
            if (!rows.TryGetValue(item.Key, out var row)) rows[item.Key] = item.Copy();
            else
            {
                row.CvMark = item.CvMark;
                if (!VoiceDescriptionDesigner.IsGenerated(item))
                {
                    row.VoiceDescription = item.VoiceDescription; row.DescriptionBasis = item.DescriptionBasis;
                    row.DescriptionSource = item.DescriptionSource; row.DescriptionVersion = item.DescriptionVersion;
                    row.VoiceGender = item.VoiceGender; row.VoiceAge = item.VoiceAge; row.VoiceTraitsBasis = item.VoiceTraitsBasis;
                }
                row.Notes = item.Notes; row.LastSeen = item.LastSeen;
            }
        }
        foreach (var row in rows.Values.Where(r => VoiceDescriptionDesigner.IsGenerated(r) && r.DescriptionVersion < VoiceDescriptionDesigner.Version))
            NpcVoiceIndex.SetSuggestedDescription(row);
        Reindex();
    }
    private void Reindex() => byName = rows.Values.SelectMany(r => r.Names.Select(n => (Name: VoiceResolver.Normalize(n), Row: r)))
        .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Select(p => p.Row).Distinct().ToArray());

    public NpcVoiceEntry? Find(string name)
    {
        var key = VoiceResolver.Normalize(name);
        if (key.Length == 0) return null;
        if (rows.TryGetValue("live:" + key, out var live)) return live;
        return byName.TryGetValue(key, out var matches) && matches.Length == 1 ? matches[0] : null;
    }
    public NpcVoiceEntry? Observe(string name, string language)
    {
        var key = VoiceResolver.Normalize(name);
        if (key.Length == 0) return null;
        var row = Find(name);
        if (row is null)
        {
            row = new NpcVoiceEntry { Key = "live:" + key, Notes = "遊戲遇見；名稱未唯一對應 NPC ID，按顯示名稱共用設定。" };
            if (language == "ja") row.Japanese = name;
            else if (language == "de") row.German = name;
            else if (language == "fr") row.French = name;
            else row.English = name;
            NpcVoiceIndex.SetSuggestedDescription(row);
            rows[row.Key] = row; Reindex();
        }
        if (!seenThisSession.Contains(row.Key))
        {
            row.LastSeen = DateTimeOffset.Now; saved[row.Key] = row.Copy();
            Save(); seenThisSession.Add(row.Key);
        }
        return row;
    }
    public string DescriptionFor(string name, string defaultDescription)
    {
        var row = Find(name);
        if (row is null || string.IsNullOrWhiteSpace(row.VoiceDescription)) return defaultDescription;
        // A changed global default also applies to untouched generic template rows.
        if (row.DescriptionBasis.StartsWith("建議演繹") && row.VoiceDescription == "自然、清晰、親切，中等語速，平穩敘述。") return defaultDescription;
        return row.VoiceDescription;
    }
    public string DescriptionForDialogue(string name, string language, string defaultDescription)
    {
        if (Find(name) is not null) return DescriptionFor(name, defaultDescription);
        // Same generated description as Observe, without marking every client NPC as encountered.
        var row = new NpcVoiceEntry { Key = "live:" + VoiceResolver.Normalize(name) };
        if (language == "ja") row.Japanese = name;
        else if (language == "de") row.German = name;
        else if (language == "fr") row.French = name;
        else row.English = name;
        NpcVoiceIndex.SetSuggestedDescription(row);
        return row.VoiceDescription;
    }
    public void ApplyEdits(IEnumerable<NpcVoiceEntry> edits)
    {
        foreach (var edit in edits)
        {
            if (!rows.TryGetValue(edit.Key, out var row)) continue;
            if (row.VoiceDescription == edit.VoiceDescription && row.CvMark == edit.CvMark && row.Notes == edit.Notes) continue;
            if (row.VoiceDescription != edit.VoiceDescription)
            {
                row.DescriptionBasis = "使用者指定演繹，非官方 CV 資料";
                row.DescriptionVersion = 0; row.DescriptionSource = "";
                row.VoiceGender = ""; row.VoiceAge = ""; row.VoiceTraitsBasis = "使用者自訂描述；以描述內容為準";
            }
            row.VoiceDescription = edit.VoiceDescription; row.CvMark = edit.CvMark; row.Notes = edit.Notes;
            edit.DescriptionBasis = row.DescriptionBasis;
            edit.DescriptionSource = row.DescriptionSource; edit.DescriptionVersion = row.DescriptionVersion;
            edit.VoiceGender = row.VoiceGender; edit.VoiceAge = row.VoiceAge; edit.VoiceTraitsBasis = row.VoiceTraitsBasis;
            saved[row.Key] = row.Copy();
        }
        Save();
    }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(saved.Values, VoiceCatalog.JsonOptions));
        File.Move(file + ".tmp", file, true);
    }
}
