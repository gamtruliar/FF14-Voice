using NAudio.Wave;
using System.IO;

namespace FF14Sound;

/// <summary>One dialogue owns one playback buffer. Disposing it stops and discards all queued audio.</summary>
public sealed class StreamingPcmPlayer : IDisposable
{
    private readonly BufferedWaveProvider buffer = new(new WaveFormat(48000, 16, 1))
    { BufferDuration = TimeSpan.FromSeconds(30), ReadFully = false, DiscardOnBufferOverflow = false };
    private readonly WaveOutEvent output = new() { DesiredLatency = 120, NumberOfBuffers = 3 };
    private readonly TaskCompletionSource ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly PcmGainProvider gainProvider;
    private readonly PcmTempoProvider tempoProvider;
    private bool inputComplete;
    public double Speed { get => tempoProvider.Speed; set => tempoProvider.Speed = value; }
    public float Gain { get => gainProvider.Gain; set => gainProvider.Gain = value; }
    public PlaybackLevelSettings Levels { get => gainProvider.Levels; set => gainProvider.Levels = value; }
    private bool started, disposed;
    public StreamingPcmPlayer()
    {
        tempoProvider = new(buffer, () => Volatile.Read(ref inputComplete));
        gainProvider = new(tempoProvider);
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
        Volatile.Write(ref inputComplete, true);
        await ended.Task.WaitAsync(token);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        output.Stop(); buffer.ClearBuffer(); output.Dispose(); tempoProvider.Discard(); ended.TrySetCanceled();
    }
}
