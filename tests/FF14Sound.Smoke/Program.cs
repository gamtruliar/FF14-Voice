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
        if (args.Length == 1 && args[0] == "--tempo-check") { TempoChecks.Run(); return; }
        if (args.Length == 1 && args[0] == "--tempo-device") { TempoChecks.Playback().GetAwaiter().GetResult(); return; }
        if (args.Length == 3 && args[0] == "--tempo-review") { TempoChecks.Export(args[1], args[2]); return; }
        IntegrationChecks.Run().GetAwaiter().GetResult();
        if (args.Contains("--learned-live"))
            SpeechCacheChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        if (args.Contains("--gemini-live"))
            GeminiChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        if (args.Contains("--ollama-live"))
            OllamaChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        if (args.Contains("--stream-live"))
            StreamingChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        if (args.Contains("--tts"))
            IntegrationChecks.Live(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        if (args.Contains("--fallback-tts"))
            IntegrationChecks.LiveFallback(Path.GetDirectoryName(Path.GetFullPath(args[0]))!).GetAwaiter().GetResult();
        var app = new App();
        app.InitializeComponent();
        var testSettingsPath = Path.Combine(Path.GetTempPath(), "ff14sound-ui-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var window = new MainWindow(testSettingsPath);
        ((ComboBox)window.FindName("SourceLanguage")).SelectedValue = "ja";
        ((ComboBox)window.FindName("TargetLanguage")).SelectedValue = "yue";
        ((ComboBox)window.FindName("SpeechMode")).SelectedValue = "original";
        var streamOption = (CheckBox)window.FindName("StreamSpeech");
        if (streamOption.IsChecked != true) throw new Exception("Streaming must default on");
        streamOption.IsChecked = false;
        var volumeOption = (Slider)window.FindName("SpeechVolume");
        if (((ComboBox)window.FindName("TranslationEngine")).SelectedValue?.ToString() != "ollama") throw new Exception("Ollama must be selected for this setup");
        ((ComboBox)window.FindName("OllamaModel")).Text = "test-local-model";
        ((ComboBox)window.FindName("GeminiModel")).Text = "gemini-3.8-flash";
        ((TextBox)window.FindName("GeminiKeyFile")).Text = "APIKey/qqDragon.txt";
        if (volumeOption.Value != 100) throw new Exception("Old settings must retain 100% playback gain");
        volumeOption.Value = 200;
        var speedOption = (Slider)window.FindName("SpeechSpeed");
        if (speedOption.Value != 1) throw new Exception("Old settings must retain normal tempo");
        speedOption.Value = 1.25;
        var speaker = (TextBox)window.FindName("Speaker");
        var currentVoice = (TextBlock)window.FindName("CurrentVoice");
        var nativeCatalog = GameVoiceLibrary.FindCatalog();
        if (nativeCatalog.Length > 0)
        {
            var nativeFile = Path.Combine(Path.GetDirectoryName(nativeCatalog)!, FF14Sound.Voices.NativeDialogue.FileName);
            if (File.Exists(nativeFile))
            {
                var nativeIndex = BatchDubbing.Read<FF14Sound.Voices.NativeVoiceIndex>(nativeFile);
                var voiced = nativeIndex.Lines.First(l => l.Static && l.Language == "ja" && nativeIndex.Names[l.Token].Length > 0);
                speaker.Text = voiced.Token;
                ((TextBox)window.FindName("Original")).Text = voiced.Text;
                ((TextBox)window.FindName("Translated")).Clear();
                var method = typeof(MainWindow).GetMethod("SpeakCurrent", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                foreach (var mode in new[] { "original", "translated" })
                {
                    ((ComboBox)window.FindName("SpeechMode")).SelectedValue = mode;
                    var task = (Task)method.Invoke(window, new object[] { CancellationToken.None, 0 })!;
                    if (!task.IsCompletedSuccessfully) throw new Exception("Native manual speech must return before async generation/cache/playback, even with empty translation");
                    if (!((TextBlock)window.FindName("Status")).Text.Contains("交由 GAME")) throw new Exception("Native audio status missing");
                }
                ((ComboBox)window.FindName("SpeechMode")).SelectedValue = "original";
                ((TextBox)window.FindName("Original")).Clear();
            }
        }
        speaker.Text = "未配對 GUI 測試 NPC";
        if (!currentVoice.Text.Contains("default")) throw new Exception("GUI fallback mapping missing");
        if (GameVoiceLibrary.FindCatalog().Length > 0)
        {
            speaker.Text = "ヤ・シュトラ";
            if (!currentVoice.Text.Contains("YSHTOLA")) throw new Exception("GUI automatic mapping missing");
            ((ComboBox)window.FindName("GameVoices")).SelectedIndex = 0;
            if (!currentVoice.Text.Contains("YSHTOLA")) throw new Exception("Browsing voices must not change actual mapping");
            if (!((TextBlock)window.FindName("CurrentVoiceDescriptionLabel")).Text.Contains("未套用")) throw new Exception("Game clone must not claim unused direction is applied");
            speaker.Text = "ウジェノア";
            if (!((TextBlock)window.FindName("CurrentVoiceDescription")).Text.StartsWith("成年男聲。")) throw new Exception("Current NPC direction is not visible");
            if (!((TextBlock)window.FindName("CurrentVoiceDescriptionLabel")).Text.Contains("套用於配音")) throw new Exception("Fallback direction must be applied");
            if (!currentVoice.Text.Contains("無 clone / 無 reference") || currentVoice.Text.Contains(".wav")) throw new Exception("Default UI must show text-only generation");
            speaker.Clear();
            if (((TextBlock)window.FindName("CurrentVoiceDescription")).Text != "等候 NPC 名稱") throw new Exception("Old voice description was not cleared");
            speaker.Text = "ウジェノア";
        }
        ((TextBox)window.FindName("OllamaUrl")).Text = "https://custom.example.com/ollama/";
        var browserSession = CloudflareAccess.FromToken("https://custom.example.com/ollama/", OllamaChecks.TestToken());
        typeof(MainWindow).GetField("accessSession", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(window, browserSession);
        ((TextBox)window.FindName("OllamaUrl")).Text = "https://another.example.com/";
        if (!((TextBlock)window.FindName("AccessStatus")).Text.Contains("未登入")) throw new Exception("Other URL must not reuse session");
        ((TextBox)window.FindName("OllamaUrl")).Text = "https://custom.example.com/ollama/";
        if (!((TextBlock)window.FindName("AccessStatus")).Text.Contains("有效至")) throw new Exception("Matching URL should reuse session");
        var frame = new System.Windows.Threading.DispatcherFrame();
        var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        delay.Tick += (_, _) => { delay.Stop(); frame.Continue = false; }; delay.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        var autosaved = Settings.Load(testSettingsPath);
        if (autosaved.StreamSpeech) throw new Exception("Streaming toggle was not autosaved");
        if (autosaved.SpeechVolumePercent != 200) throw new Exception("Playback volume was not autosaved");
        if (autosaved.SpeechSpeed != 1.25) throw new Exception("Playback speed was not autosaved");
        if (autosaved.TranslationEngine != "ollama" || autosaved.OllamaModel != "test-local-model" || autosaved.OllamaUrl != "https://custom.example.com/ollama/") throw new Exception("Custom Ollama settings did not persist");
        if (autosaved.GeminiModel != "gemini-3.8-flash" || autosaved.GeminiKeyFile != "APIKey/qqDragon.txt") throw new Exception("Gemini settings did not persist");
        if (CloudflareAccess.Restore(autosaved.OllamaBrowserSessionProtected)?.Token != browserSession.Token || File.ReadAllText(testSettingsPath).Contains(browserSession.Token)) throw new Exception("Cloudflare session must persist encrypted");
        var reopened = new MainWindow(testSettingsPath);
        if (((Slider)reopened.FindName("SpeechSpeed")).Value != 1.25) throw new Exception("Playback speed did not reload");
        if (!((TextBlock)reopened.FindName("AccessStatus")).Text.Contains("有效至")) throw new Exception("Stored Cloudflare session did not reload");
        typeof(MainWindow).GetMethod("AccessClear_Click", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(reopened, new object[] { reopened, new RoutedEventArgs() });
        reopened.Close();
        if (Settings.Load(testSettingsPath).OllamaBrowserSessionProtected.Length != 0) throw new Exception("Clear session did not persist");
        ((ComboBox)window.FindName("TranslationEngine")).SelectedValue = "gemini";
        if (((FrameworkElement)window.FindName("GeminiModelPanel")).Visibility != Visibility.Visible || ((FrameworkElement)window.FindName("OllamaModelPanel")).Visibility != Visibility.Collapsed) throw new Exception("Gemini picker visibility incorrect");
        ((ComboBox)window.FindName("OllamaModel")).Text = "qwen3.5:9b";
        streamOption.IsChecked = true;
        if (autosaved.SourceLanguage != "ja" || autosaved.TargetLanguage != "yue" || autosaved.SpeechMode != "original") throw new Exception("UI changes were not autosaved");
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
        var cacheFolder = Path.Combine(Path.GetTempPath(), "ff14sound-cache-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheFolder);
        foreach (var item in new[] { ("ウジェノア", 'A'), ("イベント案内窓口", 'B') })
        {
            var audio = Path.Combine(cacheFolder, new string(item.Item2, 64) + ".wav");
            File.WriteAllBytes(audio, SpeechCacheChecks.Wave());
            var voice = new VoiceProfile { Speaker = item.Item1, IsFallback = true, Language = "ja" };
            using var lease = SpeechCache.Lease(cacheFolder);
            SpeechCache.Complete(cacheFolder, audio, voice, CancellationToken.None);
        }
        var cacheWindow = new SpeechCacheWindow(new Settings { SpeechCacheFolder = cacheFolder });
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(cacheWindow.Dispatcher));
        var cacheRefresh = (Task)typeof(SpeechCacheWindow).GetMethod("RefreshRows", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(cacheWindow, null)!;
        SynchronizationContext.SetSynchronizationContext(previousContext);
        var cacheFrame = new System.Windows.Threading.DispatcherFrame();
        var cacheDeadline = DateTime.UtcNow.AddSeconds(10);
        var cacheTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        cacheTimer.Tick += (_, _) => { if (cacheRefresh.IsCompleted || DateTime.UtcNow > cacheDeadline) { cacheTimer.Stop(); cacheFrame.Continue = false; } };
        cacheTimer.Start(); System.Windows.Threading.Dispatcher.PushFrame(cacheFrame);
        if (!cacheRefresh.IsCompletedSuccessfully) throw new Exception("Cache inventory GUI did not load: " + cacheRefresh.Status + " / " + cacheRefresh.Exception + " / " + ((TextBlock)cacheWindow.FindName("Status")).Text);
        var cacheRows = (DataGrid)cacheWindow.FindName("Rows");
        if (cacheRows.Items.Count != 2 || ((CheckBox)cacheWindow.FindName("ResetReferences")).IsChecked == true) throw new Exception("Cache GUI inventory or safe default incorrect");
        ((TextBox)cacheWindow.FindName("Search")).Text = "ウジェ";
        if (cacheRows.Items.Count != 1) throw new Exception("NPC cache search failed");
        ((TextBox)cacheWindow.FindName("Search")).Clear(); cacheRows.SelectedIndex = 0;
        if (!((Button)cacheWindow.FindName("ClearSelected")).IsEnabled) throw new Exception("Per-NPC clear button missing");
        var cacheRoot = (FrameworkElement)cacheWindow.Content; cacheWindow.Content = null;
        var cacheCanvas = new Border { Background = cacheWindow.Background, Child = cacheRoot };
        TextElement.SetFontFamily(cacheCanvas, cacheWindow.FontFamily); TextElement.SetFontSize(cacheCanvas, cacheWindow.FontSize);
        cacheCanvas.Measure(new Size(900, 660)); cacheCanvas.Arrange(new Rect(0, 0, 900, 660)); cacheCanvas.UpdateLayout();
        var cacheImage = new RenderTargetBitmap(900, 660, 96, 96, PixelFormats.Pbgra32); cacheImage.Render(cacheCanvas);
        var cacheEncoder = new PngBitmapEncoder(); cacheEncoder.Frames.Add(BitmapFrame.Create(cacheImage));
        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "speech-cache-window.png"))) cacheEncoder.Save(stream);
        cacheWindow.Close();
        var registry = new NpcVoiceRegistry(Path.Combine(Path.GetTempPath(), "ff14sound-render-unused-" + Guid.NewGuid().ToString("N") + ".json"));
        var catalog = GameVoiceLibrary.FindCatalog();
        registry.Load(catalog.Length == 0 ? null : Path.Combine(Path.GetDirectoryName(catalog)!, "npc-voice-index.json"));
        var displayLibrary = new GameVoiceLibrary(); if (catalog.Length > 0) displayLibrary.Load(catalog);
        var tableWindow = new NpcVoicesWindow(registry, row => displayLibrary.DescribeRow(row, new Settings { SourceLanguage = "ja" }));
        if (tableWindow.FindName("Search") is TextBox search) search.Text = "案内";
        var tableRoot = (FrameworkElement)tableWindow.Content; tableWindow.Content = null;
        var tableCanvas = new Border { Background = tableWindow.Background, Child = tableRoot };
        TextElement.SetFontFamily(tableCanvas, tableWindow.FontFamily); TextElement.SetFontSize(tableCanvas, tableWindow.FontSize);
        tableCanvas.Measure(new Size(1180, 760)); tableCanvas.Arrange(new Rect(0, 0, 1180, 760)); tableCanvas.UpdateLayout();
        var tableImage = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32); tableImage.Render(tableCanvas);
        var tableEncoder = new PngBitmapEncoder(); tableEncoder.Frames.Add(BitmapFrame.Create(tableImage));
        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "npc-table.png"))) tableEncoder.Save(stream);
        tableWindow.Close();
        var subtitleOptions = new SubtitleSettings { ContentMode = "both" };
        var subtitleChanges = 0;
        var subtitle = new SubtitleWindow(subtitleOptions, () => subtitleChanges++);
        subtitle.ShowDialogue("コランティオ", "こんにちは。旅の準備はできましたか？", "你好呀，準備好出發未？");
        ((Slider)subtitle.FindName("TextSize")).Value = 32;
        if (subtitleChanges == 0 || subtitleOptions.FontSize != 32) throw new Exception("Subtitle controls do not persist appearance");
        subtitle.ShowDialogue("另一個 NPC", "次の台詞", "");
        if (((TextBlock)subtitle.FindName("TranslatedText")).Text.Length != 0) throw new Exception("Subtitle retained previous translation");
        subtitle.ShowDialogue("コランティオ", "こんにちは。旅の準備はできましたか？", "你好呀，準備好出發未？");
        var subtitleRoot = (FrameworkElement)subtitle.Content; subtitle.Content = null;
        var subtitleCanvas = new Border { Background = new SolidColorBrush(Color.FromRgb(38, 48, 63)), Child = subtitleRoot };
        TextElement.SetFontFamily(subtitleCanvas, subtitle.FontFamily); TextElement.SetFontSize(subtitleCanvas, 14); TextElement.SetForeground(subtitleCanvas, Brushes.White);
        subtitleCanvas.Measure(new Size(960, 230)); subtitleCanvas.Arrange(new Rect(0, 0, 960, 230)); subtitleCanvas.UpdateLayout();
        var subtitleImage = new RenderTargetBitmap(960, 230, 96, 96, PixelFormats.Pbgra32); subtitleImage.Render(subtitleCanvas);
        var subtitleEncoder = new PngBitmapEncoder(); subtitleEncoder.Frames.Add(BitmapFrame.Create(subtitleImage));
        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "subtitle-window.png"))) subtitleEncoder.Save(stream);
        subtitle.Close();
        var translator = new Translator();
        var same = translator.Translate("Hello", "en", "en", CancellationToken.None).GetAwaiter().GetResult();
        if (same != "Hello") throw new Exception("Same-language passthrough failed");
        if (args.Contains("--live"))
        {
            var translated = translator.Translate("Good morning, my friend.", "en", "zh-TW", CancellationToken.None).GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(translated) || translated == "Good morning, my friend.") throw new Exception("Live translation failed");
            Console.WriteLine("Live translation: " + translated);
        }
        var batch = new BatchWindow(new Settings { SourceLanguage = "ja", SpeechMode = "original", CatalogPath = GameVoiceLibrary.FindCatalog() });
        var batchRoot = (FrameworkElement)batch.Content; batch.Content = null;
        var batchCanvas = new Border { Background = batch.Background, Child = batchRoot };
        TextElement.SetFontFamily(batchCanvas, batch.FontFamily); TextElement.SetFontSize(batchCanvas, batch.FontSize);
        batchCanvas.Measure(new Size(840, 700)); batchCanvas.Arrange(new Rect(0, 0, 840, 700)); batchCanvas.UpdateLayout();
        var batchImage = new RenderTargetBitmap(840, 700, 96, 96, PixelFormats.Pbgra32); batchImage.Render(batchCanvas);
        var batchEncoder = new PngBitmapEncoder(); batchEncoder.Frames.Add(BitmapFrame.Create(batchImage));
        using (var stream = File.Create(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "batch-window.png"))) batchEncoder.Save(stream);
        batch.Close();
        window.Close();
        File.Delete(testSettingsPath);
        Console.WriteLine("PASS: WPF construction, debounced autosave, subtitle controls/render, translation adapter, render: " + output);
    }
}
