using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using FF14Sound.Voices;

namespace FF14Sound;

public partial class MainWindow : Window
{
    private readonly Settings settings;
    private readonly Translator translator;
    private readonly SpeechClient speech = new();
    private readonly GameVoiceLibrary library = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly MediaPlayer player = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<string> history = [];
    private GameReader? game;
    private CancellationTokenSource? operation;
    private string lastLine = "";
    private string? playingFile;
    private bool reading;
    private bool closing;
    private int generation;
    private record LanguageOption(string Code, string Label);

    public MainWindow()
    {
        InitializeComponent();
        try { settings = Settings.Load(); }
        catch (Exception ex) { settings = new(); Status.Text = "設定讀取失敗：" + ex.Message; }
        translator = new();
        SourceLanguage.ItemsSource = new[] { new LanguageOption("en", "English"), new("ja", "日本語"), new("de", "Deutsch"), new("fr", "Français") };
        TargetLanguage.ItemsSource = new[] { new LanguageOption("zh-TW", "繁體中文 / 普通話"), new("yue", "廣東話"), new("zh-CN", "简体中文"), new("en", "English"), new("ja", "日本語"), new("de", "Deutsch"), new("fr", "Français") };
        SpeechLanguage.ItemsSource = new[] { new LanguageOption("auto", "Auto / 跟隨文字"), new("yue", "廣東話"), new("zh", "普通話"), new("en", "English"), new("ja", "日本語"), new("ko", "한국어"), new("fr", "Français"), new("de", "Deutsch") };
        SourceLanguage.SelectedValue = settings.SourceLanguage;
        TargetLanguage.SelectedValue = settings.TargetLanguage;
        SpeechLanguage.SelectedValue = "zh";
        Server.Text = settings.Server;
        AutoSpeak.IsChecked = settings.AutoSpeak;
        ReferenceLanguage.ItemsSource = new[] { new LanguageOption("ja", "日語版原聲"), new("en", "英語版原聲"), new("de", "德語版原聲"), new("fr", "法語版原聲") };
        ReferenceLanguage.SelectedValue = settings.ReferenceLanguage;
        var catalogPath = string.IsNullOrWhiteSpace(settings.CatalogPath) ? GameVoiceLibrary.FindCatalog() : settings.CatalogPath;
        if (File.Exists(catalogPath)) LoadLibrary(catalogPath);
        else LibraryStatus.Text = "請先執行 Build-VoiceLibrary.cmd，或載入 catalog.json。";
        RefreshProfiles();
        History.ItemsSource = history;
        TargetLanguage.SelectionChanged += (_, _) => Cancel();
        SourceLanguage.SelectionChanged += (_, _) => Cancel();
        AutoSpeak.Unchecked += (_, _) => Cancel();
        timer.Tick += Poll;
        player.MediaEnded += (_, _) => { StopPlayback(); Status.Text = "配音完成"; };
        player.MediaFailed += (_, e) => { StopPlayback(); Status.Text = "播放失敗：" + e.ErrorException.Message; };
        Closed += (_, _) => { closing = true; lifetime.Cancel(); timer.Stop(); Cancel(); game?.Dispose(); speech.Dispose(); lifetime.Dispose(); };
    }

