using System.IO;
using System.Windows;

namespace FF14Sound;
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        var batchPlan = e.Args.Length == 2 && e.Args[0] == "--batch-run" ? Path.GetFullPath(e.Args[1]) : null;
        // Tataru's resource loaders resolve paths from the working directory.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        if (batchPlan is not null)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            try { await Task.Run(() => BatchDubbing.Run(batchPlan, CancellationToken.None)); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(ex);
                try { File.AppendAllText(Path.Combine(Path.GetDirectoryName(batchPlan)!, "startup-errors.log"), DateTimeOffset.Now + " " + ex + Environment.NewLine); }
                catch (IOException) { }
            }
            Shutdown(); return;
        }
        if (e.Args.Length == 1 && e.Args[0] == "--batch-window")
        {
            base.OnStartup(e);
            MainWindow = new BatchWindow(Settings.Load()); MainWindow.Show(); return;
        }
        base.OnStartup(e);
        MainWindow = new MainWindow(); MainWindow.Show();
    }
}
