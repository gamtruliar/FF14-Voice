using System.ComponentModel;
using System.IO;
using System.Security.Principal;

namespace FF14Sound;

public static class GameAccess
{
    public static bool IsAdministrator
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public static bool IsAccessDenied(Exception error) =>
        error is UnauthorizedAccessException || error is Win32Exception { NativeErrorCode: 5 } ||
        error.InnerException is { } inner && IsAccessDenied(inner);

    public static string Describe(Exception error, string stage, bool elevated)
    {
        if (!IsAccessDenied(error)) return stage + "失敗：" + error.Message;
        return elevated
            ? stage + "：已用管理員權限，但 Windows 仍拒絕存取。請確認遊戲執行帳戶及查看 connection-error.log。"
            : stage + "：存取被拒。請按「以管理員重啟」，接受 Windows UAC 後再連接 FF14。";
    }

    public static string Record(Exception error, string stage)
    {
        var message = Describe(error, stage, IsAdministrator);
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(Path.Combine(Settings.Folder, "connection-error.log"),
                $"{DateTimeOffset.Now:O}\nStage: {stage}\nAdministrator: {IsAdministrator}\n{error}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return message;
    }
}
