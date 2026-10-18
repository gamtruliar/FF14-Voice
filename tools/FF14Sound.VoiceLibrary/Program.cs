using FF14Sound.Voices;
if (args.Length == 4 && args[0] == "--traditional-dialogue")
{
    var report = TraditionalDialogue.Build(args[1], args[2], args[3], new Progress<string>(Console.WriteLine));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report.StatusCounts));
    Console.WriteLine($"Paired {report.Rows} Japanese rows; {report.Errors.Length} errors.");
    return report.Errors.Length == 0 ? 0 : 2;
}
if (args.Length == 2 && args[0] == "--inspect-languages") { Inspect.Languages(args[1]); return 0; }
if (args.Length == 5 && args[0] == "--ref-candidates-all")
{
    await ReferenceCandidates.ExportAll(args[1], args[2], args[3], args[4], CancellationToken.None);
    return 0;
}
if (args.Length == 6 && args[0] == "--ref-candidates")
{
    await ReferenceCandidates.Export(args[1], args[2], args[3], args[4], args[5], CancellationToken.None);
    return 0;
}
if (args.Length == 3 && args[0] == "--native-index")
{
    var index = NativeDialogue.Build(args[1], args[2]);
    Console.WriteLine($"Native audio: {index.Lines.Length} localized rows; {index.Lines.Count(l => l.Static)} static lines.");
    return 0;
}
if (args.Length == 2 && args[0] == "--inspect-speech") { Inspect.DialogueSchemas(args[1]); return 0; }
if (args.Length == 4 && args[0] == "--dialogue-index")
{
    var report = DialogueIndex.Build(args[1], args[2], args[3], new Progress<string>(Console.WriteLine), CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report.StatusCounts));
    Console.WriteLine($"Indexed {report.Rows} rows from {report.TextTables} text tables; {report.Errors.Length} errors.");
    return report.Errors.Length == 0 ? 0 : 2;
}
if (args.Length == 3 && args[0] == "--inspect-appearance") { Inspect.Appearance(args[1], args[2]); return 0; }
if (args.Length == 3 && args[0] == "--inspect") { Inspect.Run(args[1], args[2]); return 0; }
if (args.Length == 3 && args[0] == "--match-names")
{
    var result = VoiceNameMatcher.Build(args[1], Path.Combine(args[2], "catalog.json"));
    Console.WriteLine($"Names matched: {result.Voices.Count(v => v.Names.Length > 0)} / {result.Voices.Length}. Candidate evidence saved.");
    return 0;
}
if (args.Length == 3 && args[0] == "--npc-table")
{
    var rows = NpcVoiceIndex.Build(args[1], VoiceCatalog.Load(Path.Combine(args[2], "catalog.json")), args[2]);
    Console.WriteLine($"NPC table: {rows.Length} name groups, {rows.Sum(r => r.NpcIds.Length)} NPC IDs, {rows.Count(r => r.ReferenceTokens.Length == 0)} without a name-matched reference.");
    return 0;
}
if (args.Length != 3) { Console.Error.WriteLine("Usage: VoiceLibrary <sqpack> <voice-clips.json> <output-directory>"); return 1; }
var progress = new Progress<string>(Console.WriteLine);
var catalog = await CatalogBuilder.Build(args[0], args[1], args[2], progress, CancellationToken.None);
catalog = VoiceNameMatcher.Build(args[0], Path.Combine(args[2], "catalog.json"));
NativeDialogue.Build(Path.Combine(args[2], "catalog.json"), args[1]);
Console.WriteLine($"Ready: {catalog.Voices.Length} speakers; {catalog.Voices.Sum(v => v.References.Length)} references; {catalog.Errors.Length} errors.");
return catalog.Errors.Length == 0 ? 0 : 2;
