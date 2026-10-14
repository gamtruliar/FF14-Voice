using System.Diagnostics;
using System.IO;
using System.Text.Json;
using FF14Sound.Voices;

namespace FF14Sound;

public sealed record BatchJob(string Speaker, string Text, VoiceProfile Voice, bool Fallback, string CacheFile, string Source);
public sealed record BatchPlan(string Index, string Server, string CacheFolder, string Language, DateTimeOffset CreatedAt,
    int Jobs, int Duplicates, int Cached, Dictionary<string, int> Deferred, string JobsFile)
{
    public string Ordering { get; init; } = "source";
    public int ReferenceJobs { get; init; }
    public int DefaultJobs { get; init; }
    public int MissingReferences { get; init; }
    public string NativeVoiceIndex { get; init; } = "";
}
public sealed record BatchStatus(string State, int Total, int Completed, int Generated, int CacheHits, int Failed,
    double GenerationSeconds, long GeneratedBytes, string Current, DateTimeOffset UpdatedAt)
{
    public int NativeSkipped { get; init; }
    public double? RemainingHours => Generated > 0 ? Math.Max(0, Total - Completed) * GenerationSeconds / Generated / 3600 : null;
    public double? RemainingGiB => Generated > 0 ? Math.Max(0, Total - Completed) * (double)GeneratedBytes / Generated / 1073741824 : null;
}

