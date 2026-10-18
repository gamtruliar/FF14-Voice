using FF14Sound;
using NAudio.Wave;
using System.IO;

internal static class PlaybackLevelChecks
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static byte[] Tone(double amplitude, int seconds = 2)
    {
        var data = new byte[48000 * seconds * 2];
        for (var i = 0; i < data.Length / 2; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2, 2), (short)(32767 * amplitude * Math.Sin(2 * Math.PI * 200 * i / 48000)));
        return data;
    }
    private static double Rms(byte[] pcm) => Math.Sqrt(Enumerable.Range(pcm.Length / 4, pcm.Length / 4)
        .Average(i => Math.Pow(BitConverter.ToInt16(pcm, i * 2) / 32768.0, 2)));
    private static double Peak(byte[] pcm) => Enumerable.Range(0, pcm.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(pcm, i * 2))) / 32768.0;
    private static byte[] Process(byte[] pcm, PlaybackLevelSettings settings, float gain = 1, int chunkBytes = 1920)
    {
        var source = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(15) };
        var copy = pcm.ToArray(); source.AddSamples(pcm, 0, pcm.Length);
        var provider = new PcmGainProvider(source) { Levels = settings, Gain = gain };
        using var result = new MemoryStream(); var buffer = new byte[chunkBytes + 6];
        int read;
        while ((read = provider.Read(buffer, 4, chunkBytes)) > 0)
        {
            Check(buffer[0] == 0 && buffer[3] == 0 && buffer[^1] == 0, "AGC must respect read offset/count");
            result.Write(buffer, 4, read);
        }
        Check(copy.SequenceEqual(pcm), "AGC must not change cached/source PCM");
        return result.ToArray();
    }
    public static void Export(string input, string output)
    {
        using var wave = new WaveFileReader(input);
        if (wave.WaveFormat.SampleRate != 48000 || wave.WaveFormat.Channels != 1 || wave.WaveFormat.BitsPerSample != 16) throw new Exception("Expected mono 48 kHz PCM16");
        var pcm = new byte[(int)wave.Length]; wave.ReadExactly(pcm);
        var processed = Process(pcm, new(true, -27, -24, -6));
        using var writer = new WaveFileWriter(output, wave.WaveFormat);
        writer.Write(processed, 0, processed.Length);
        Console.WriteLine($"Volume review: {output}, {processed.Length / 96000.0:F3}s, peak={20 * Math.Log10(Peak(processed)):F2} dBFS");
    }
    public static void Run()
    {
        var levels = new PlaybackLevelSettings(true);
        var quiet = Process(Tone(.03), levels);
        var loud = Process(Tone(.8), levels, 4);
        var quietDb = 20 * Math.Log10(Rms(quiet)); var loudDb = 20 * Math.Log10(Rms(loud));
        Check(Math.Abs(quietDb - levels.MinimumDb) < 1, "Quiet speech should approach configured lower target");
        Check(Math.Abs(loudDb - levels.MaximumDb) < 1, "Loud speech should approach configured upper target even at 400% manual volume");
        var changing = Tone(.03, 1).Concat(Tone(.99, 1)).Concat(Tone(.03, 1)).ToArray();
        var bounded = Process(changing, levels, 4);
        Check(Peak(bounded) <= Math.Pow(10, levels.PeakDb / 20), "Sudden loud speech must never exceed the PCM peak ceiling");
        Check(Process(new byte[96000], levels).All(b => b == 0), "Silence remains silence");
        Check(Process(Tone(.8), levels, 0).All(b => b == 0), "Manual mute wins over AGC");
        var noise = Tone(.0005);
        var delayBytes = 48000 * PcmGainProvider.LookAheadMilliseconds / 1000 * 2;
        Check(Process(noise, levels).Skip(delayBytes).SequenceEqual(noise), "Sub-gate noise must not be amplified (accounting for lookahead)");
        var shortSignal = new byte[] { 40, 0, 80, 0, 120, 0 };
        var shortResult = Process(shortSignal, levels, 1, 2);
        Check(shortResult.Length == shortSignal.Length + delayBytes && shortResult.Take(delayBytes).All(b => b == 0)
            && shortResult.Skip(delayBytes).SequenceEqual(shortSignal), "Lookahead must drain the full tail of a sub-window utterance exactly once");
        var unchanged = Tone(.2);
        Check(Process(unchanged, new()).SequenceEqual(unchanged), "Disabled AGC preserves existing playback");
        var limitedBoost = Tone(.005);
        Check(Rms(Process(limitedBoost, levels)) / Rms(limitedBoost) <= Math.Pow(10, 12.01 / 20), "Quiet speech cannot exceed +12 dB auto gain");
        var source = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { ReadFully = false };
        source.AddSamples(unchanged, 0, unchanged.Length);
        var live = new PcmGainProvider(source);
        var output = new byte[1920]; live.Read(output, 0, output.Length);
        live.Levels = new(true, -26, -16, -12); live.Gain = 4;
        live.Read(output, 0, output.Length);
        Check(Peak(output) <= Math.Pow(10, -12.0 / 20), "Live ceiling applies to already queued audio");
        live.Gain = 0; live.Read(output, 0, output.Length);
        Check(output.All(b => b == 0), "Live mute works with AGC enabled");
        Check(Process(Tone(.8), levels, 4, 106).SequenceEqual(loud), "Playback callback/chunk boundaries must not modulate the voice");
        var ramp = new byte[96000];
        for (var i = 0; i < ramp.Length / 2; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(ramp.AsSpan(i * 2, 2), (short)(32767 * (.1 + .8 * i / (ramp.Length / 2.0))));
        var smooth = Process(ramp, new(true, -40, -8, -12));
        double previousGain = 0, largestDrop = 0;
        for (var i = 0; i < ramp.Length / 2; i++)
        {
            var measuredGain = (double)BitConverter.ToInt16(smooth, delayBytes + i * 2) / BitConverter.ToInt16(ramp, i * 2);
            if (i > 0) largestDrop = Math.Max(largestDrop, previousGain - measuredGain);
            previousGain = measuredGain;
        }
        Check(largestDrop < .006, "Limiter gain must ramp, not step down at 10 ms boundaries");
        Console.WriteLine($"Limiter largest per-sample gain drop={largestDrop:F6}; lookahead tail and chunk invariance passed");
        Console.WriteLine($"PASS: auto volume quiet={quietDb:F2} dBFS loud={loudDb:F2} dBFS; peak ceiling, silence, noise gate, gain cap, live update, source unchanged");
    }
}
