using System.Buffers.Binary;
using System.IO;
using FF14Sound.Voices;
using NAudio.Wave;

namespace FF14Sound;

/// <summary>Generate independent human voices and mix them; never pitch-shift a single shriek into a crowd.</summary>
public static class GroupSpeech
{
    public const string CacheMode = "ensemble-v1";
    public static bool Applies(VoiceProfile voice) => voice.IsFallback && (voice.IsGroup || CollectiveSpeaker.IsCollective(voice.Speaker));
    private static readonly string[] Parts =
    [
        "成年男聲，穩定自然嘅中低音，胸腔共鳴厚實。",
        "成年男聲，自然中音，音色明亮結實。",
        "成年女聲，自然中音，音色圓潤飽滿。"
    ];

    public static async Task<byte[]> Render(VoiceProfile group, Func<VoiceProfile, Task<byte[]>> generate, CancellationToken token)
    {
        var takes = new List<byte[]>();
        for (var i = 0; i < Parts.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var member = new VoiceProfile
            {
                Speaker = group.Speaker + " · 聲部 " + (i + 1), Language = group.Language, IsFallback = true,
                Control = Parts[i] + "一個人清楚讀出整句，正常音域，穩定氣息，節奏俐落，句尾收乾淨。",
                UtteranceTone = group.UtteranceTone,
                VoiceIdentity = group.EffectiveVoiceIdentity + ":" + CacheMode + ":" + i,
                Seed = VoiceSeed.For(group.EffectiveSeed + ":" + CacheMode + ":" + i)
            };
            // No learned reference or per-member sentence cache: only the complete group is persisted.
            takes.Add(await generate(member));
        }
        token.ThrowIfCancellationRequested();
        return Mix(takes, token);
    }

    private static byte[] ReadPcm(byte[] wave)
    {
        using var reader = new WaveFileReader(new MemoryStream(wave));
        var format = reader.WaveFormat;
        if (format.Encoding != WaveFormatEncoding.Pcm || format.SampleRate != 48000 || format.BitsPerSample != 16 || format.Channels != 1)
            throw new InvalidDataException("多人配音需要 48 kHz mono PCM16。");
        var pcm = new byte[checked((int)reader.Length)];
        reader.ReadExactly(pcm);
        int first = 0, last = pcm.Length - 2;
        while (first <= last && Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(first, 2))) < 160) first += 2;
        while (last >= first && Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(last, 2))) < 160) last -= 2;
        if (last < first) throw new InvalidDataException("多人配音有聲部冇有效人聲。");
        // Retain 20 ms around the spoken section; align the independently generated starts.
        first = Math.Max(0, first - 1920); last = Math.Min(pcm.Length - 2, last + 1920);
        return pcm.AsSpan(first, last - first + 2).ToArray();
    }

    public static byte[] Mix(IReadOnlyList<byte[]> waves, CancellationToken token)
    {
        if (waves.Count != Parts.Length) throw new ArgumentException("Expected three group voices.", nameof(waves));
        var takes = waves.Select(ReadPcm).ToArray();
        var medianLength = takes.Select(t => t.Length).Order().ElementAt(takes.Length / 2);
        for (var i = 0; i < takes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            var speed = PcmTempoProvider.ValidateSpeed((double)takes[i].Length / medianLength);
            using var source = new RawSourceWaveStream(new MemoryStream(takes[i]), new WaveFormat(48000, 16, 1));
            var tempo = new PcmTempoProvider(source, () => true) { Speed = speed };
            using var aligned = new MemoryStream();
            var block = new byte[8192];
            try
            {
                int read;
                while ((read = tempo.Read(block, 0, block.Length)) > 0)
                { token.ThrowIfCancellationRequested(); aligned.Write(block, 0, read); }
            }
            finally { tempo.Discard(); }
            takes[i] = aligned.ToArray();
        }
        // 0/25/50 ms separation gives distinct people without a long artificial echo.
        var samples = new double[takes.Select((t, i) => t.Length / 2 + i * 1200).Max()];
        var weight = .65 / Math.Sqrt(takes.Length);
        for (var i = 0; i < takes.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            for (var j = 0; j < takes[i].Length / 2; j++)
                samples[j + i * 1200] += BinaryPrimitives.ReadInt16LittleEndian(takes[i].AsSpan(j * 2, 2)) / 32768.0 * weight;
        }
        var peak = samples.Max(Math.Abs);
        var attenuation = peak > .707 ? .707 / peak : 1;
        var mixed = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
        {
            if (i % 48000 == 0) token.ThrowIfCancellationRequested();
            // 5 ms fade at the edges avoids trimming/mix clicks.
            var fade = Math.Min(1, Math.Min(i, samples.Length - 1 - i) / 240.0);
            BinaryPrimitives.WriteInt16LittleEndian(mixed.AsSpan(i * 2, 2), (short)Math.Round(samples[i] * attenuation * fade * 32768));
        }
        using var result = new MemoryStream();
        using var writer = new WaveFileWriter(result, new WaveFormat(48000, 16, 1));
        writer.Write(mixed); writer.Flush();
        return result.ToArray();
    }
}
