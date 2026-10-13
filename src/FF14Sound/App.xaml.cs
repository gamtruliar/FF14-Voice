using System.IO;
using System.Windows;

namespace FF14Sound;
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Tataru's resource loaders resolve paths from the working directory.
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        base.OnStartup(e);
    }
}
