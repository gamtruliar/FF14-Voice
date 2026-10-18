using System.Buffers.Binary;
using NAudio.Wave;

namespace FF14Sound;

/// <summary>Playback-only gain, applied as queued audio is read; cached PCM stays unchanged.</summary>
public sealed class PcmGainProvider(IWaveProvider source) : IWaveProvider
{
    private float gain = 1;
    private PlaybackLevelSettings levels = new();
    private double automaticGain = 1, limiterGain = 1, meanSquare;
    private double[]? delayed;
    private (long Index, double Bound)[] constraints = [];
    private long position;
    private int head, size, remainingTail = -1;
    public const int LookAheadMilliseconds = 5;
    public PlaybackLevelSettings Levels
    {
        get => Volatile.Read(ref levels);
        set => Volatile.Write(ref levels, value.Validated());
    }
    public WaveFormat WaveFormat => source.WaveFormat;
    public float Gain
    {
        get => Volatile.Read(ref gain);
        set => Volatile.Write(ref gain, float.IsFinite(value) ? Math.Clamp(value, 0, 4) : 1);
    }
    public int Read(byte[] buffer, int offset, int count)
    {
        if (WaveFormat.Encoding != WaveFormatEncoding.Pcm || WaveFormat.BitsPerSample != 16)
            throw new InvalidOperationException("Playback gain requires PCM16.");
        if (count % 2 != 0) throw new ArgumentException("PCM16 reads must contain complete samples.", nameof(count));
        if (count == 0) return 0;
        var read = source.Read(buffer, offset, count);
        if (read % 2 != 0) throw new InvalidOperationException("Incomplete PCM16 sample.");
        var multiplier = Gain;
        var limits = Levels;
        if (limits.Enabled || delayed is not null)
        {
            if (delayed is null)
            {
                var delay = Math.Max(1, WaveFormat.SampleRate * WaveFormat.Channels * LookAheadMilliseconds / 1000);
                delayed = new double[delay + 1]; constraints = new (long, double)[delay + 2];
            }
            if (read == 0)
            {
                // Keep every delayed sample when the source ends, including a very short utterance.
                if (remainingTail < 0) remainingTail = position > 0 ? delayed.Length - 1 : 0;
                read = Math.Min(count, remainingTail * 2);
                if (read == 0) return 0;
                buffer.AsSpan(offset, read).Clear(); remainingTail -= read / 2;
            }
            else remainingTail = -1;
            Adjust(buffer, offset, read, multiplier, limits);
            return read;
        }
        automaticGain = limiterGain = 1;
        if (multiplier == 1) return read;
        for (var i = offset; i < offset + read; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2));
            var amplified = (short)Math.Clamp(MathF.Round(sample * multiplier), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i, 2), amplified);
        }
        return read;
    }

    private void Adjust(byte[] buffer, int offset, int read, float manual, PlaybackLevelSettings limits)
    {
        // Continuous detector and gain state: output is independent of WaveOut/network read boundaries.
        // Slow gain recovery preserves syllables rather than lifting each gap between them.
        var rate = WaveFormat.SampleRate * WaveFormat.Channels;
        var detector = Math.Exp(-1.0 / (rate * .1));
        var attack = Math.Exp(-1.0 / (rate * .08));
        var recovery = Math.Exp(-1.0 / (rate * .8));
        var limiterRelease = Math.Exp(-1.0 / (rate * .15));
        var minimum = Math.Pow(10, limits.MinimumDb / 20);
        var maximum = Math.Pow(10, limits.MaximumDb / 20);
        var ceiling = limits.Enabled ? Math.Floor(Math.Pow(10, limits.PeakDb / 20) * 32768) / 32768 : 32767.0 / 32768;
        const double maxBoost = 3.9810717055349722; // +12 dB, automatic gain only.
        const double noiseFloor = 0.0031622776601683794; // -50 dBFS: do not lift silence/noise.
        var delay = delayed!.Length - 1;
        var slope = 1.0 / delay;
        for (var i = offset; i < offset + read; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2)) / 32768.0;
            meanSquare = detector * meanSquare + (1 - detector) * sample * sample;
            var rawRms = Math.Sqrt(meanSquare);
            var rms = rawRms * manual;
            var desired = !limits.Enabled ? 1 : rawRms <= noiseFloor || manual == 0 ? Math.Min(automaticGain, 1)
                : rms < minimum ? Math.Min(maxBoost, minimum / rms)
                : rms > maximum ? maximum / rms : 1;
            var coefficient = desired < automaticGain ? attack : recovery;
            automaticGain = desired + (automaticGain - desired) * coefficient;
            var amplified = sample * manual * automaticGain;
            delayed[position % delayed.Length] = amplified;

            // For each upcoming peak j, constrain gain at k by required[j] + (j-k)/delay.
            // Taking the minimum anticipates every peak with a bounded downward ramp. A monotone
            // queue keeps this O(1) per sample. No block-edge gain steps or hard peak clipping.
            var outputPosition = position - delay;
            while (size > 0 && constraints[head].Index < outputPosition) { head = (head + 1) % constraints.Length; size--; }
            var required = Math.Abs(amplified) > ceiling ? ceiling / Math.Abs(amplified) : 1;
            var bound = required + position * slope;
            while (size > 0 && constraints[(head + size - 1) % constraints.Length].Bound >= bound) size--;
            constraints[(head + size) % constraints.Length] = (position, bound); size++;
            double value = 0;
            if (outputPosition >= 0)
            {
                var allowed = Math.Clamp(constraints[head].Bound - outputPosition * slope, 0, 1);
                limiterGain = Math.Min(allowed, 1 - (1 - limiterGain) * limiterRelease);
                value = delayed[outputPosition % delayed.Length] * limiterGain;
            }
            position++;
            var adjusted = manual == 0 ? (short)0 : (short)Math.Clamp(Math.Round(value * 32768), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i, 2), adjusted);
        }
    }
}
