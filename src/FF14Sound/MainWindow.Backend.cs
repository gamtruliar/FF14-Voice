using System.Windows;

namespace FF14Sound;

public partial class MainWindow
{
    private bool restartingBackend;

    private async void RestartBackend_Click(object sender, RoutedEventArgs e)
    {
        if (restartingBackend) return;
        restartingBackend = true;
        RestartBackend.IsEnabled = false;
        Server.IsEnabled = false;
        var resumeReading = timer.IsEnabled;
        timer.Stop(); Cancel();
        Status.Text = "正在準備重啟 VoxCPM…";
        try
        {
            SaveCurrentSettings();
            var progress = new Progress<string>(message => { if (!closing) Status.Text = message; });
            await BackendRestart.Run(Server.Text.Trim(), settings, progress, lifetime.Token);
        }
        catch (OperationCanceledException) { if (!closing) Status.Text = "重啟已取消"; }
        catch (Exception ex) { if (!closing) Status.Text = ex.Message; }
        finally
        {
            restartingBackend = false;
            if (!closing)
            {
                RestartBackend.IsEnabled = true; Server.IsEnabled = true;
                ResetDialogueContext();
                if (resumeReading && game is not null) { lastLine = ""; timer.Start(); }
            }
        }
    }
}
