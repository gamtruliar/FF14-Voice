using System.IO;
using System.Text.Json;
using FF14Sound;
using System.Net.Http;
using FF14Sound.Voices;

if (args.Length < 2) { Console.Error.WriteLine("Batch prepare <index.jsonl> <folder> | run <plan.json> [--limit N] | pause <folder> | status <folder>"); return 1; }
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
if (args[0] == "prepare" && args.Length == 3)
{
    var settings = Settings.Load(); settings.SourceLanguage = "ja"; settings.SpeechMode = "original";
    var result = await BatchDubbing.Prepare(args[1], args[2], settings, new Progress<string>(Console.WriteLine), stop.Token);
    Console.WriteLine(JsonSerializer.Serialize(result)); return 0;
}
if (args[0] == "run")
{
    var limit = args.Length == 4 && args[2] == "--limit" ? int.Parse(args[3]) : int.MaxValue;
    await BatchDubbing.Run(args[1], stop.Token, limit); Console.WriteLine(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, "status.json"))); return 0;
}
if (args[0] == "pause") { BatchDubbing.Pause(args[1]); return 0; }
if (args[0] == "exclude-native" && args.Length == 3) { Console.WriteLine(JsonSerializer.Serialize(BatchDubbing.ExcludeNativeAudio(args[1], args[2], stop.Token))); return 0; }
if (args[0] == "ref-first") { Console.WriteLine(JsonSerializer.Serialize(BatchDubbing.PrioritizeReferences(args[1], stop.Token))); return 0; }
if (args[0] == "status") { Console.WriteLine(File.ReadAllText(Path.Combine(args[1], "status.json"))); return 0; }
if (args[0] == "verify-cache")
{
    var plan = BatchDubbing.Read<BatchPlan>(args[1]);
    var settings = Settings.Load();
    var library = new GameVoiceLibrary(); library.Load(settings.CatalogPath.Length > 0 ? settings.CatalogPath : GameVoiceLibrary.FindCatalog());
    var registry = new NpcVoiceRegistry(Path.Combine(Settings.Folder, "npc-voices.json"));
    registry.Load(Path.Combine(Path.GetDirectoryName(library.CatalogPath)!, "npc-voice-index.json"));
    using var speech = new SpeechClient(new NoNetwork());
    int checkedCount = 0;
    foreach (var json in File.ReadLines(plan.JobsFile))
    {
        var job = JsonSerializer.Deserialize<BatchJob>(json, DialogueIndex.Json)!;
        if (!SpeechClient.IsCached(job.CacheFile)) continue;
        var selection = SpeechSelection.Choose("original", job.Text.Replace("\r\n", "\n"), "", plan.Language, settings.TargetLanguage);
        var voice = selection.Apply(library.Resolve(job.Speaker, selection.Language, settings, registry.DescriptionForDialogue(job.Speaker, plan.Language, settings.DefaultVoiceDescription)));
        var audio = await speech.Generate(settings.Server, selection.Text, voice, CancellationToken.None, cacheFolder: SpeechClient.CacheFolder(settings));
        var expected = await File.ReadAllBytesAsync(job.CacheFile);
        if (!audio.SequenceEqual(expected)) throw new Exception("Interactive cache mismatch.");
        if (++checkedCount == 3) break;
    }
    if (checkedCount == 0) throw new Exception("No generated audio available to verify.");
    Console.WriteLine($"PASS: {checkedCount} actual preloaded WAVs reused by interactive voice selection; zero backend requests."); return 0;
}
return 1;

sealed class NoNetwork : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new Exception("Unexpected HTTP request: preload cache missed.");
}
