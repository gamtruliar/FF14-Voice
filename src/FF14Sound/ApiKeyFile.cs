using System.IO;

namespace FF14Sound;

public static class ApiKeyFile
{
    public static string Resolve(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured)) throw new InvalidOperationException("請選擇 API key 檔案。");
        if (Path.IsPathFullyQualified(configured)) return Path.GetFullPath(configured);
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var path = Path.GetFullPath(Path.Combine(directory.FullName, configured));
                if (File.Exists(path)) return path;
            }
        throw new InvalidOperationException("搵唔到 API key 檔案，請喺設定重新選擇。");
    }
}
