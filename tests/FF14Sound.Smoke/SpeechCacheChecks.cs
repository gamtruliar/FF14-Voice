using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FF14Sound;
using NAudio.Wave;

internal static class SpeechCacheChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static VoiceProfile Voice(string name) => new() { Speaker = name, IsFallback = true, Language = "ja", Control = "成年男聲", Seed = 123 };
    public static async Task Live(string folder)
    {
        var cache = Path.Combine(folder, "learned-live-" + Guid.NewGuid().ToString("N"));
        var voice = Voice("聲線連續性實測 NPC");
        using var client = new SpeechClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        await client.Stream("http://localhost:8808", "こんにちは。旅の準備はできましたか。", voice, _ => Task.CompletedTask, deadline.Token, 1, cache);
        var reference = SpeechCache.LearnedReference(cache, voice);
        Check(reference is not null, "Live first utterance creates a reference");
        var original = File.ReadAllBytes(reference!);
        await client.Stream("http://localhost:8808", "それでは、次の町へ向かいましょう。", voice, _ => Task.CompletedTask, deadline.Token, 2, cache);
        Check(original.SequenceEqual(File.ReadAllBytes(reference!)), "Live second utterance preserves first voice sample");
        Check(SpeechCache.Scan(cache).Single() is { Sentences: 2, References: 1 }, "Live cloned second utterance cached");
        Console.WriteLine("PASS: real Omni first utterance -> learned reference -> second streamed utterance; samples: " + cache);
    }
    public static byte[] Wave()
    {
        using var output = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(output), new WaveFormat(48000, 16, 1)))
        {
            var pcm = new byte[48000];
            for (int i = 0; i < pcm.Length; i += 2) BitConverter.GetBytes((short)(2000 * Math.Sin(i * .05))).CopyTo(pcm, i);
            writer.Write(pcm, 0, pcm.Length);
        }
        return output.ToArray();
    }
    public static async Task Run(string root)
    {
        var folder = Path.Combine(root, "learned-cache");
        var handler = new Handler(); using var speech = new SpeechClient(handler);
        var a = Voice("NPC A"); var b = Voice("NPC B");
        var before = await speech.CachePath("http://localhost:8808", "one", a, folder, CancellationToken.None);
        await speech.Generate("http://localhost:8808", "one", a, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Single() is null, "First sentence uses description without a generic clone");
        var reference = SpeechCache.LearnedReference(folder, a);
        Check(reference is not null && File.Exists(reference), "Completed first sentence becomes character reference");
        var saved = File.ReadAllBytes(reference!);
        await speech.Generate("http://localhost:8808", "two", a, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Last() == reference && File.ReadAllBytes(reference!).SequenceEqual(saved), "Next sentence clones immutable first take");
        var requests = handler.References.Count;
        await speech.Generate("http://localhost:8808", "one", a, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Count == requests && before == await speech.CachePath("http://localhost:8808", "one", a, folder, CancellationToken.None), "First take remains a cache hit after voice learning");
        await speech.Generate("http://localhost:8808", "one", b, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Last() is null && SpeechCache.LearnedReference(folder, b) != reference, "Two NPCs with identical descriptions/seeds never share first reference");
        var c = Voice("Stream NPC");
        await speech.Stream("http://localhost:8808", "first stream", c, _ => Task.CompletedTask, CancellationToken.None, 1, folder);
        Check(handler.References.Last() is null && SpeechCache.LearnedReference(folder, c) is not null, "Complete stream learns first reference");
        await speech.Stream("http://localhost:8808", "next stream", c, _ => Task.CompletedTask, CancellationToken.None, 2, folder);
        Check(handler.References.Last() == SpeechCache.LearnedReference(folder, c), "Streaming clones the same learned voice");
        var cancelled = Voice("Cancelled NPC");
        using var cancel = new CancellationTokenSource();
        try { await speech.Stream("http://localhost:8808", "cancel", cancelled, _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token, 3, folder); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(SpeechCache.LearnedReference(folder, cancelled) is null && !SpeechCache.Scan(folder).Any(g => g.VoiceKey == cancelled.EffectiveVoiceIdentity), "Cancelled stream never creates cache or reference");
        var concurrent = Voice("Concurrent NPC");
        handler.Block = true;
        var first = speech.Generate("http://localhost:8808", "concurrent first", concurrent, CancellationToken.None, cacheFolder: folder);
        await handler.Started.Task;
        using var secondClient = new SpeechClient(handler);
        var second = secondClient.Generate("http://localhost:8808", "concurrent second", concurrent, CancellationToken.None, cacheFolder: folder);
        try { SpeechCache.Clear(folder, null, true); throw new Exception("Cleared during generation"); }
        catch (IOException) { }
        Check(!second.IsCompleted, "Concurrent first takes wait for one owner");
        handler.Release.SetResult(); await Task.WhenAll(first, second);
        Check(handler.References.TakeLast(2).First() is null && handler.References.Last() == SpeechCache.LearnedReference(folder, concurrent), "Waiting request uses the winning first take");
        var entries = SpeechCache.Scan(folder);
        Check(entries.Single(g => g.VoiceKey == a.EffectiveVoiceIdentity) is { Sentences: 2, References: 1 }, "Cache inventory groups actual sentences and reference per NPC");
        SpeechCache.Clear(folder, a.EffectiveVoiceIdentity, false);
        Check(File.Exists(reference) && SpeechCache.Scan(folder).Single(g => g.VoiceKey == a.EffectiveVoiceIdentity).Sentences == 0, "Per NPC clear preserves learned voice by default");
        Check(SpeechCache.Scan(folder).Single(g => g.VoiceKey == b.EffectiveVoiceIdentity).Sentences == 1, "Per NPC clear leaves another NPC untouched");
        SpeechCache.Clear(folder, a.EffectiveVoiceIdentity, true);
        Check(SpeechCache.LearnedReference(folder, a) is null, "Reset removes only learned reference");
        handler.Block = false;
        await speech.Generate("http://localhost:8808", "after reset", a, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Last() is null, "Reset character starts from description again");
        var originalReference = Path.Combine(root, "original-custom-reference.wav"); File.WriteAllBytes(originalReference, Wave());
        var custom = new VoiceProfile { Speaker = "Custom", ReferenceAudio = originalReference, Language = "ja" };
        await speech.Generate("http://localhost:8808", "custom line", custom, CancellationToken.None, cacheFolder: folder);
        Check(handler.References.Last() == originalReference && SpeechCache.LearnedReference(folder, custom) is null, "Explicit reference always wins and is never learned over");
        var unrelated = Path.Combine(folder, "notes.txt"); File.WriteAllText(unrelated, "keep");
        var unknown = Path.Combine(folder, new string('A', 64) + ".wav"); File.WriteAllBytes(unknown, Wave());
        Check(SpeechCache.Scan(folder).Any(g => g.Speaker.Contains("未識別")), "Legacy audio without ownership remains visible");
        var legacyJobs = Path.Combine(root, "legacy-jobs.jsonl");
        var legacyPlan = Path.Combine(root, "legacy-plan.json");
        File.WriteAllText(legacyJobs, JsonSerializer.Serialize(new BatchJob("Legacy NPC", "old line", Voice("Legacy NPC"), true, unknown, "old:1")));
        BatchDubbing.Write(legacyPlan, new BatchPlan("", "http://localhost:8808", folder, "ja", DateTimeOffset.Now, 1, 0, 1, new(), legacyJobs));
        using (var lease = SpeechCache.Lease(folder, true)) SpeechCache.ImportLegacyOwners(folder, legacyPlan);
        Check(SpeechCache.Scan(folder).Any(g => g.Speaker == "Legacy NPC" && g.Sentences == 1), "Old batch snapshots recover cache owner without regenerating audio");
        var noVoice = Voice("Silent NPC");
        var silence = Path.Combine(folder, new string('B', 64) + ".wav");
        using (var writer = new WaveFileWriter(silence, new WaveFormat(48000, 16, 1))) writer.Write(new byte[48000], 0, 48000);
        SpeechCache.Complete(folder, silence, noVoice, CancellationToken.None);
        Check(SpeechCache.LearnedReference(folder, noVoice) is null, "Silent completed audio is never a learned voice");
        try { SpeechCache.Complete(folder, originalReference, a, CancellationToken.None); throw new Exception("Outside path accepted"); }
        catch (IOException) { }
        SpeechCache.Clear(folder, null, true);
        Check(SpeechCache.Scan(folder).Length == 0 && File.Exists(originalReference) && File.Exists(unrelated), "Clear all only removes generated cache and optional learned refs");
        Console.WriteLine("PASS: first-voice learning, stream/cancel/concurrent generation, cache ownership, per-NPC/all clear and reference preservation.");
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string?> References = [];
        public bool Block;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/api/health") return new(HttpStatusCode.OK) { Content = new StringContent("{\"streaming\":true}", Encoding.UTF8, "application/json") };
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token)).RootElement;
            References.Add(body.GetProperty("reference_audio").GetString());
            if (Block) { Started.TrySetResult(); await Release.Task.WaitAsync(token); }
            if (request.RequestUri.AbsolutePath.EndsWith("/stream"))
            {
                using var wav = new WaveFileReader(new MemoryStream(Wave()));
                var pcm = new byte[wav.Length]; wav.ReadExactly(pcm);
                var lines = "{\"type\":\"format\",\"sample_rate\":48000,\"channels\":1,\"bits\":16,\"encoding\":\"pcm_s16le\"}\n" +
                    JsonSerializer.Serialize(new { type = "audio", pcm = Convert.ToBase64String(pcm) }) + "\n{\"type\":\"done\"}\n";
                return new(HttpStatusCode.OK) { Content = new StringContent(lines, Encoding.UTF8, "application/x-ndjson") };
            }
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(Wave()) };
        }
    }
    private sealed class IgnoreDisposeStream(Stream stream) : NAudio.Utils.IgnoreDisposeStream(stream) { }
}
