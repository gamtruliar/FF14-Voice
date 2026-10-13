using FF14Sound.Voices;
if (args.Length != 3) { Console.Error.WriteLine("Usage: VoiceLibrary <sqpack> <voice-clips.json> <output-directory>"); return 1; }
var progress = new Progress<string>(Console.WriteLine);
var catalog = await CatalogBuilder.Build(args[0], args[1], args[2], progress, CancellationToken.None);
Console.WriteLine($"Ready: {catalog.Voices.Length} speakers; {catalog.Voices.Sum(v => v.References.Length)} references; {catalog.Errors.Length} errors.");
return 0;
