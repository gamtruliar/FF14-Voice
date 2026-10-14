using Lumina;
using Lumina.Data;
using Lumina.Excel;

namespace FF14Sound.Voices;

public sealed record NpcAppearance(uint NpcId, byte Race, byte Gender, byte BodyType, byte Tribe);

public static class NpcAppearanceReader
{
    // EXDSchema e773c41 / ENpcBase; Anamnesis ActorCustomizeMemory documents the enum values.
    public const string SchemaSource = "https://github.com/xivdev/EXDSchema/blob/e773c41a90aed788cf4c1c48469fa85618ef01fb/ENpcBase.yml";
    public const string ValuesSource = "https://github.com/imchillin/Anamnesis/blob/master/Anamnesis/Memory/ActorCustomizeMemory.cs";

    public static Dictionary<uint, NpcAppearance> Read(GameData game)
    {
        var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("ENpcBase", Language.None));
        // Fail explicitly after incompatible game updates instead of silently assigning the wrong voice.
        // RawRow columns follow EXH storage order, not the schema's logical field order.
        var columns = new[] { 202, 203, 204, 206 }.Select(offset => Enumerable.Range(0, sheet.Columns.Count)
            .SingleOrDefault(i => sheet.Columns[i].Offset == offset && sheet.Columns[i].Type.ToString() == "UInt8", -1)).ToArray();
        if (sheet.Columns.Count != 110 || columns.Any(i => i < 0))
            throw new InvalidDataException("ENpcBase 外觀欄位已改動，請更新 schema 後重建 NPC 表。");
        return sheet.ToDictionary(r => r.RowId, r => new NpcAppearance(r.RowId,
            Convert.ToByte(r.ReadColumn(columns[0])), Convert.ToByte(r.ReadColumn(columns[1])),
            Convert.ToByte(r.ReadColumn(columns[2])), Convert.ToByte(r.ReadColumn(columns[3]))));
    }

    public static void Attach(NpcVoiceEntry row, IReadOnlyDictionary<uint, NpcAppearance> appearances)
        => row.Appearances = row.NpcIds.Where(appearances.ContainsKey).Select(id => appearances[id]).ToArray();
}
