using System.Diagnostics;
using System.IO;
using System.Text;

namespace FF14Sound;

public static class BackendRestart
{
    public static string FindRoot(string start)
    {
        for (var folder = new DirectoryInfo(start); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "tools", "voxcpm", "restart_backend.py")) &&
                File.Exists(Path.Combine(folder.FullName, "VoxCPM", ".venv", "Scripts", "python.exe"))) return folder.FullName;
        throw new InvalidOperationException("搵唔到 VoxCPM 安裝目錄；請從專案目錄內啟動 FF14 Voice。");
    }

    public static ProcessStartInfo StartInfo(string root, string server)
    {
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http" ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("重啟只支援本機 HTTP backend 地址。");
        var start = new ProcessStartInfo(Path.Combine(root, "VoxCPM", ".venv", "Scripts", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.Environment["PYTHONUTF8"] = "1";
        start.ArgumentList.Add("-u");
        start.ArgumentList.Add(Path.Combine(root, "tools", "voxcpm", "restart_backend.py"));
        start.ArgumentList.Add("--server"); start.ArgumentList.Add(server);
        return start;
    }

    public static async Task Run(string server, Settings settings, IProgress<string> progress, CancellationToken token)
    {
        var start = StartInfo(FindRoot(AppContext.BaseDirectory), server);
        var batchRoot = Path.GetDirectoryName(BatchDubbing.DefaultFolder(settings))!;
        var folders = new[] { "en", "ja", "de", "fr" }.Select(language => Path.Combine(batchRoot, "batch-" + language))
            .Where(BatchDubbing.IsRunning).ToArray();
        if (folders.Length > 0)
        {
            progress.Report("正在暫停背景預配，等目前一句完成…");
            foreach (var folder in folders) BatchDubbing.Pause(folder);
            var deadline = DateTime.UtcNow.AddMinutes(2);
            while (folders.Any(BatchDubbing.IsRunning))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("背景預配仍未暫停；未有重啟 backend，請稍後再試。");
                await Task.Delay(300, token);
            }
        }
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("無法啟動重啟工具。");
        var errors = process.StandardError.ReadToEndAsync();
        string last = "";
        while (await process.StandardOutput.ReadLineAsync() is { } line)
        {
            last = line; progress.Report(line);
        }
        // Once launched, finish the stop/start pair even if the GUI is closed.
        await process.WaitForExitAsync();
        var error = await errors;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(last.Length > 0 ? last : "重啟工具失敗：" + error);
        if (folders.Length > 0) progress.Report(last + " · 背景預配已暫停，可手動續跑");
    }
}
