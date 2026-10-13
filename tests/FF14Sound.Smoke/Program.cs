using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FF14Sound;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        IntegrationChecks.Run().GetAwaiter().GetResult();
        if (args.Contains("--tts"))
            IntegrationChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        var window = new MainWindow();
        var root = (FrameworkElement)window.Content;
        window.Content = null;
        var canvas = new Border { Background = window.Background, Child = root };
        TextElement.SetFontFamily(canvas, window.FontFamily);
        TextElement.SetFontSize(canvas, window.FontSize);
        TextElement.SetForeground(canvas, window.Foreground);
        canvas.Measure(new Size(1120, 830));
        canvas.Arrange(new Rect(0, 0, 1120, 830));
        canvas.UpdateLayout();
        if (window.FindName("SpeechLanguage") is not ComboBox { Items.Count: 8 }) throw new Exception("Speech languages missing");
        if (new Settings().AutoSpeak != true) throw new Exception("Auto dubbing must default on");
        var image = new RenderTargetBitmap(1120, 830, 96, 96, PixelFormats.Pbgra32);
        image.Render(canvas);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        var output = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "gui-smoke.png");
        using (var stream = File.Create(output)) encoder.Save(stream);
        var translator = new Translator();
        var same = translator.Translate("Hello", "en", "en", CancellationToken.None).GetAwaiter().GetResult();
        if (same != "Hello") throw new Exception("Same-language passthrough failed");
        if (args.Contains("--live"))
        {
            var translated = translator.Translate("Good morning, my friend.", "en", "zh-TW", CancellationToken.None).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(translated) || translated == "Good morning, my friend.") throw new Exception("Live translation failed");
            Console.WriteLine("Live translation: " + translated);
        }
        window.Close();
        Console.WriteLine("PASS: WPF construction, language controls, automatic speech default, translation adapter, render: " + output);
    }
}
