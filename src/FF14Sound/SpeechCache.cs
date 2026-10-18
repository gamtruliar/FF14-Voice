using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace FF14Sound;

public sealed record SpeechCacheMetadata(string Speaker, string VoiceKey, uint Seed, bool IsReference);
public sealed record SpeechCacheGroup(string VoiceKey, string Speaker, int Sentences, int References, long Bytes)
{
    public string Size => $"{Bytes / 1048576.0:N2} MB";
}

// Cache-owned files only. Original/custom/game references are never managed here.
public static class SpeechCache
{
    private const string Unknown = "unidentified-cache";
    private static readonly Regex FileName = new(@"\A[0-9A-F]{64}\.wav\z", RegexOptions.IgnoreCase);
    private static string Root(string folder)
    {
        var root = Path.GetFullPath(folder);
        for (var item = new DirectoryInfo(root); item is not null; item = item.Parent)
            if (item.Exists && item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("快取路徑不可經過 symbolic link／junction。");
        return root;
    }
    private static string Owned(string folder, string path)
    {
        var root = Root(folder);
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("檔案不在配音快取資料夾內。");
        Root(Path.GetDirectoryName(full)!);
        if (File.Exists(full) && File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("快取檔案不可為 symbolic link。");
        return full;
    }
    public static FileStream Lease(string folder, bool exclusive = false)
    {
        var root = Root(folder); Directory.CreateDirectory(root);
        return File.Open(Owned(root, Path.Combine(root, ".cache.lock")), FileMode.OpenOrCreate, FileAccess.ReadWrite,
            exclusive ? FileShare.None : FileShare.ReadWrite);
    }
    private static string ReferenceId(VoiceProfile voice) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { identity = voice.EffectiveVoiceIdentity, seed = voice.EffectiveSeed, voice.Control }))));
    private static bool CanLearn(VoiceProfile voice) => voice.IsFallback && !GroupSpeech.Applies(voice) && !string.IsNullOrWhiteSpace(voice.Speaker);
    private static string ReferencePath(string folder, VoiceProfile voice) => Owned(folder, Path.Combine(Root(folder), "learned-voices", ReferenceId(voice) + ".wav"));
    public static string? LearnedReference(string folder, VoiceProfile voice)
    {
        if (!CanLearn(voice)) return null;
        var path = ReferencePath(folder, voice);
        return SpeechClient.IsCached(path) ? path : null;
    }
    public static async Task<FileStream?> LockVoice(string folder, VoiceProfile voice, CancellationToken token)
    {
        if (!CanLearn(voice)) return null;
        var path = Owned(folder, Path.Combine(Root(folder), "learned-voices", ReferenceId(voice) + ".lock"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }
    private static SpeechCacheMetadata Metadata(VoiceProfile voice, bool reference) =>
        new(voice.Speaker.Length > 0 ? voice.Speaker : "未命名角色", voice.EffectiveVoiceIdentity, voice.EffectiveSeed, reference);
    private static void WriteMetadata(string folder, string wave, SpeechCacheMetadata metadata)
    {
        var path = Owned(folder, wave + ".json"); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(metadata)); File.Move(temp, path, true); }
        finally { File.Delete(temp); }
    }
    public static void Complete(string folder, string wave, VoiceProfile voice, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        wave = Owned(folder, wave);
        WriteMetadata(folder, wave, Metadata(voice, false));
        if (!CanLearn(voice) || LearnedReference(folder, voice) is not null) return;
        // Only completed, decodable, non-silent PCM can become a voice reference.
        byte[] samples; WaveFormat format;
        try
        {
            using var reader = new WaveFileReader(wave);
            format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 || format.Channels != 1 || format.SampleRate != 48000) return;
            var length = (int)Math.Min(reader.Length, format.AverageBytesPerSecond * 12L);
            if (length < format.AverageBytesPerSecond * .3) return;
            samples = new byte[length - length % format.BlockAlign];
            if (reader.Read(samples, 0, samples.Length) != samples.Length) return;
            var audible = false;
            for (var i = 0; i + 1 < samples.Length; i += 2)
                if (Math.Abs((int)BitConverter.ToInt16(samples, i)) > 100) { audible = true; break; }
            if (!audible) return;
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or EndOfStreamException) { return; }
        var reference = ReferencePath(folder, voice); Directory.CreateDirectory(Path.GetDirectoryName(reference)!);
        var temp = reference + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var writer = new WaveFileWriter(temp, format)) writer.Write(samples, 0, samples.Length);
            token.ThrowIfCancellationRequested();
            File.Move(temp, reference, true);
            WriteMetadata(folder, reference, Metadata(voice, true));
        }
        finally { File.Delete(temp); }
    }
    private sealed record Item(string Wave, SpeechCacheMetadata Metadata, long Bytes);
    private static IEnumerable<Item> Items(string folder)
    {
        var root = Root(folder);
        foreach (var directory in new[] { root, Path.Combine(root, "learned-voices") })
        {
            if (!Directory.Exists(directory)) continue;
            Root(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*.wav", SearchOption.TopDirectoryOnly))
            {
                if (!FileName.IsMatch(Path.GetFileName(file))) continue;
                Owned(root, file);
                var info = new FileInfo(file);
                SpeechCacheMetadata? metadata = null;
                var sidecar = Owned(root, file + ".json");
                if (File.Exists(sidecar))
                {
                    try { metadata = JsonSerializer.Deserialize<SpeechCacheMetadata>(File.ReadAllText(sidecar)); }
                    catch (JsonException) { }
                }
                metadata ??= new("舊快取／未識別角色", Unknown, 42, directory != root);
                yield return new(file, metadata with { IsReference = directory != root }, info.Length);
            }
        }
    }
    public static SpeechCacheGroup[] Scan(string folder) => Items(folder).GroupBy(i => i.Metadata.VoiceKey)
        .Select(g => new SpeechCacheGroup(g.Key, string.Join(" / ", g.Select(i => i.Metadata.Speaker).Distinct()),
            g.Count(i => !i.Metadata.IsReference), g.Count(i => i.Metadata.IsReference), g.Sum(i => i.Bytes)))
        .OrderBy(g => g.Speaker).ToArray();

    // Recover ownership of old hashed audio from existing batch snapshots, never infer an NPC from a hash.
    public static void ImportLegacyOwners(string folder, string planPath)
    {
        if (!File.Exists(planPath)) return;
        var unknown = Items(folder).Where(i => i.Metadata.VoiceKey == Unknown && !i.Metadata.IsReference)
            .Select(i => Path.GetFileName(i.Wave)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (unknown.Count == 0) return;
        var plan = BatchDubbing.Read<BatchPlan>(planPath);
        if (!string.Equals(Root(plan.CacheFolder), Root(folder), StringComparison.OrdinalIgnoreCase) || !File.Exists(plan.JobsFile)) return;
        foreach (var line in File.ReadLines(plan.JobsFile))
        {
            var job = JsonSerializer.Deserialize<BatchJob>(line, FF14Sound.Voices.DialogueIndex.Json);
            if (job is null || !unknown.Remove(Path.GetFileName(job.CacheFile))) continue;
            var wave = Owned(folder, Path.Combine(Root(folder), Path.GetFileName(job.CacheFile)));
            WriteMetadata(folder, wave, Metadata(job.Voice, false));
            if (unknown.Count == 0) break;
        }
    }
    public static int Clear(string folder, string? voiceKey, bool resetReference)
    {
        // Refuse while any GUI/worker is generating, so a completed request cannot recreate deleted cache.
        using var lease = Lease(folder, exclusive: true);
        var targets = Items(folder).Where(i => (voiceKey is null || i.Metadata.VoiceKey == voiceKey) &&
            (!i.Metadata.IsReference || resetReference)).ToArray();
        foreach (var item in targets)
        {
            File.Delete(Owned(folder, item.Wave));
            File.Delete(Owned(folder, item.Wave + ".json"));
        }
        return targets.Length;
    }
}