    private void RefreshProfiles() { Profiles.ItemsSource = null; Profiles.ItemsSource = settings.Voices; }
    private void StopPlayback()
    {
        player.Close();
        if (playingFile is not null) { try { File.Delete(playingFile); } catch (IOException) { } playingFile = null; }
    }
    private void Cancel()
    {
        generation++; operation?.Cancel(); operation?.Dispose(); operation = null; StopPlayback();
        _ = speech.CancelPending(Server.Text.Trim(), generation);
    }
    private async Task Run(Func<CancellationToken, int, Task> action)
    {
        Cancel();
        operation = new();
        var token = operation.Token;
        var version = generation;
        try { await action(token, version); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (version == generation) Status.Text = ex.Message; }
    }
    private async Task TranslateCurrent(CancellationToken token, int version)
    {
        var text = Original.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("請輸入對白。");
        Status.Text = "翻譯中…";
        var translated = await translator.Translate(text, SourceLanguage.SelectedValue?.ToString() ?? "en", TargetLanguage.SelectedValue?.ToString() ?? "zh-TW", token);
        token.ThrowIfCancellationRequested();
        if (version != generation) return;
        Translated.Text = translated;
        history.Insert(0, $"{Speaker.Text}: {translated}");
        while (history.Count > 100) history.RemoveAt(history.Count - 1);
        Status.Text = "翻譯完成 · 可按配音";
    }
    private async Task SpeakCurrent(CancellationToken token, int version)
    {
        var name = Speaker.Text.Trim();
        var target = TargetLanguage.SelectedValue?.ToString() ?? "zh-TW";
        var voice = library.Resolve(name, target, settings);
        var text = Translated.Text.Trim();
        if (text.Length == 0) throw new InvalidOperationException("請先翻譯或輸入配音文字。");
        Status.Text = $"{name} · {voice.Language} 配音生成中…";
        var watch = Stopwatch.StartNew();
        var wav = await speech.Generate(Server.Text.Trim(), text, voice, token, version, Path.Combine(Settings.Folder, "speech-cache"));
        token.ThrowIfCancellationRequested();
        if (version != generation) return;
        Directory.CreateDirectory(Settings.Folder);
        var file = Path.Combine(Settings.Folder, $"speech-{Guid.NewGuid():N}.wav");
        try
        {
            await File.WriteAllBytesAsync(file, wav, token);
            token.ThrowIfCancellationRequested();
            if (version != generation) return;
            playingFile = file;
            player.Open(new Uri(file)); player.Play();
        }
        finally { if (playingFile != file) File.Delete(file); }
        Status.Text = $"正在播放：{name} · 生成 / 快取 {watch.Elapsed.TotalSeconds:F1}s";
    }
    private async void Translate_Click(object sender, RoutedEventArgs e) => await Run(TranslateCurrent);
    private async void Speak_Click(object sender, RoutedEventArgs e) => await Run(SpeakCurrent);
    private async void TranslateSpeak_Click(object sender, RoutedEventArgs e) => await Run(async (t, v) => { await TranslateCurrent(t, v); await SpeakCurrent(t, v); });
    private void Stop_Click(object sender, RoutedEventArgs e) { Cancel(); Status.Text = "已停止 / 略過今句配音"; }
    private void Topmost_Checked(object sender, RoutedEventArgs e) => Topmost = true;
    private void Topmost_Unchecked(object sender, RoutedEventArgs e) => Topmost = false;
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        timer.Stop(); Cancel();
        var button = (Button)sender;
        button.IsEnabled = false;
        var reader = new GameReader();
        try
        {
            game?.Dispose(); game = null;
            var language = SourceLanguage.SelectedValue?.ToString() switch { "ja" => "Japanese", "de" => "German", "fr" => "French", _ => "English" };
            Status.Text = "預載 VoxCPM 模型中…";
            if (AutoSpeak.IsChecked == true) await speech.WarmUp(Server.Text.Trim(), lifetime.Token);
            Status.Text = "連接遊戲中…";
            await Task.Run(() => reader.Attach(language));
            if (closing) { reader.Dispose(); return; }
            game = reader;
            lastLine = ""; timer.Start(); Status.Text = "已連接 · 等待對白";
        }
        catch (Exception ex) { reader.Dispose(); Status.Text = ex.Message; }
        finally { button.IsEnabled = true; }
    }
    private async void Poll(object? sender, EventArgs e)
    {
        if (reading || game is null) return;
        reading = true;
        try
        {
            var line = game.Read();
            if (line is null)
            {
                if (lastLine.Length > 0) { lastLine = ""; Cancel(); }
                return;
            }
            var signature = line.Value.Speaker + "\n" + line.Value.Text;
            if (signature == lastLine) return;
            lastLine = signature;
            Speaker.Text = line.Value.Speaker; Original.Text = line.Value.Text; Translated.Clear();
            // Start work without blocking the reader: a newer line cancels this one.
            _ = Run(async (t, v) => { await TranslateCurrent(t, v); if (AutoSpeak.IsChecked == true) await SpeakCurrent(t, v); });
        }
        catch (Exception ex) { timer.Stop(); Cancel(); Status.Text = ex.Message; }
        finally { reading = false; }
        await Task.CompletedTask;
    }
    private void Web_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var uri = new Uri(Server.Text.Trim());
            if (!uri.IsLoopback || uri.Scheme != "http") throw new InvalidOperationException("請使用本機 http 地址。");
            Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Audio|*.wav;*.flac;*.mp3;*.ogg" };
        if (picker.ShowDialog() == true) { ReferenceAudio.Text = picker.FileName; Transcript.Clear(); }
    }
    private void Profiles_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Profiles.SelectedItem is not VoiceProfile voice) return;
        ProfileName.Text = voice.Speaker; ReferenceAudio.Text = voice.ReferenceAudio;
        Transcript.Text = voice.Transcript; Control.Text = voice.Control; SpeechLanguage.SelectedValue = voice.Language;
    }
    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = ProfileName.Text.Trim();
            if (name.Length == 0 || !File.Exists(ReferenceAudio.Text)) throw new InvalidOperationException("請填角色名稱同有效音檔。");
            var lang = SpeechLanguage.SelectedValue?.ToString() ?? "auto";
            if (!string.IsNullOrWhiteSpace(Transcript.Text) && (lang != "auto" || !string.IsNullOrWhiteSpace(Control.Text)))
                throw new InvalidOperationException("有樣本文字時請用 Auto 語言並清空風格，或清空樣本文字使用一般 clone。");
            var voice = new VoiceProfile { Speaker = name, ReferenceAudio = ReferenceAudio.Text, Transcript = Transcript.Text.Trim(), Control = Control.Text.Trim(), Language = lang };
            settings.Voices.RemoveAll(v => string.Equals(v.Speaker, name, StringComparison.OrdinalIgnoreCase));
            settings.Voices.Add(voice); settings.Save(); RefreshProfiles(); Profiles.SelectedItem = voice;
            Status.Text = $"已儲存 {name} 嘅聲音";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not VoiceProfile voice) return;
        try { settings.Voices.Remove(voice); settings.Save(); RefreshProfiles(); Status.Text = "已刪除角色設定"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            settings.Server = Server.Text.Trim(); settings.SourceLanguage = SourceLanguage.SelectedValue?.ToString() ?? "en";
            settings.TargetLanguage = TargetLanguage.SelectedValue?.ToString() ?? "zh-TW";
            settings.AutoSpeak = AutoSpeak.IsChecked == true;
            settings.Save(); Status.Text = "設定已儲存";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void LoadLibrary(string path)
    {
        try
        {
            library.Load(path);
            settings.CatalogPath = path;
            var voices = library.Resolver!.Catalog.Voices;
            GameVoices.ItemsSource = voices.OrderBy(v => v.Label).ToArray();
            LibraryStatus.Text = $"{voices.Length} 個說話者 · {voices.Sum(v => v.References.Length)} 個 clone references\n自動匹配名稱；未識別嘅 NPC 可喺下方配對。";
        }
        catch (Exception ex) { LibraryStatus.Text = "聲音庫讀取失敗：" + ex.Message; }
    }
    private void LoadLibrary_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Voice catalog|catalog.json|JSON|*.json" };
        if (picker.ShowDialog() == true) LoadLibrary(picker.FileName);
    }
    private async void Warmup_Click(object sender, RoutedEventArgs e) => await Run(async (t, v) =>
    {
        Status.Text = "預載 VoxCPM 模型中…";
        await speech.WarmUp(Server.Text.Trim(), t);
        t.ThrowIfCancellationRequested();
        if (v == generation) Status.Text = "VoxCPM 已準備好";
    });
    private void ReferenceLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceLanguage.SelectedValue is not string language) return;
        settings.ReferenceLanguage = language;
        if (IsLoaded) Cancel();
        UpdateVoiceDetails();
    }
    private void GameVoice_Changed(object sender, SelectionChangedEventArgs e) => UpdateVoiceDetails();
    private void UpdateVoiceDetails()
    {
        if (GameVoices.SelectedItem is not GameVoice voice) return;
        var reference = voice.References.FirstOrDefault(r => r.Language == settings.ReferenceLanguage);
        VoiceDetails.Text = reference is null ? "此角色未有所選語言嘅原聲。" : $"{reference.Language} · {reference.Seconds:F1}s · {reference.Sheet}" +
            (reference.Seconds < 3 ? "\n樣本較短，clone 效果可能較弱。" : "");
    }
    private void MapSpeaker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = Speaker.Text.Trim();
            if (name.Length == 0 || GameVoices.SelectedItem is not GameVoice voice) throw new InvalidOperationException("請先填目前 NPC 名稱並選擇角色。");
            settings.SpeakerAliases[name] = voice.Token;
            settings.Save();
            Status.Text = $"已記住：{name} → {voice.Token}。自訂聲音覆蓋仍優先使用。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
}
