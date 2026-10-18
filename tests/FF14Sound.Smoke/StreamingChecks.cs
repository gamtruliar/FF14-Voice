using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using FF14Sound;
using NAudio.Wave;

internal static class StreamingChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static async Task Live(string folder)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        // Exercise the actual Windows output device with silence, including immediate buffer disposal.
        using (var player = new StreamingPcmPlayer())
        {
            await player.Add(new byte[9600], deadline.Token);
            await player.Finish(deadline.Token);
        }
        var stopped = new StreamingPcmPlayer();
        await stopped.Add(new byte[96000 * 3], deadline.Token);
        stopped.Dispose();
        try { await stopped.Add(new byte[2], deadline.Token); throw new Exception("Disposed player accepted PCM"); }
        catch (OperationCanceledException) { }
        using var speech = new SpeechClient();
        var voice = new VoiceProfile { IsFallback = true, Language = "ja", Control = "Adult male, calm." };
        var cache = Path.Combine(folder, "live-stream-" + Guid.NewGuid().ToString("N"));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        try
        {
            await speech.Stream("http://localhost:8808", "こんにちは。旅の準備はできましたか。", voice,
                _ => { cancel.Cancel(); return Task.CompletedTask; }, cancel.Token, 1, cache);
            throw new Exception("Live stream ignored cancellation");
        }
        catch (OperationCanceledException) { }
        Check(!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "*.wav").Any(), "Live cancellation cached partial speech");
        await speech.CancelPending("http://localhost:8808", 2);
        var result = await speech.Stream("http://localhost:8808", "こんにちは。旅の準備はできましたか。", voice,
            _ => Task.CompletedTask, deadline.Token, 2, cache);
        Check(result.Streamed && result.Chunks > 1, "Live stream must deliver multiple chunks after cancellation");
        Console.WriteLine($"PASS: Windows PCM output/drain/stop; real HTTP cancel then stream: first={result.FirstAudioSeconds:F3}s chunks={result.Chunks}");
    }
    public static async Task Run(string folder)
    {
        TempoChecks.Run();
        var original = new byte[] { 0xe8, 0x03, 0x18, 0xfc, 0x20, 0x4e, 0xe0, 0xb1, 0xf4, 0x01, 0x0c, 0xfe };
        var source = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { ReadFully = false };
        source.AddSamples(original, 0, original.Length);
        var gain = new PcmGainProvider(source) { Gain = 2 };
        var output = new byte[8];
        Check(gain.Read(output, 2, 4) == 4 && BitConverter.ToInt16(output, 2) == 2000 && BitConverter.ToInt16(output, 4) == -2000,
            "200% amplifies positive/negative samples and respects output offset");
        gain.Gain = 4;
        gain.Read(output, 0, 4);
        Check(BitConverter.ToInt16(output, 0) == short.MaxValue && BitConverter.ToInt16(output, 2) == short.MinValue,
            "Amplified peaks saturate instead of wrapping into opposite polarity");
        gain.Gain = 0;
        gain.Read(output, 0, 4);
        Check(output.Take(4).All(x => x == 0), "Live volume update mutes already buffered audio");
        Check(BitConverter.ToInt16(original, 0) == 1000, "Playback gain must not alter original PCM/cache");
        var voice = new VoiceProfile { IsFallback = true, Language = "ja", Control = "adult male", ReferenceAudio = "must-not-clone.wav", Transcript = "must-not-send", Seed = uint.MaxValue };
        foreach (var outcome in new[] { "done", "eof", "error", "cancel" })
        {
            var body = new Feed();
            var handler = new Handler(body);
            using var client = new SpeechClient(handler);
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var cache = Path.Combine(folder, "stream-" + outcome);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var delivered = new MemoryStream();
            var run = client.Stream("http://localhost:8808", outcome, voice, bytes =>
            { delivered.Write(bytes); first.TrySetResult(); return Task.CompletedTask; }, cancel.Token, 1, cache);
            body.Put("{\"type\":\"format\",\"sample_rate\":48000,\"channels\":1,\"bits\":16,\"encoding\":\"pcm_s16le\"}");
            var pcm = new byte[] { 1, 2, 3, 4 };
            body.Put(JsonSerializer.Serialize(new { type = "audio", pcm = Convert.ToBase64String(pcm) }));
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!run.IsCompleted && delivered.Length == 4, "First PCM must reach playback before final marker/EOF");
            Check(!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "*.wav").Any(), "Partial audio must not be cached");
            using (var sent = JsonDocument.Parse(handler.Payload!))
            {
                Check(sent.RootElement.GetProperty("reference_audio").ValueKind == JsonValueKind.Null, "Fallback streaming must not clone");
                Check(sent.RootElement.GetProperty("transcript").GetString() == "", "Fallback streaming must not continue reference transcript");
                Check(sent.RootElement.GetProperty("seed").GetUInt32() == voice.EffectiveSeed, "Streaming sends the same fixed character seed");
            }
            if (outcome == "cancel") cancel.Cancel();
            else
            {
                if (outcome == "done") body.Put("{\"type\":\"done\"}");
                if (outcome == "error") body.Put("{\"type\":\"error\",\"message\":\"inference failed\"}");
                body.End();
            }
            Exception? failure = null;
            try { await run; } catch (Exception ex) { failure = ex; }
            if (outcome == "done")
            {
                Check(failure is null && run.Result.Streamed, "Successful stream result");
                using var wav = new WaveFileReader(Directory.GetFiles(cache, "*.wav").Single());
                Check(wav.Length == 4 && wav.WaveFormat.SampleRate == 48000, "Complete stream cached as valid PCM WAV");
                var requests = handler.Requests;
                var hit = await client.Stream("http://localhost:8808", outcome, voice, _ => Task.CompletedTask, CancellationToken.None, 2, cache);
                Check(hit.CacheHit && handler.Requests == requests, "Streaming cache replay makes no HTTP request");
            }
            else
            {
                Check(failure is not null, "Incomplete stream must fail");
                Check(!Directory.Exists(cache) || !Directory.EnumerateFiles(cache, "*.wav").Any(), "Failed/cancelled stream leaves no cached file");
                if (outcome == "cancel") Check(failure is OperationCanceledException, "Cancellation interrupts pending stream read");
            }
        }
        Console.WriteLine("Streaming protocol/cache/cancellation checks passed.");
    }
    private sealed class Handler(Feed body) : HttpMessageHandler
    {
        public string? Payload;
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            if (request.RequestUri!.AbsolutePath == "/api/health")
                return new(HttpStatusCode.OK) { Content = new StringContent("{\"streaming\":true}", Encoding.UTF8, "application/json") };
            Payload = await request.Content!.ReadAsStringAsync(token);
            var content = new StreamContent(body);
            content.Headers.ContentType = new("application/x-ndjson");
            return new(HttpStatusCode.OK) { Content = content };
        }
    }
    private sealed class Feed : Stream
    {
        private readonly Channel<byte[]> channel = Channel.CreateUnbounded<byte[]>();
        private byte[] current = []; private int offset;
        public void Put(string line) => channel.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));
        public void End() => channel.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> target, CancellationToken token = default)
        {
            if (offset == current.Length)
            {
                if (!await channel.Reader.WaitToReadAsync(token)) return 0;
                current = await channel.Reader.ReadAsync(token); offset = 0;
            }
            var length = Math.Min(target.Length, current.Length - offset);
            current.AsMemory(offset, length).CopyTo(target); offset += length; return length;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
