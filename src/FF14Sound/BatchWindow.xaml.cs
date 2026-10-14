using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using FF14Sound.Voices;

namespace FF14Sound;

public partial class BatchWindow : Window
{
    private readonly Settings settings;
    private readonly string folder;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource lifetime = new();
    private bool preparing;
    public BatchWindow(Settings current)
    {
        InitializeComponent();
        settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(current))!;
        folder = BatchDubbing.DefaultFolder(settings);
        Folder.Text = folder; Cache.Text = SpeechClient.CacheFolder(settings);
        Mode.Text = $"原文：{settings.SourceLanguage} · Clone 原聲版本：{settings.ReferenceLanguage} · 佇列保存建立時嘅角色設定；改音色／描述後可重建，快取會保留。";
        var catalog = settings.CatalogPath.Length > 0 ? settings.CatalogPath : GameVoiceLibrary.FindCatalog();
        Sqpack.Text = File.Exists(catalog) ? VoiceCatalog.Load(catalog).Sqpack : @"D:\Game\ff14\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack";
        timer.Tick += (_, _) => Refresh(); timer.Start(); Refresh();
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); };
    }
    private void Refresh()
    {
        if (preparing) return;
        try
        {
            var running = BatchDubbing.IsRunning(folder);
            Scan.IsEnabled = !running; Resume.IsEnabled = !running && File.Exists(BatchDubbing.PlanPath(folder)); Pause.IsEnabled = running;
            var planFile = BatchDubbing.PlanPath(folder);
            if (!File.Exists(planFile)) return;
            var plan = BatchDubbing.Read<BatchPlan>(planFile);
            Summary.Text = $"{plan.Jobs:N0} 個不重複配音 · 已合併 {plan.Duplicates:N0} 行重複文字／音色";
            Summary.Text += $"\n原錄音交由 GAME：已排除 {plan.Deferred.GetValueOrDefault("game-voiced"):N0} 行";
            if (plan.Ordering == "reference-first") Summary.Text += $"\n先配有 REF：{plan.ReferenceJobs:N0} 個 → default：{plan.DefaultJobs:N0} 個";
            if (plan.MissingReferences > 0) Summary.Text += $" · 缺少 reference 音檔：{plan.MissingReferences:N0}";
            Deferred.Text = $"待處理：動態文字 {plan.Deferred.GetValueOrDefault("dynamic-text"):N0} · 未確認講者 {plan.Deferred.GetValueOrDefault("unresolved-speaker"):N0} · 過長文字 {plan.Deferred.GetValueOrDefault("long-text"):N0}\n另有空白、非語音、系統／選項及未辨識格式；完整分類見 plan.json 及索引報告。";
            var statusFile = Path.Combine(folder, "status.json");
            if (!File.Exists(statusFile)) return;
            var status = BatchDubbing.Read<BatchStatus>(statusFile);
            Counts.Text = $"完成／快取 {status.Completed:N0} / {status.Total:N0} · 本輪生成 {status.Generated:N0} · 本輪快取 {status.CacheHits:N0} · 失敗 {status.Failed:N0}";
            if (status.NativeSkipped > 0) Counts.Text += $" · 原錄音略過 {status.NativeSkipped:N0}";
            Progress.Value = status.Total == 0 ? 0 : status.Completed * 100.0 / status.Total;
            Estimate.Text = status.RemainingHours is { } hours ? $"按本輪樣本粗估：剩餘 {hours:N1} 小時，音檔約需再 {status.RemainingGiB:N1} GiB；句長及遊玩負載會影響速度。" : "速度及剩餘時間會按實際生成更新。";
            var state = status.State switch { "prepared" => "已準備", "paused" => "已暫停", "completed" => "佇列完成", "finished-with-errors" => "有失敗句子", "disk-low" => "空間不足", "error" => "已停止", "running" => "工作已中斷，可續跑", _ => status.State };
            Status.Text = (running ? "背景工作中" : state) + " · " + status.Current;
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        preparing = true; Scan.IsEnabled = Resume.IsEnabled = Pause.IsEnabled = false;
        try
        {
            settings.SpeechMode = "original";
            var index = Path.Combine(Path.GetDirectoryName(folder)!, "dialogue-" + settings.SourceLanguage + ".jsonl");
            var input = Sqpack.Text.Trim();
            var progress = new Progress<string>(s => Status.Text = s);
            var report = await Task.Run(() => DialogueIndex.Build(input, index, settings.SourceLanguage, progress, lifetime.Token));
            if (report.Errors.Length > 0) throw new InvalidDataException($"掃描有 {report.Errors.Length} 個錯誤，請查看索引報告。");
            await Task.Run(() => BatchDubbing.Prepare(index, folder, settings, progress, lifetime.Token));
        }
        catch (OperationCanceledException) { Status.Text = "已取消掃描"; }
        catch (Exception ex) { Status.Text = "建立失敗：" + ex.Message; }
        finally { preparing = false; Refresh(); }
    }
    private void Resume_Click(object sender, RoutedEventArgs e)
    {
        try { using var worker = BatchDubbing.Start(folder); Status.Text = "正在啟動背景預配…"; Resume.IsEnabled = false; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        try { BatchDubbing.Pause(folder); Status.Text = "完成目前一句後暫停，已完成音檔會保留。"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true }); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