public static class BatchDubbing
{
    public static string DefaultFolder(Settings settings)
    {
        var catalog = settings.CatalogPath.Length > 0 ? settings.CatalogPath : GameVoiceLibrary.FindCatalog();
        return catalog.Length > 0 ? Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(catalog)))!, "batch-" + settings.SourceLanguage)
            : Path.Combine(Settings.Folder, "batch-" + settings.SourceLanguage);
    }
    public static string PlanPath(string folder) => Path.Combine(folder, "plan.json");
    public static T Read<T>(string file) => JsonSerializer.Deserialize<T>(File.ReadAllText(file), DialogueIndex.Json) ?? throw new InvalidDataException("Invalid batch file: " + file);
    public static void Write<T>(string file, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(value, DialogueIndex.Json));
        File.Move(file + ".tmp", file, true);
    }
    public static bool IsRunning(string folder)
    {
        var path = Path.Combine(folder, "worker.lock");
        if (!File.Exists(path)) return false;
        try { using var probe = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException) { return true; }
    }
    public static void Pause(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pause.request"), "Pause after the active sentence; keep its completed audio.");
    }
    public static Process? Start(string folder)
    {
        if (IsRunning(folder)) return null;
        File.Delete(Path.Combine(folder, "pause.request"));
        var executable = Path.Combine(AppContext.BaseDirectory, "FF14Sound.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add("--batch-run"); start.ArgumentList.Add(PlanPath(folder));
        return Process.Start(start);
    }

    // Stable partition preserves the original sequence within each group and every cache identity.
    private static (int References, int Defaults, int Missing) OrderReferencesFirst(string source, string destination, CancellationToken token)
    {
        var references = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int count = 0, defaults = 0, missing = 0;
        var staging = destination + ".ordered.tmp";
        try
        {
            using (var writer = new StreamWriter(staging))
            for (var pass = 0; pass < 2; pass++)
            foreach (var json in File.ReadLines(source))
            {
                token.ThrowIfCancellationRequested();
                var job = JsonSerializer.Deserialize<BatchJob>(json, DialogueIndex.Json) ?? throw new InvalidDataException("Invalid batch job.");
                var hasReference = false;
                if (!job.Fallback)
                {
                    if (!references.TryGetValue(job.Voice.ReferenceAudio, out hasReference))
                        references[job.Voice.ReferenceAudio] = hasReference = File.Exists(job.Voice.ReferenceAudio);
                }
                if (pass == 0)
                {
                    if (hasReference) count++;
                    else if (job.Fallback) defaults++;
                    else missing++;
                }
                if (hasReference == (pass == 0)) writer.WriteLine(json);
            }
            File.Move(staging, destination, true);
            return (count, defaults, missing);
        }
        finally { File.Delete(staging); }
    }

    public static BatchPlan PrioritizeReferences(string folder, CancellationToken token)
    {
        using var exclusive = File.Open(Path.Combine(folder, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var plan = Read<BatchPlan>(PlanPath(folder));
        var jobsFile = Path.Combine(folder, "jobs-ref-first-" + Guid.NewGuid().ToString("N") + ".jsonl");
        var counts = OrderReferencesFirst(plan.JobsFile, jobsFile, token);
        if (counts.References + counts.Defaults + counts.Missing != plan.Jobs) throw new InvalidDataException("Job count changed while reordering.");
        var updated = plan with { JobsFile = jobsFile, Ordering = "reference-first", ReferenceJobs = counts.References, DefaultJobs = counts.Defaults, MissingReferences = counts.Missing };
        Write(PlanPath(folder), updated);
        return updated;
    }

    public static BatchPlan ExcludeNativeAudio(string folder, string catalogPath, CancellationToken token)
    {
        using var exclusive = File.Open(Path.Combine(folder, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var plan = Read<BatchPlan>(PlanPath(folder));
        var nativePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalogPath))!, NativeDialogue.FileName);
        var nativeAudio = NativeDialogue.Load(nativePath);
        var jobsFile = Path.Combine(folder, "jobs-unvoiced-" + Guid.NewGuid().ToString("N") + ".jsonl");
        int excluded = 0, cached = 0;
        try
        {
            using (var writer = new StreamWriter(jobsFile + ".tmp"))
            foreach (var json in File.ReadLines(plan.JobsFile))
            {
                token.ThrowIfCancellationRequested();
                var job = JsonSerializer.Deserialize<BatchJob>(json, DialogueIndex.Json) ?? throw new InvalidDataException("Invalid batch job.");
                if (nativeAudio.Contains(job.Speaker, job.Text, plan.Language, job.Source)) { excluded++; continue; }
                writer.WriteLine(json);
                if (SpeechClient.IsCached(job.CacheFile)) cached++;
            }
            var counts = OrderReferencesFirst(jobsFile + ".tmp", jobsFile, token);
            if (counts.References + counts.Defaults + counts.Missing + excluded != plan.Jobs) throw new InvalidDataException("Queue count mismatch.");
            var deferred = new Dictionary<string, int>(plan.Deferred);
            deferred["game-voiced"] = deferred.GetValueOrDefault("game-voiced") + excluded;
            var updated = plan with { JobsFile = jobsFile, Jobs = plan.Jobs - excluded, Cached = cached, Deferred = deferred,
                NativeVoiceIndex = nativePath, Ordering = "reference-first", ReferenceJobs = counts.References,
                DefaultJobs = counts.Defaults, MissingReferences = counts.Missing };
            Write(PlanPath(folder), updated);
            Write(Path.Combine(folder, "status.json"), new BatchStatus("prepared", updated.Jobs, cached, 0, cached, 0, 0, 0, "已排除遊戲原錄音", DateTimeOffset.Now));
            return updated;
        }
        finally { File.Delete(jobsFile + ".tmp"); }
    }

    public static async Task<BatchPlan> Prepare(string index, string folder, Settings settings, IProgress<string>? progress, CancellationToken token)
    {
        if (settings.SpeechMode != "original") throw new InvalidOperationException("呢個全 client 佇列使用原文配音；請先選原文配音。");
        if (IsRunning(folder)) throw new InvalidOperationException("請先暫停背景預配，再重建佇列。");
        Directory.CreateDirectory(folder);
        using var exclusive = File.Open(Path.Combine(folder, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var library = new GameVoiceLibrary();
        var catalog = settings.CatalogPath.Length > 0 ? settings.CatalogPath : GameVoiceLibrary.FindCatalog();
        if (File.Exists(catalog)) library.Load(catalog);
        if (library.NativeAudio is null) throw new InvalidOperationException("缺少遊戲原錄音索引，未開始預配。");
        var registry = new NpcVoiceRegistry(Path.Combine(Settings.Folder, "npc-voices.json"));
        registry.Load(File.Exists(catalog) ? Path.Combine(Path.GetDirectoryName(catalog)!, "npc-voice-index.json") : null);
        var cache = SpeechClient.CacheFolder(settings);
        using var speech = new SpeechClient();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deferred = new Dictionary<string, int>();
        var recovered = new Dictionary<string, string>();
        var jobsFile = Path.Combine(folder, "jobs-" + Guid.NewGuid().ToString("N") + ".jsonl");
        int jobs = 0, duplicates = 0, cached = 0, visited = 0;
        try
        {
            using (var writer = new StreamWriter(jobsFile + ".tmp"))
            foreach (var line in DialogueIndex.Read(index))
            {
                token.ThrowIfCancellationRequested();
                if (++visited % 5000 == 0) progress?.Report($"建立佇列 {visited:N0} 行 · {jobs:N0} 個配音 · 去重 {duplicates:N0}");
                if (line.Language != settings.SourceLanguage) throw new InvalidDataException("索引語言同設定唔一致。");
                if (library.HasNativeAudio(line.Speaker, line.Text, line.Language, line.Sheet + ":" + line.RowId, line.SpeakerToken))
                { deferred["game-voiced"] = deferred.GetValueOrDefault("game-voiced") + 1; continue; }
                var speaker = line.Speaker;
                if (line.Status == "unresolved-speaker" && line.SpeakerToken.Length > 0)
                {
                    if (!recovered.TryGetValue(line.SpeakerToken, out speaker))
                    {
                        var match = library.Resolver?.Resolve(line.SpeakerToken, settings.ReferenceLanguage);
                        var entries = match?.Voice.Names.Select(registry.Find).Where(r => r is not null).DistinctBy(r => r!.Key).ToArray();
                        var names = entries?.Select(r => settings.SourceLanguage switch { "ja" => r!.Japanese, "fr" => r!.French, "de" => r!.German, _ => r!.English }).Where(n => n.Length > 0).Distinct().ToArray() ?? [];
                        speaker = names.Length == 1 ? names[0] : match is not null ? line.SpeakerToken : "";
                        recovered[line.SpeakerToken] = speaker;
                    }
                }
                if ((line.Status != "ready" && line.Status != "unresolved-speaker") || string.IsNullOrWhiteSpace(speaker))
                { deferred[line.Status] = deferred.GetValueOrDefault(line.Status) + 1; continue; }
                var selection = SpeechSelection.Choose("original", line.Text, "", line.Language, settings.TargetLanguage);
                var voice = selection.Apply(library.Resolve(speaker, line.Language, settings, registry.DescriptionForDialogue(speaker, line.Language, settings.DefaultVoiceDescription), registry.Find(speaker)?.Key));
                var cacheFile = await speech.CachePath(settings.Server, selection.Text, voice, cache, token);
                if (!seen.Add(cacheFile)) { duplicates++; continue; }
                var job = new BatchJob(speaker, selection.Text, voice, voice.IsFallback, cacheFile, line.Sheet + ":" + line.RowId);
                writer.WriteLine(JsonSerializer.Serialize(job, DialogueIndex.Json)); jobs++;
                if (SpeechClient.IsCached(cacheFile)) cached++;
            }
            var counts = OrderReferencesFirst(jobsFile + ".tmp", jobsFile, token);
            var plan = new BatchPlan(Path.GetFullPath(index), settings.Server, cache, settings.SourceLanguage, DateTimeOffset.Now,
                jobs, duplicates, cached, deferred, jobsFile)
            { Ordering = "reference-first", ReferenceJobs = counts.References, DefaultJobs = counts.Defaults, MissingReferences = counts.Missing,
                NativeVoiceIndex = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(catalog))!, NativeDialogue.FileName) };
            Write(PlanPath(folder), plan);
            Write(Path.Combine(folder, "status.json"), new BatchStatus("prepared", jobs, cached, 0, cached, 0, 0, 0, "佇列已建立", DateTimeOffset.Now));
            return plan;
        }
        finally { File.Delete(jobsFile + ".tmp"); }
    }

    public static async Task Run(string planPath, CancellationToken token, int limit = int.MaxValue, SpeechClient? injectedClient = null, long reserveBytes = 5L * 1024 * 1024 * 1024)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(planPath))!;
        // Exclusive across GUI instances and command-line workers. Never overwrite an active worker's status.
        using var exclusive = File.Open(Path.Combine(folder, "worker.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var plan = Read<BatchPlan>(planPath);
        var statusFile = Path.Combine(folder, "status.json");
        var status = new BatchStatus("running", plan.Jobs, 0, 0, 0, 0, 0, 0, "檢查快取", DateTimeOffset.Now);
        using var ownedClient = injectedClient is null ? new SpeechClient() : null;
        var speech = injectedClient ?? ownedClient!;
        Directory.CreateDirectory(plan.CacheFolder);
        var watch = new Stopwatch(); int consecutiveFailures = 0;
        try
        {
            Write(statusFile, status);
            using var cacheLease = SpeechCache.Lease(plan.CacheFolder);
            var nativePath = plan.NativeVoiceIndex.Length > 0 ? plan.NativeVoiceIndex
                : Path.Combine(Path.GetDirectoryName(GameVoiceLibrary.FindCatalog()) ?? "", NativeDialogue.FileName);
            var nativeAudio = NativeDialogue.Load(nativePath);
            if (injectedClient is null) await speech.EnsureBackgroundSupport(plan.Server, token);
            foreach (var json in File.ReadLines(plan.JobsFile))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.Combine(folder, "pause.request")) || status.Generated >= limit)
                { status = status with { State = "paused", Current = "已暫停，可續跑" }; break; }
                var job = JsonSerializer.Deserialize<BatchJob>(json, DialogueIndex.Json) ?? throw new InvalidDataException("Invalid batch job.");
                job.Voice.IsFallback = job.Fallback;
                if (nativeAudio.Contains(job.Speaker, job.Text, plan.Language, job.Source))
                {
                    status = status with { Completed = status.Completed + 1, NativeSkipped = status.NativeSkipped + 1 };
                    continue;
                }
                // Detect edited references instead of silently producing audio for an obsolete plan.
                var currentCache = await speech.CachePath(plan.Server, job.Text, job.Voice, plan.CacheFolder, token);
                if (!string.Equals(currentCache, job.CacheFile, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("角色 seed、reference 或快取格式已改動，請重建佇列；既有快取會保留。");
                if (SpeechClient.IsCached(currentCache))
                {
                    status = status with { Completed = status.Completed + 1, CacheHits = status.CacheHits + 1 };
                    if (status.Completed % 250 == 0) Write(statusFile, status with { UpdatedAt = DateTimeOffset.Now });
                    continue;
                }
                if (new DriveInfo(Path.GetPathRoot(plan.CacheFolder)!).AvailableFreeSpace < reserveBytes)
                { status = status with { State = "disk-low", Current = "磁碟剩餘少於 5 GiB，已暫停" }; break; }
                status = status with { Current = (job.Fallback ? "default · " : "REF · ") + job.Speaker + " · " + job.Source, UpdatedAt = DateTimeOffset.Now }; Write(statusFile, status);
                try
                {
                    watch.Restart();
                    var audio = await speech.Generate(plan.Server, job.Text, job.Voice, token, cacheFolder: plan.CacheFolder, background: true);
                    if (!SpeechClient.IsCached(currentCache)) throw new InvalidDataException("生成結果未寫入有效快取。");
                    status = status with { Completed = status.Completed + 1, Generated = status.Generated + 1,
                        GenerationSeconds = status.GenerationSeconds + watch.Elapsed.TotalSeconds, GeneratedBytes = status.GeneratedBytes + audio.LongLength };
                    consecutiveFailures = 0;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    status = status with { Failed = status.Failed + 1 };
                    File.AppendAllText(Path.Combine(folder, "errors.jsonl"), JsonSerializer.Serialize(new { job.Source, job.Speaker, Error = ex.Message, At = DateTimeOffset.Now }, DialogueIndex.Json) + Environment.NewLine);
                    if (++consecutiveFailures >= 3) throw new IOException("連續 3 句失敗，已暫停；檢查 backend 後可續跑。", ex);
                }
                Write(statusFile, status with { UpdatedAt = DateTimeOffset.Now });
            }
            if (status.State == "running") status = status with { State = status.Failed > 0 ? "finished-with-errors" : "completed", Current = status.Failed > 0 ? "有失敗句子；續跑會重試" : "此佇列已完成（待處理文字見 plan.json）" };
        }
        catch (OperationCanceledException) { status = status with { State = "paused", Current = "已暫停，可續跑" }; }
        catch (Exception ex) { status = status with { State = "error", Current = ex.Message }; }
        finally { Write(statusFile, status with { UpdatedAt = DateTimeOffset.Now }); }
    }
}
