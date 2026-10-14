using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FF14Sound;

public sealed class CloudflareAccess
{
    public string BaseUrl { get; }
    public string Token { get; }
    public DateTimeOffset ExpiresAt { get; }
    private CloudflareAccess(string baseUrl, string token, DateTimeOffset expiresAt)
        => (BaseUrl, Token, ExpiresAt) = (baseUrl, token, expiresAt);
    public static string NormalizeUrl(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("Cloudflare 網頁登入需要有效 HTTPS base URL。");
        return uri.AbsoluteUri.TrimEnd('/') + "/";
    }
    public bool Matches(string address)
    {
        try { return BaseUrl == NormalizeUrl(address); }
        catch (InvalidOperationException) { return false; }
    }
    public void ValidateFor(Uri endpoint)
    {
        if (endpoint.Scheme != "https" || !endpoint.AbsoluteUri.StartsWith(BaseUrl, StringComparison.Ordinal))
            throw new InvalidOperationException("Cloudflare 登入狀態不屬於呢個 URL，請重新網頁登入。");
        if (ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30))
            throw new InvalidOperationException("Cloudflare 登入已過期，請按「網頁登入」。");
    }
    // Parse only for local expiry checks; Cloudflare validates the JWT signature at the edge.
    public static CloudflareAccess FromToken(string address, string token)
    {
        var baseUrl = NormalizeUrl(address);
        try
        {
            if (!Regex.IsMatch(token, @"\A[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\z")) throw new FormatException();
            var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '=')));
            return new(baseUrl, token, DateTimeOffset.FromUnixTimeSeconds(json.RootElement.GetProperty("exp").GetInt64()));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        { throw new InvalidOperationException("Cloudflare 未回傳有效登入狀態，請重新網頁登入。"); }
    }
    public string Protect() => SecretStorage.Protect(JsonSerializer.Serialize(new { BaseUrl, Token }));
    public static CloudflareAccess? Restore(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try
        {
            using var json = JsonDocument.Parse(SecretStorage.Unprotect(encrypted));
            return FromToken(json.RootElement.GetProperty("BaseUrl").GetString()!, json.RootElement.GetProperty("Token").GetString()!);
        }
        catch { throw new InvalidOperationException("無法讀取 Cloudflare 登入狀態，請重新網頁登入。"); }
    }
    public override string ToString() => "Cloudflare Access (redacted)";
}

public static class CloudflareBrowserLogin
{
    public static string FindExecutable()
    {
        var folders = new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "cloudflared"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "cloudflared")
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator));
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)))
        {
            var path = Path.Combine(folder.Trim('"'), "cloudflared.exe");
            if (File.Exists(path)) return Path.GetFullPath(path);
        }
        throw new InvalidOperationException("未安裝 cloudflared。請先安裝 Cloudflare.cloudflared，再按網頁登入。");
    }
    public static ProcessStartInfo StartInfo(string executable, string address)
    {
        var start = new ProcessStartInfo(executable) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "access", "login", "--no-verbose", NormalizeLoginUrl(address) }) start.ArgumentList.Add(arg);
        return start;
    }
    private static string NormalizeLoginUrl(string address) => CloudflareAccess.NormalizeUrl(address);
    public static async Task<CloudflareAccess> Login(string address, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var process = new Process { StartInfo = StartInfo(FindExecutable(), address) };
        try { process.Start(); }
        catch { throw new InvalidOperationException("無法啟動 cloudflared 網頁登入，請檢查安裝。"); }
        // Capture privately: stdout contains the session credential, stderr can contain login links.
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } });
        await process.WaitForExitAsync();
        var result = await output;
        await error;
        if (timeout.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Cloudflare 網頁登入逾時，請重試。");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Cloudflare 網頁登入未完成；請檢查網絡、瀏覽器及 Access 存取權限後重試。");
        var session = CloudflareAccess.FromToken(address, result.Trim());
        session.ValidateFor(new Uri(session.BaseUrl));
        return session;
    }
}

public static class SecretStorage
{
    public static string Protect(string value)
    {
        if (value.Length == 0) return "";
        var bytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public static string Unprotect(string value)
    {
        if (value.Length == 0) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        { throw new InvalidOperationException("無法解密 Cloudflare 登入狀態，請重新網頁登入。"); }
    }
}
