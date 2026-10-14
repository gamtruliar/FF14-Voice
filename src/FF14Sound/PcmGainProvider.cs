using System.Buffers.Binary;
using NAudio.Wave;

namespace FF14Sound;

/// <summary>Playback-only gain, applied as queued audio is read; cached PCM stays unchanged.</summary>
public sealed class PcmGainProvider(IWaveProvider source) : IWaveProvider
{
    private float gain = 1;
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
        var read = source.Read(buffer, offset, count);
        var multiplier = Gain;
        if (multiplier == 1) return read;
        for (var i = offset; i < offset + read; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i, 2));
            var amplified = (short)Math.Clamp(MathF.Round(sample * multiplier), short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i, 2), amplified);
        }
        return read;
    }
}
