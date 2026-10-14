using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using NAudio.Wave;

namespace FF14Sound;

public sealed record SpeechStreamResult(bool CacheHit, bool Streamed, double FirstAudioSeconds, int Chunks);

public sealed partial class SpeechClient
{
    public async Task<SpeechStreamResult> Stream(string server, string text, VoiceProfile voice, Func<byte[], Task> onAudio,
        CancellationToken token, int version, string cacheFolder)
    {
        var watch = Stopwatch.StartNew();
        using var cacheLease = SpeechCache.Lease(cacheFolder);
        using var voiceLock = await SpeechCache.LockVoice(cacheFolder, voice, token);
        var cache = await CachePath(server, text, voice, cacheFolder, token);
        if (IsCached(cache))
        {
            SpeechCache.Complete(cacheFolder, cache, voice, token);
            await DeliverWave(await File.ReadAllBytesAsync(cache, token), onAudio, token);
            return new(true, false, 0, 1);
        }
        var health = await client.GetFromJsonAsync<JsonElement>(Endpoint(server, "/api/health"), token);
        if (!health.TryGetProperty("streaming", out var supported) || !supported.GetBoolean())
        {
            voiceLock?.Dispose(); cacheLease.Dispose();
            var wave = await Generate(server, text, voice, token, version, cacheFolder);
            var first = watch.Elapsed.TotalSeconds;
            await DeliverWave(wave, onAudio, token);
            return new(false, false, first, 1);
        }
        var reference = voice.IsFallback ? SpeechCache.LearnedReference(cacheFolder, voice) : voice.ReferenceAudio;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(server, "/api/tts/stream"))
        {
            Content = JsonContent.Create(new { text, language = voice.Language, reference_audio = reference,
                transcript = voice.IsFallback ? "" : voice.Transcript, control = voice.Control, seed = voice.EffectiveSeed,
                session_id = SessionId, revision = version, priority = "interactive" })
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict) throw new OperationCanceledException(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(await response.Content.ReadAsStringAsync(token));
        if (response.Content.Headers.ContentType?.MediaType != "application/x-ndjson") throw new InvalidDataException("Unexpected streaming format.");
        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        using var pcm = new MemoryStream();
        bool format = false, done = false;
        int chunks = 0; double firstAudio = 0;
        while (await reader.ReadLineAsync(token) is { } line)
        {
            token.ThrowIfCancellationRequested();
            using var item = JsonDocument.Parse(line);
            var value = item.RootElement;
            switch (value.GetProperty("type").GetString())
            {
                case "format":
                    if (format || chunks > 0 || value.GetProperty("sample_rate").GetInt32() != 48000 ||
                        value.GetProperty("channels").GetInt32() != 1 || value.GetProperty("bits").GetInt32() != 16 ||
                        value.GetProperty("encoding").GetString() != "pcm_s16le") throw new InvalidDataException("Unsupported streaming PCM format.");
                    format = true; break;
                case "audio":
                    if (!format) throw new InvalidDataException("Missing streaming format.");
                    var bytes = value.GetProperty("pcm").GetBytesFromBase64();
                    if (bytes.Length == 0 || bytes.Length % 2 != 0 || pcm.Length + bytes.Length > 64 * 1024 * 1024)
                        throw new InvalidDataException("Invalid streaming audio chunk.");
                    pcm.Write(bytes);
                    if (chunks++ == 0) firstAudio = watch.Elapsed.TotalSeconds;
                    await onAudio(bytes);
                    break;
                case "done": done = true; break;
                case "cancelled": throw new OperationCanceledException("Dialogue superseded.", token);
                case "error": throw new InvalidOperationException(value.GetProperty("message").GetString());
                default: throw new InvalidDataException("Unknown streaming message.");
            }
            if (done) break;
        }
        token.ThrowIfCancellationRequested();
        if (!done || chunks == 0) throw new InvalidDataException("串流未完整完成；不會保存殘缺配音。");
        // A done marker is mandatory: disconnects/errors/cancelled speech never become valid cached WAVs.
        Directory.CreateDirectory(cacheFolder);
        var temporary = cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(temporary, new WaveFormat(48000, 16, 1)))
            { pcm.Position = 0; pcm.CopyTo(writer); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, cache, true);
        }
        finally { File.Delete(temporary); }
        SpeechCache.Complete(cacheFolder, cache, voice, token);
        return new(false, true, firstAudio, chunks);
    }
    internal static async Task DeliverWave(byte[] wave, Func<byte[], Task> onAudio, CancellationToken token)
    {
        using var input = new WaveFileReader(new MemoryStream(wave));
        if (input.WaveFormat.Encoding != WaveFormatEncoding.Pcm || input.WaveFormat.SampleRate != 48000 || input.WaveFormat.Channels != 1 || input.WaveFormat.BitsPerSample != 16)
            throw new InvalidDataException("Expected 48 kHz mono PCM16 cached audio.");
        var buffer = new byte[16384];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        { token.ThrowIfCancellationRequested(); await onAudio(buffer.AsSpan(0, read).ToArray()); }
    }
}
