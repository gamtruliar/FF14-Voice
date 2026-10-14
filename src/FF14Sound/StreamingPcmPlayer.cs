using NAudio.Wave;
using System.IO;

namespace FF14Sound;

/// <summary>One dialogue owns one playback buffer. Disposing it stops and discards all queued audio.</summary>
public sealed class StreamingPcmPlayer : IDisposable
{
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48000, 16, 1))
    { BufferDuration = TimeSpan.FromSeconds(30), ReadFully = true, DiscardOnBufferOverflow = false };
    private readonly WaveOutEvent output = new() { DesiredLatency = 120, NumberOfBuffers = 3 };
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PcmGainProvider gainProvider;
    public float Gain { get => gainProvider.Gain; set => gainProvider.Gain = value; }
    private bool started, disposed;
    public StreamingPcmPlayer()
    {
        gainProvider = new(buffer);
        output.PlaybackStopped += (_, e) => { if (e.Exception is not null) ended.TrySetException(e.Exception); else ended.TrySetResult(); };
        try { output.Init(gainProvider); } catch { output.Dispose(); throw; }
    }
    public async Task Add(byte[] pcm, CancellationToken token)
    {
        if (pcm.Length % 2 != 0) throw new InvalidDataException("Incomplete PCM sample.");
        for (var offset = 0; offset < pcm.Length;)
        {
            token.ThrowIfCancellationRequested();
            if (disposed) throw new OperationCanceledException(token);
            var count = Math.Min(pcm.Length - offset, buffer.BufferLength - buffer.BufferedBytes) / 2 * 2;
            if (count == 0) { await Task.Delay(20, token); continue; }
            buffer.AddSamples(pcm, offset, count); offset += count;
            if (!started) { output.Play(); started = true; }
        }
    }
    public async Task Finish(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!started) return;
        buffer.ReadFully = false;
        await ended.Task.WaitAsync(token);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        output.Stop(); buffer.ClearBuffer(); output.Dispose(); ended.TrySetCanceled();
    }
}
