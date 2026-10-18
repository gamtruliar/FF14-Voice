using System.Buffers.Binary;
using NAudio.Wave;
using SoundTouch;

namespace FF14Sound;

/// <summary>Playback tempo only. Processor state belongs to the audio read thread.</summary>
public sealed class PcmTempoProvider : IWaveProvider
{
    private readonly IWaveProvider source;
    private readonly Func<bool> inputComplete;
    private readonly byte[] input = new byte[8192];
    private readonly float[] samples = new float[4096];
    private SoundTouchProcessor? processor;
    private double speed = 1;
    private bool flushed, discarded;
    public const double MinimumSpeed = .75, MaximumSpeed = 1.5;
    public static double ValidateSpeed(double value) => double.IsFinite(value) ? Math.Clamp(value, MinimumSpeed, MaximumSpeed) : 1;
    public double Speed { get => Volatile.Read(ref speed); set => Volatile.Write(ref speed, ValidateSpeed(value)); }
    public WaveFormat WaveFormat => source.WaveFormat;

    public PcmTempoProvider(IWaveProvider source, Func<bool> inputComplete)
    {
        if (source.WaveFormat.Encoding != WaveFormatEncoding.Pcm || source.WaveFormat.SampleRate != 48000 ||
            source.WaveFormat.BitsPerSample != 16 || source.WaveFormat.Channels != 1)
            throw new ArgumentException("Tempo playback requires 48 kHz mono PCM16.", nameof(source));
        this.source = source; this.inputComplete = inputComplete;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        if (count % 2 != 0) throw new ArgumentException("PCM16 reads must contain complete samples.", nameof(count));
        if (discarded || count == 0) return 0;
        var tempo = Speed;
        if (processor is null && tempo == 1)
        {
            var complete = inputComplete();
            var read = source.Read(buffer, offset, count);
            if (read == count || complete) return read;
            // A network pause is not EOF. Pad output only; do not time-stretch synthetic silence.
            buffer.AsSpan(offset + read, count - read).Clear(); return count;
        }
        processor ??= new SoundTouchProcessor { SampleRate = 48000, Channels = 1 };
        processor.Tempo = tempo; // Rate/Pitch stay at 1. UI writes only the volatile setting above.
        var written = 0;
        while (written < count)
        {
            var received = processor.ReceiveSamples(samples.AsSpan(), Math.Min(samples.Length, (count - written) / 2));
            for (var i = 0; i < received; i++)
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(offset + written + i * 2, 2),
                    (short)Math.Clamp(MathF.Round(samples[i] * 32768), short.MinValue, short.MaxValue));
            written += received * 2;
            if (written == count || flushed && received == 0) break;
            if (received > 0) continue;
            // Snapshot completion BEFORE reading. Otherwise Finish could race an empty read
            // and make us flush while its last input chunk is still waiting in the source.
            var complete = inputComplete();
            var read = source.Read(input, 0, input.Length);
            if (read % 2 != 0) throw new InvalidOperationException("Incomplete PCM sample.");
            if (read > 0)
            {
                for (var i = 0; i < read / 2; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(input.AsSpan(i * 2, 2)) / 32768f;
                processor.PutSamples(samples.AsSpan(0, read / 2), read / 2);
            }
            else if (complete) { processor.Flush(); flushed = true; }
            else { buffer.AsSpan(offset + written, count - written).Clear(); return count; }
        }
        return written;
    }

    // Called after the output device has stopped; no queued tempo samples may leak to the next line.
    public void Discard() { processor?.Clear(); discarded = true; }
}
