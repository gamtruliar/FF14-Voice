using System.IO;
using FF14Sound;

internal static class BackendRestartChecks
{
    public static void Run(string folder)
    {
        var root = Path.Combine(folder, "backend with spaces");
        foreach (var relative in new[] { "tools/voxcpm/restart_backend.py", "VoxCPM/.venv/Scripts/python.exe" })
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "fixture");
        }
        var nested = Path.Combine(root, "artifacts", "release"); Directory.CreateDirectory(nested);
        if (BackendRestart.FindRoot(nested) != root) throw new Exception("Restart must find checkout above release artifacts");
        var start = BackendRestart.StartInfo(root, "http://127.0.0.1:8808");
        if (start.UseShellExecute || !start.CreateNoWindow || start.ArgumentList.Count != 4 ||
            start.ArgumentList[1] != Path.Combine(root, "tools", "voxcpm", "restart_backend.py") ||
            start.ArgumentList[3] != "http://127.0.0.1:8808") throw new Exception("Restart uses a hidden process with separate, unescaped arguments");
        foreach (var invalid in new[] { "http://example.com:8808", "https://localhost:8808", "http://user@localhost:8808", "http://localhost:8808/path" })
        {
            try { BackendRestart.StartInfo(root, invalid); throw new Exception("Unsafe restart endpoint accepted"); }
            catch (InvalidOperationException) { }
        }
        Console.WriteLine("PASS: backend restart root discovery, paths with spaces, hidden launch and local-only endpoints.");
    }
}
