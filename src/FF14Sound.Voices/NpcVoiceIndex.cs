using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Lumina;

namespace FF14Sound.Voices;

public sealed class NpcVoiceEntry
{
    public string Key { get; set; } = "";
    public uint[] NpcIds { get; set; } = [];
    public string English { get; set; } = "";
    public string Japanese { get; set; } = "";
    public string German { get; set; } = "";
    public string French { get; set; } = "";
    public string[] ReferenceTokens { get; set; } = [];
    public string CvMark { get; set; } = "未確認";
    public string VoiceDescription { get; set; } = "";
    public string DescriptionBasis { get; set; } = "";
    public string DescriptionSource { get; set; } = "";
    public int DescriptionVersion { get; set; }
    public NpcAppearance[] Appearances { get; set; } = [];
    public string VoiceGender { get; set; } = "";
    public string VoiceAge { get; set; } = "";
    public string VoiceTraitsBasis { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTimeOffset? LastSeen { get; set; }
    [JsonIgnore] public string Name => Names.FirstOrDefault() ?? Key;
    [JsonIgnore] public string OriginalStatus => ReferenceTokens.Length > 0
        ? "名稱配對原聲：" + string.Join(", ", ReferenceTokens) : "未找到原聲（未確認）";
    [JsonIgnore] public IEnumerable<string> Names => new[] { Japanese, English, German, French }.Where(n => n.Length > 0);
    public NpcVoiceEntry Copy() => (NpcVoiceEntry)MemberwiseClone();
    // A settings snapshot for display/export, never a persisted canonical alias.
    [JsonIgnore] public string VoiceMapping { get; set; } = "";
    [JsonIgnore] public string MappingSpeaker { get; set; } = "";
    [JsonIgnore] public string MappingBasis { get; set; } = "";
    [JsonIgnore] public string MappedReference { get; set; } = "";
}

public static class NpcVoiceIndex
{
    public static NpcVoiceEntry[] Build(string sqpack, VoiceCatalog catalog, string output)
    {
        using var game = new GameData(sqpack);
        var names = CatalogBuilder.ReadNames(game);
        var appearances = NpcAppearanceReader.Read(game);
        var voices = catalog.Voices.SelectMany(v => v.Names.Append(v.Token).Select(n => (Name: VoiceResolver.Normalize(n), v.Token)))
            .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => g.Select(p => p.Token).Distinct().ToArray());
        // Identical localized labels cannot be distinguished by the dialogue reader.
        // Keep all row IDs so the name-level grouping is explicit and reversible.
        var rows = names.GroupBy(p => string.Join("\u001f", new[] { "English", "Japanese", "German", "French" }.Select(l => p.Value.GetValueOrDefault(l, ""))))
            .Select(group =>
            {
                var localized = group.First().Value;
                var ids = group.Select(p => p.Key).Order().ToArray();
                var row = new NpcVoiceEntry
                {
                    Key = "npc:" + ids[0], NpcIds = ids,
                    English = localized.GetValueOrDefault("English", ""), Japanese = localized.GetValueOrDefault("Japanese", ""),
                    German = localized.GetValueOrDefault("German", ""), French = localized.GetValueOrDefault("French", "")
                };
                row.ReferenceTokens = row.Names.Select(VoiceResolver.Normalize).Where(voices.ContainsKey)
                    .SelectMany(n => voices[n].Length == 1 ? voices[n] : []).Distinct().Order().ToArray();
                NpcAppearanceReader.Attach(row, appearances);
                SetSuggestedDescription(row);
                return row;
            }).OrderBy(r => r.Key).ToArray();
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "npc-voice-index.json"), JsonSerializer.Serialize(rows, VoiceCatalog.JsonOptions));
        ExportCsv(Path.Combine(output, "npc-voices.csv"), rows);
        ExportCsv(Path.Combine(output, "npcs-without-matched-voice.csv"), rows.Where(r => r.ReferenceTokens.Length == 0));
        return rows;
    }

    public static void SetSuggestedDescription(NpcVoiceEntry row) => VoiceDescriptionDesigner.Apply(row);

    public static void ExportCsv(string path, IEnumerable<NpcVoiceEntry> rows)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("Key,NPC IDs,Japanese,English,German,French,原聲索引狀態,CV 手動標記,Voice description,描述依據,備註,最後遇見,Voice mapping,Mapping speaker,Mapping basis,Reference audio,角色身份來源,聲線性別,聲線年齡感,性別年齡依據,NPC 外觀資料");
        foreach (var row in rows)
            writer.WriteLine(string.Join(",", new[] { row.Key, string.Join(";", row.NpcIds), row.Japanese, row.English, row.German, row.French,
                row.OriginalStatus, row.CvMark, row.VoiceDescription, row.DescriptionBasis, row.Notes, row.LastSeen?.ToString("O") ?? "",
                row.VoiceMapping.Length > 0 ? row.VoiceMapping : row.ReferenceTokens.Length > 0 ? string.Join(" / ", row.ReferenceTokens) : "default",
                row.MappingSpeaker, row.MappingBasis.Length > 0 ? row.MappingBasis : "索引候選；實際使用以 GUI 設定及可用樣本為準",
                row.MappedReference, row.DescriptionSource, row.VoiceGender, row.VoiceAge, row.VoiceTraitsBasis,
                string.Join(";", row.Appearances.Select(a => $"{a.NpcId}:Race={a.Race}/Gender={a.Gender}/BodyType={a.BodyType}/Tribe={a.Tribe}")) }.Select(Csv)));
    }
    private static string Csv(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
