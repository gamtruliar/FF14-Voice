using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;
using FF14Sound.Voices;
using Lumina.Text.ReadOnly;
using Lumina.Text.Payloads;

internal static class BatchChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static async Task Run(string folder)
    {
        Check(DialogueIndex.ActorToken("TEXT_TEST_00001_EUGENNOIX_000_1") == "EUGENNOIX", "Short dialogue sequence numbers supported");
        Check(DialogueIndex.ActorToken("TEXT_VOICEMAN_03200_00010_YSHTOLA") == "YSHTOLA", "VoiceMan actor supported");
        Check(DialogueIndex.ActorToken("TEXT_TEST_00001_A1_000_1") == "" && DialogueIndex.ActorToken("TEXT_TEST_00001_SYSTEM_000_1") == "", "Choices and system messages must not be cast as NPCs");
        Check(DialogueIndex.IsStatic(new ReadOnlySeString(Encoding.UTF8.GetBytes("Static dialogue"))), "Plain dialogue is static");
        Check(!DialogueIndex.IsStatic(new ReadOnlySeString(new byte[] { 2, (byte)MacroCode.PcName, 1, 3 })), "Player name macro must never be stripped and pre-generated");
        Check(DialogueIndex.IsStatic(new ReadOnlySeString(new byte[] { 2, (byte)MacroCode.NewLine, 1, 3 })), "Formatting newline is safe");
        var root = Path.Combine(folder, "batch-checks"); Directory.CreateDirectory(root);
        var catalogPath = Path.Combine(root, "catalog.json");
        new VoiceCatalog(1, "unused", DateTimeOffset.Now, [], []).Save(catalogPath);
        var nativeFile = Path.Combine(root, NativeDialogue.FileName);
        var nativeIndex = new NativeVoiceIndex(1, new() { ["TEST"] = ["BatchTest NPC"] },
            [new("TEST", "ja", "cut_scene/test:10", "Game already speaks", true),
             new("TEST", "ja", "cut_scene/test:11", "Dynamic fragment", false)]);
        BatchDubbing.Write(nativeFile, nativeIndex);
        var nativeAudio = NativeDialogue.Load(nativeFile);
        Check(nativeAudio.Contains("BatchTest NPC", "Game\nalready speaks", "ja"), "Native matching tolerates line wrapping");
        Check(!nativeAudio.Contains("Another NPC", "Game already speaks", "ja") && !nativeAudio.Contains("BatchTest NPC", "Game already speaks", "en"), "Native matching requires actor and source language");
        Check(!nativeAudio.Contains("BatchTest NPC", "Unvoiced sentence", "ja"), "Voiced actors still get TTS for their unvoiced lines");
        Check(!nativeAudio.Contains("BatchTest NPC", "Dynamic fragment", "ja") && nativeAudio.Contains("", "", "ja", "cut_scene/test:11"), "Dynamic text uses source identity, never stripped fragments");
        foreach (var originalMode in new[] { true, false })
        {
            int translations = 0, plays = 0;
            await SpeechWorkflow.Run(() => { translations++; return Task.CompletedTask; }, () => { plays++; return Task.CompletedTask; }, originalMode, nativeAudio: true);
            Check(translations == 1 && plays == 0, "Native audio keeps translation/subtitles and suppresses original/translated playback");
        }
        var settings = new Settings { SourceLanguage = "ja", SpeechMode = "original", CatalogPath = catalogPath, SpeechCacheFolder = Path.Combine(root, "cache") };
        var registry = new NpcVoiceRegistry(Path.Combine(root, "test-npcs.json")); registry.Load(null);
        var before = registry.DescriptionForDialogue("BatchTest NPC", "ja", settings.DefaultVoiceDescription);
        registry.Observe("BatchTest NPC", "ja");
        Check(before == registry.DescriptionFor("BatchTest NPC", settings.DefaultVoiceDescription), "Preload and first live encounter must use identical fallback direction");
        var index = Path.Combine(root, "index.jsonl");
        DialogueLine Line(string text, string status = "ready") => new("quest/test", 1, "key", "ja", text, text, "TEST", "BatchTest NPC", "test", status);
        var lines = new[] { Line("Hello\r\nthere"), Line("Hello\nthere"), Line("Another sentence"), Line("Player name", "dynamic-text"), Line("choose", "non-dialogue-or-unclassified"), Line("Game already speaks") };
        File.WriteAllLines(index, lines.Select(l => JsonSerializer.Serialize(l, DialogueIndex.Json)));
        var queue = Path.Combine(root, "queue");
        var plan = await BatchDubbing.Prepare(index, queue, settings, null, CancellationToken.None);
        Check(plan.Jobs == 2 && plan.Duplicates == 1 && plan.Deferred["dynamic-text"] == 1, "Batch deduplicates line endings and defers dynamic text");
        Check(plan.Deferred["game-voiced"] == 1, "New queues exclude native audio before voice selection");
        using var handler = new WavHandler(); using var speech = new SpeechClient(handler);
        await BatchDubbing.Run(BatchDubbing.PlanPath(queue), CancellationToken.None, 1, speech, 0);
        var state = BatchDubbing.Read<BatchStatus>(Path.Combine(queue, "status.json"));
        Check(state.State == "paused" && state.Completed == 1 && handler.Calls == 1, "Finite batch persists valid cache before pausing");
        Check(handler.LastPriority == "background" && handler.LastReferenceIsNull, "Default bulk jobs are text-only and background priority");
        await BatchDubbing.Run(BatchDubbing.PlanPath(queue), CancellationToken.None, injectedClient: speech, reserveBytes: 0);
        state = BatchDubbing.Read<BatchStatus>(Path.Combine(queue, "status.json"));
        Check(state.State == "completed" && state.Completed == 2 && state.CacheHits == 1 && handler.Calls == 2, "Resume skips valid outputs");
        var job = JsonSerializer.Deserialize<BatchJob>(File.ReadLines(plan.JobsFile).First(), DialogueIndex.Json)!;
        job.Voice.IsFallback = job.Fallback;
        Check(job.Voice.Seed.HasValue && handler.LastSeed == job.Voice.EffectiveSeed, "Batch snapshot and background request preserve NPC seed");
        await speech.Generate(plan.Server, "Hello\nthere", job.Voice, CancellationToken.None, cacheFolder: plan.CacheFolder);
        Check(handler.Calls == 2, "Interactive generation consumes preloaded audio without HTTP");
        File.WriteAllText(job.CacheFile, "broken");
        await BatchDubbing.Run(BatchDubbing.PlanPath(queue), CancellationToken.None, injectedClient: speech, reserveBytes: 0);
        Check(handler.Calls == 3 && SpeechClient.IsCached(job.CacheFile), "Corrupt outputs are regenerated on resume");
        File.Delete(job.CacheFile);
        await BatchDubbing.Run(BatchDubbing.PlanPath(queue), CancellationToken.None, injectedClient: speech, reserveBytes: long.MaxValue);
        Check(BatchDubbing.Read<BatchStatus>(Path.Combine(queue, "status.json")).State == "disk-low" && handler.Calls == 3, "Low disk stops before inference");
        BatchDubbing.Pause(queue);
        await BatchDubbing.Run(BatchDubbing.PlanPath(queue), CancellationToken.None, injectedClient: speech, reserveBytes: 0);
        Check(handler.Calls == 3 && BatchDubbing.Read<BatchStatus>(Path.Combine(queue, "status.json")).State == "paused", "Pause request prevents next sentence");
        using (var locked = File.Open(Path.Combine(queue, "worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(BatchDubbing.IsRunning(queue), "Exclusive lock detects another worker");
        var mixedFolder = Path.Combine(root, "mixed"); Directory.CreateDirectory(mixedFolder);
        var reference = Path.Combine(root, "reference.wav"); File.WriteAllBytes(reference, new byte[100]);
        var cloneVoice = new VoiceProfile { ReferenceAudio = reference, Language = "ja" };
        var mixed = new[] { job, job with { Speaker = "Clone B", Voice = cloneVoice, Fallback = false, Source = "clone-b", CacheFile = "clone-b.wav" },
            job with { Speaker = "Clone A", Voice = cloneVoice, Fallback = false, Source = "clone-a", CacheFile = "clone-a.wav" } };
        var mixedSource = Path.Combine(mixedFolder, "original.jsonl");
        var originals = mixed.Select(j => JsonSerializer.Serialize(j, DialogueIndex.Json)).ToArray();
        File.WriteAllLines(mixedSource, originals);
        BatchDubbing.Write(BatchDubbing.PlanPath(mixedFolder), plan with { Jobs = 3, JobsFile = mixedSource });
        var reordered = BatchDubbing.PrioritizeReferences(mixedFolder, CancellationToken.None);
        var reorderedLines = File.ReadAllLines(reordered.JobsFile);
        Check(reordered.ReferenceJobs == 2 && reordered.DefaultJobs == 1 && reordered.MissingReferences == 0, "REF/default counts match queue");
        Check(reorderedLines.SequenceEqual(new[] { originals[1], originals[2], originals[0] }), "Reference jobs come first, keeping stable order and unchanged cache/profile identities");
        Check(File.ReadAllLines(mixedSource).SequenceEqual(originals), "Reordering preserves old queue snapshot");
        using (var locked = File.Open(Path.Combine(mixedFolder, "worker.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool rejected = false;
            try { BatchDubbing.PrioritizeReferences(mixedFolder, CancellationToken.None); } catch (IOException) { rejected = true; }
            Check(rejected, "Cannot reorder an active worker queue");
        }
        Check(plan.Ordering == "reference-first", "Newly prepared queues default to references first");
        var nativeFolder = Path.Combine(root, "native-check"); Directory.CreateDirectory(nativeFolder);
        var nativeJob = job with { Text = "Game already speaks", CacheFile = "intentionally-unused-cache.wav" };
        var nativeJobs = Path.Combine(nativeFolder, "old.jsonl");
        File.WriteAllLines(nativeJobs, new[] { nativeJob, mixed[1] }.Select(j => JsonSerializer.Serialize(j, DialogueIndex.Json)));
        BatchDubbing.Write(BatchDubbing.PlanPath(nativeFolder), plan with { Jobs = 2, JobsFile = nativeJobs });
        var filtered = BatchDubbing.ExcludeNativeAudio(nativeFolder, catalogPath, CancellationToken.None);
        Check(filtered.Jobs == 1 && filtered.ReferenceJobs == 1 && File.ReadAllLines(nativeJobs).Length == 2, "Existing queue removes native sentences while retaining REF work and snapshot");
        File.WriteAllText(nativeJobs, JsonSerializer.Serialize(nativeJob, DialogueIndex.Json) + Environment.NewLine);
        BatchDubbing.Write(BatchDubbing.PlanPath(nativeFolder), plan with { Jobs = 1, JobsFile = nativeJobs });
        var calls = handler.Calls;
        await BatchDubbing.Run(BatchDubbing.PlanPath(nativeFolder), CancellationToken.None, injectedClient: speech, reserveBytes: 0);
        var skipped = BatchDubbing.Read<BatchStatus>(Path.Combine(nativeFolder, "status.json"));
        Check(skipped.State == "completed" && skipped.NativeSkipped == 1 && handler.Calls == calls, "Old queues skip native sentences before cache identity validation or generation");
        Console.WriteLine("PASS: dialogue classification, batch deduplication, default direction, pause/resume, disk guard, corrupt-cache recovery, interactive cache reuse.");
    }
    private sealed class WavHandler : HttpMessageHandler
    {
        public int Calls; public string LastPriority = ""; public bool LastReferenceIsNull;
        public uint LastSeed;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement;
            LastPriority = body.GetProperty("priority").GetString()!; LastReferenceIsNull = body.GetProperty("reference_audio").ValueKind == JsonValueKind.Null;
            LastSeed = body.GetProperty("seed").GetUInt32();
            var audio = new byte[100]; Encoding.ASCII.GetBytes("RIFF").CopyTo(audio, 0); Encoding.ASCII.GetBytes("WAVE").CopyTo(audio, 8);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(audio) };
        }
    }
}
