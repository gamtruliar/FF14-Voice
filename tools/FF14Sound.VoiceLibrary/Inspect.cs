using System.Text.Json;
using Lumina;
using Lumina.Data;
using Lumina.Excel;
using FF14Sound.Voices;

internal static class Inspect
{
    public static void Languages(string sqpack)
    {
        using var game = new GameData(sqpack);
        Console.WriteLine("Languages: " + string.Join(", ", Enum.GetValues<Language>().Select(l => $"{(int)l}={l}")));
        Console.WriteLine($"Sheets: {game.Excel.SheetNames.Count()}");
        foreach (var name in new[] { "ENpcResident", "Quest", "NpcYell" })
        foreach (var lang in Enum.GetValues<Language>())
        {
            try
            {
                var raw = game.Excel.GetRawSheet(name, lang);
                var sheet = new ExcelSheet<RawRow>(raw);
                Console.WriteLine($"{name} requested={lang} actual={raw.Language} columns={sheet.Columns.Count}: " +
                    string.Join(" | ", sheet.Take(3).Select(r => r.RowId + ":" + r.ReadColumn(0)?.ToString())));
            }
            catch (Exception ex) { Console.WriteLine($"{name}/{lang}: {ex.Message}"); }
        }
    }
    public static void DialogueSchemas(string sqpack)
    {
        using var game = new GameData(sqpack);
        foreach (var name in new[] { "NpcYell", "Balloon", "DefaultTalk", "ContentTalk", "ContentTalkParam", "PublicContentTextData" })
        {
            var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet(name, Language.Japanese));
            Console.WriteLine(name + ": " + string.Join(", ", sheet.Columns.Select((c, i) => $"{i}:{c.Offset}/{c.Type}")));
            foreach (var row in sheet.Where(r => sheet.Columns.Select((c, i) => (c, i)).Any(p => p.c.Type.ToString() == "String" && r.ReadStringColumn(p.i).ExtractText().Length > 0)).Take(2))
                Console.WriteLine(row.RowId + ": " + JsonSerializer.Serialize(Enumerable.Range(0, sheet.Columns.Count).Select(i => row.ReadColumn(i)?.ToString()), DialogueIndex.Json));
        }
    }
    public static void Appearance(string sqpack, string output)
    {
        using var game = new GameData(sqpack);
        var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("ENpcBase", Language.None));
        var residents = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("ENpcResident", Language.English));
        var names = residents.ToDictionary(r => r.RowId, r => r.ReadColumn(0)?.ToString() ?? "");
        var ids = names.Where(p => p.Value is "Eugennoix" or "Mother Miounne" or "Gerolt" or "Khloe Aliapoh" or "Hamon" or "Y'shtola").Select(p => p.Key).Take(25);
        var data = ids.Select(id => new { Id = id, Names = names[id], Columns = Enumerable.Range(32, Math.Min(32, sheet.Columns.Count - 32)).Select(i => new { Column = i, sheet.Columns[i].Offset, Type = sheet.Columns[i].Type.ToString(), Value = sheet.GetRow(id).ReadColumn(i)?.ToString() }) });
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "npc-appearance-inspect.json"), JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"ENpcBase: {sheet.Columns.Count} columns; " + string.Join(", ", Enumerable.Range(46, 11).Select(i => $"{i}={sheet.Columns[i].Offset}/{sheet.Columns[i].Type}")));
    }
    public static void Run(string sqpack, string output)
    {
        Directory.CreateDirectory(output);
        using var game = new GameData(sqpack);
        var sheets = game.Excel.SheetNames.Order().ToArray();
        File.WriteAllLines(Path.Combine(output, "sheets.txt"), sheets);
        var quest = new ExcelSheet<RawRow>(game.Excel.GetRawSheet("Quest", Language.English));
        foreach (var id in new uint[] { 65536 + 2236, 65536 + 3608 })
        {
            var row = quest.GetRow(id);
            File.WriteAllText(Path.Combine(output, "Quest-" + id + ".json"), JsonSerializer.Serialize(
                Enumerable.Range(0, quest.Columns.Count).Select(i => new { Column = i, Type = quest.Columns[i].Type.ToString(), Value = row.ReadColumn(i)?.ToString() })
                    .Where(p => !string.IsNullOrEmpty(p.Value) && p.Value != "0" && p.Value != "False"), new JsonSerializerOptions { WriteIndented = true }));
        }
        foreach (var name in sheets.Where(n => n is "BNpcName" or "ENpcResident" || n.Contains("02236") || n.Contains("03608") || n == "cut_scene/032/VoiceMan_03200"))
        {
            var sheet = new ExcelSheet<RawRow>(game.Excel.GetRawSheet(name, Language.English));
            var rows = sheet.Select(r => new { r.RowId, Columns = Enumerable.Range(0, sheet.Columns.Count).Select(i => r.ReadColumn(i)?.ToString()).ToArray() });
            if (name is "ENpcResident" or "BNpcName") rows = rows.Where(r => r.Columns.Any(c => new[] { "Susano", "Thordan", "Ultima", "traitor" }.Any(t => c?.Contains(t, StringComparison.OrdinalIgnoreCase) == true)));
            File.WriteAllText(Path.Combine(output, name.Replace('/', '-') + ".json"), JsonSerializer.Serialize(rows, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(name + ": " + sheet.Columns.Count + " columns");
        }
    }
}
