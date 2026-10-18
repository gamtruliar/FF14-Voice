using System.Buffers.Binary;
using System.IO;
using FF14Sound;
using NAudio.Wave;

internal static class TempoChecks
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static byte[] Tone(double hz, double seconds)
    {
        var pcm = new byte[(int)(48000 * seconds) * 2];
        for (var i = 0; i < pcm.Length / 2; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), (short)(12000 * Math.Sin(2 * Math.PI * hz * i / 48000)));
        return pcm;
    }
    private static double Frequency(byte[] pcm, double start, double duration)
    {
        var first = (int)(48000 * start); var last = Math.Min(pcm.Length / 2, first + (int)(48000 * duration));
        var crossings = 0;
        for (var i = first + 1; i < last; i++) if (BitConverter.ToInt16(pcm, (i - 1) * 2) <= 0 && BitConverter.ToInt16(pcm, i * 2) > 0) crossings++;
        return crossings * 48000.0 / (last - first);
    }
    private static byte[] Process(byte[] input, double speed, int chunk = 1920)
    {
        var buffer = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(60) };
        buffer.AddSamples(input, 0, input.Length);
        var provider = new PcmTempoProvider(buffer, () => true) { Speed = speed };
        return Drain(provider, chunk);
    }
    private static byte[] Drain(PcmTempoProvider provider, int chunk)
    {
        using var result = new MemoryStream(); var block = new byte[chunk + 6]; int read;
        while ((read = provider.Read(block, 4, chunk)) > 0)
        {
            Check(block[0] == 0 && block[3] == 0 && block[^1] == 0, "Tempo respects caller offsets");
            result.Write(block, 4, read);
            Check(result.Length < 96000 * 60, "Tempo must terminate after flush");
        }
        Check(provider.Read(block, 4, chunk) == 0, "Tempo must flush once only");
        return result.ToArray();
    }
    public static void Run()
    {
        var tone = Tone(440, 2); var copy = tone.ToArray();
        Check(Process(tone, 1).SequenceEqual(tone), "1x must bypass processing without changing samples or duration");
        foreach (var speed in new[] { .75, 1.25, 1.5 })
        {
            var result = Process(tone, speed);
            var seconds = result.Length / 96000.0;
            Check(Math.Abs(seconds - 2 / speed) < .002, "Tempo duration must follow selected multiplier");
            var hz = Frequency(result, .2, .6);
            Check(Math.Abs(hz - 440) < 4, "Tempo must retain pitch rather than change sample rate");
            Check(Process(tone, speed, 106).SequenceEqual(result), "Playback read sizes must not change tempo output");
            var shortResult = Process(Tone(440, .02), speed);
            Check(Math.Abs(shortResult.Length / 96000.0 - .02 / speed) < .002 && shortResult.Any(b => b != 0), "Sub-window utterance tail must flush");
            var ending = Process(Tone(440, 1).Concat(Tone(880, 1)).ToArray(), speed);
            Check(Math.Abs(Frequency(ending, ending.Length / 96000.0 - .4, .2) - 880) < 6, "Last portion of dialogue must survive flush");
            Console.WriteLine($"Tempo {speed:F2}x: {seconds:F3}s from 2s, pitch {hz:F1} Hz from 440 Hz");
        }
        Check(copy.SequenceEqual(tone), "Tempo must not change source/cache PCM");
        var queued = new BufferedWaveProvider(new WaveFormat(48000, 16, 1)) { ReadFully = false };
        var completed = false;
        var live = new PcmTempoProvider(queued, () => completed) { Speed = 1.25 };
        var output = new byte[1920];
        Check(live.Read(output, 0, output.Length) == output.Length && output.All(b => b == 0), "Temporary starvation must not terminate playback");
        queued.AddSamples(tone, 0, tone.Length);
        Check(live.Read(output, 0, output.Length) > 0 && output.Any(b => b != 0), "Audio resumes after network starvation");
        live.Speed = .75; completed = true;
        var remainder = Drain(live, 1920);
        Check(remainder.Length > 96000, "Live speed change must affect queued audio");
        var cancel = new PcmTempoProvider(queued, () => false) { Speed = .75 };
        queued.AddSamples(tone, 0, tone.Length); cancel.Read(output, 0, output.Length);
        cancel.Discard(); Check(cancel.Read(output, 0, output.Length) == 0, "Discarded tempo buffer must never leak into the next dialogue");
        Console.WriteLine("PASS: pitch-preserving tempo, 1x identity, speed updates, starvation, cancellation and complete tails");
    }
    public static void Export(string input, string folder)
    {
        Directory.CreateDirectory(folder);
        using var reader = new WaveFileReader(input);
        Check(reader.WaveFormat.SampleRate == 48000 && reader.WaveFormat.Channels == 1 && reader.WaveFormat.BitsPerSample == 16, "Expected 48 kHz mono PCM16");
        var pcm = new byte[(int)reader.Length]; reader.ReadExactly(pcm);
        foreach (var speed in new[] { .75, 1, 1.25, 1.5 })
        {
            var path = Path.Combine(folder, "speech-" + speed.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "x.wav");
            using var writer = new WaveFileWriter(path, reader.WaveFormat);
            var result = Process(pcm, speed); writer.Write(result, 0, result.Length);
            Console.WriteLine($"{path}: {result.Length / 96000.0:F3}s");
        }
    }
    public static async Task Playback()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        foreach (var speed in new[] { .75, 1, 1.5 })
        {
            using var player = new StreamingPcmPlayer { Speed = speed };
            await player.Add(new byte[19200], timeout.Token);
            await player.Finish(timeout.Token);
        }
        var stopped = new StreamingPcmPlayer { Speed = .75 };
        await stopped.Add(new byte[96000], timeout.Token); stopped.Speed = 1.5; stopped.Dispose();
        try { await stopped.Add(new byte[1920], timeout.Token); throw new Exception("Stopped tempo player accepted audio"); }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: Windows output device drained tempo + AGC at 0.75/1/1.5x and stopped/discarded queued audio (silence only)");
    }
}
