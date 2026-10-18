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
    private readonly string? settingsFile;
    private readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool settingsReady;
    private bool refreshingModels;
    private CloudflareAccess? accessSession;
    private CancellationTokenSource? accessLogin;
    private SubtitleWindow? subtitles;
    private bool subtitlesCleared;
    private readonly Translator translator;
    private readonly SpeechClient speech = new();
    private readonly GameVoiceLibrary library = new();
    private readonly NpcVoiceRegistry npcVoices = new(Path.Combine(Settings.Folder, "npc-voices.json"));
    private VoiceNameCandidate[] nameCandidates = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly MediaPlayer player = new();
    private StreamingPcmPlayer? streamingPlayer;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly ObservableCollection<string> history = [];
    private GameReader? game;
    private CancellationTokenSource? operation;
    private string lastLine = "";
    private bool reading;
    private bool closing;
    private int generation;
    private record LanguageOption(string Code, string Label);

    public MainWindow() : this(null) { }
    public MainWindow(string? settingsFile)
    {
        this.settingsFile = settingsFile;
        InitializeComponent();
        if (GameAccess.IsAdministrator) { ElevateButton.Visibility = Visibility.Collapsed; Title += " · 管理員"; }
        try { settings = Settings.Load(settingsFile); }
        catch (Exception ex) { settings = new(); Status.Text = "設定讀取失敗：" + ex.Message; }
        translator = new();
        TranslationEngine.ItemsSource = new[] { new LanguageOption("ollama", "Ollama"), new("gemini", "Gemini · Google"), new("google", "Google Translate") };
        TranslationEngine.SelectedValue = settings.TranslationEngine;
        if (TranslationEngine.SelectedIndex < 0) TranslationEngine.SelectedValue = "ollama";
        OllamaUrl.Text = settings.OllamaUrl;
        OllamaModel.Text = settings.OllamaModel;
        try { accessSession = CloudflareAccess.Restore(settings.OllamaBrowserSessionProtected); }
        catch (InvalidOperationException ex) { Status.Text = ex.Message; }
        UpdateAccessStatus();
        GeminiModel.Text = settings.GeminiModel;
        GeminiKeyFile.Text = settings.GeminiKeyFile;
        settings.Subtitles ??= new();
        SpeechMode.ItemsSource = new[] { new LanguageOption("translated", "翻譯配音"), new LanguageOption("original", "原文配音") };
        SpeechMode.SelectedValue = settings.SpeechMode;
        if (SpeechMode.SelectedIndex < 0) SpeechMode.SelectedValue = "translated";
        SourceLanguage.ItemsSource = new[] { new LanguageOption("en", "English"), new("ja", "日本語"), new("de", "Deutsch"), new("fr", "Français") };
        TargetLanguage.ItemsSource = new[] { new LanguageOption("zh-TW", "繁體中文 / 普通話"), new("yue", "廣東話"), new("zh-CN", "简体中文"), new("en", "English"), new("ja", "日本語"), new("de", "Deutsch"), new("fr", "Français") };
        SpeechLanguage.ItemsSource = new[] { new LanguageOption("auto", "Auto / 跟隨文字"), new("yue", "廣東話"), new("zh", "普通話"), new("en", "English"), new("ja", "日本語"), new("ko", "한국어"), new("fr", "Français"), new("de", "Deutsch") };
        SourceLanguage.SelectedValue = settings.SourceLanguage;
        TargetLanguage.SelectedValue = settings.TargetLanguage;
        SpeechLanguage.SelectedValue = "zh";
        Server.Text = settings.Server;
        DefaultDescription.Text = settings.DefaultVoiceDescription;
        AutoSpeak.IsChecked = settings.AutoSpeak;
        StreamSpeech.IsChecked = settings.StreamSpeech;
        SpeechVolume.Value = double.IsFinite(settings.SpeechVolumePercent) ? Math.Clamp(settings.SpeechVolumePercent, 0, 400) : 100;
        SpeechSpeed.Value = PcmTempoProvider.ValidateSpeed(settings.SpeechSpeed);
        var levels = (settings.PlaybackLevels ?? new()).Validated();
        AutoVolume.IsChecked = levels.Enabled;
        VolumeMinimum.Value = levels.MinimumDb; VolumeMaximum.Value = levels.MaximumDb; VolumePeak.Value = levels.PeakDb;
        MainPin.IsChecked = settings.MainTopmost;
        ReferenceLanguage.ItemsSource = new[] { new LanguageOption("ja", "日語版原聲"), new("en", "英語版原聲"), new("de", "德語版原聲"), new("fr", "法語版原聲") };
        ReferenceLanguage.SelectedValue = settings.ReferenceLanguage;
        var catalogPath = string.IsNullOrWhiteSpace(settings.CatalogPath) ? GameVoiceLibrary.FindCatalog() : settings.CatalogPath;
        if (File.Exists(catalogPath)) LoadLibrary(catalogPath);
        else
        {
            LibraryStatus.Text = "未載入原聲庫／原錄音索引；翻譯可用，配音暫停。";
            try { npcVoices.Load(null); } catch (Exception ex) { LibraryStatus.Text += " NPC 表：" + ex.Message; }
        }
        RefreshProfiles();
        History.ItemsSource = history;
        TargetLanguage.SelectionChanged += AudioSettingChanged;
        SourceLanguage.SelectionChanged += AudioSettingChanged;
        SpeechMode.SelectionChanged += AudioSettingChanged;
        AutoSpeak.Checked += (_, _) => QueueSettingsSave();
        AutoSpeak.Unchecked += (_, _) => { Cancel(); QueueSettingsSave(); };
        StreamSpeech.Checked += (_, _) => { Cancel(); QueueSettingsSave(); };
        StreamSpeech.Unchecked += (_, _) => { Cancel(); QueueSettingsSave(); };
        TranslationEngine.SelectionChanged += (_, _) => { Cancel(); UpdateTranslationControls(); QueueSettingsSave(); };
        OllamaModel.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!refreshingModels) { Cancel(); QueueSettingsSave(); } }));
        OllamaUrl.TextChanged += (_, _) => { accessLogin?.Cancel(); Cancel(); UpdateAccessStatus(); UpdateTranslationControls(); QueueSettingsSave(); };
        GeminiModel.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => { if (!refreshingModels) { Cancel(); QueueSettingsSave(); } }));
        GeminiKeyFile.TextChanged += (_, _) => { Cancel(); QueueSettingsSave(); };
        SpeechVolume.ValueChanged += (_, _) =>
        {
            if (streamingPlayer is not null) streamingPlayer.Gain = (float)(SpeechVolume.Value / 100);
            QueueSettingsSave();
        };
        AutoVolume.Checked += (_, _) => UpdatePlaybackLevels();
        SpeechSpeed.ValueChanged += (_, _) =>
        {
            if (streamingPlayer is not null) streamingPlayer.Speed = SpeechSpeed.Value;
            QueueSettingsSave();
        };
        AutoVolume.Unchecked += (_, _) => UpdatePlaybackLevels();
        VolumeMinimum.ValueChanged += (_, _) => { if (VolumeMinimum.Value > VolumeMaximum.Value - 3) VolumeMaximum.Value = VolumeMinimum.Value + 3; UpdatePlaybackLevels(); };
        VolumeMaximum.ValueChanged += (_, _) => { if (VolumeMinimum.Value > VolumeMaximum.Value - 3) VolumeMinimum.Value = VolumeMaximum.Value - 3; UpdatePlaybackLevels(); };
        VolumePeak.ValueChanged += (_, _) => UpdatePlaybackLevels();
        Server.TextChanged += (_, _) => QueueSettingsSave();
        DefaultDescription.TextChanged += (_, _) => { QueueSettingsSave(); RefreshVoiceMapping(); };
        Original.TextChanged += (_, _) => { subtitlesCleared = false; UpdateSubtitles(); };
        Translated.TextChanged += (_, _) => { subtitlesCleared = false; UpdateSubtitles(); };
        Speaker.TextChanged += (_, _) => { subtitlesCleared = false; UpdateSubtitles(); RefreshVoiceMapping(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); TrySaveSettings(); };
        timer.Tick += Poll;
        player.MediaEnded += (_, _) => { StopPlayback(); Status.Text = "配音完成"; };
        player.MediaFailed += (_, e) => { StopPlayback(); Status.Text = "播放失敗：" + e.ErrorException.Message; };
        settingsReady = true;
        RefreshVoiceMapping();
        UpdateTranslationControls();
        Loaded += async (_, _) => { if (settings.Subtitles.Enabled) OpenSubtitles(); if (TranslationEngine.SelectedValue?.ToString() != "google") await LoadTranslationModels(); };
        Closing += (_, _) => { accessLogin?.Cancel(); saveTimer.Stop(); TrySaveSettings(); closing = true; subtitles?.Close(); };
        Closed += (_, _) => { closing = true; lifetime.Cancel(); timer.Stop(); Cancel(); game?.Dispose(); speech.Dispose(); translator.Dispose(); lifetime.Dispose(); };
    }

    private void RefreshProfiles() { Profiles.ItemsSource = null; Profiles.ItemsSource = settings.Voices; }
    private void StopPlayback()
    {
        streamingPlayer?.Dispose(); streamingPlayer = null;
        player.Close();
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
        ObserveNpc();
        var engine = TranslationEngine.SelectedValue?.ToString() ?? "ollama";
        var options = engine == "gemini"
            ? new TranslationOptions(engine, Server.Text.Trim(), GeminiModel.Text.Trim(), GeminiKeyFile.Text.Trim())
            : new TranslationOptions(engine, OllamaUrl.Text.Trim(), OllamaModel.Text.Trim(), Access: CurrentOllamaAccess());
        var label = options.Engine switch { "ollama" => "Ollama · " + options.Model, "gemini" => "Gemini · " + options.Model, _ => "Google Translate" };
        if (engine is "ollama" or "gemini")
        {
            var glossary = TranslationGlossary.Context(text, Speaker.Text.Trim(), SourceLanguage.SelectedValue?.ToString() ?? "en", TargetLanguage.SelectedValue?.ToString() ?? "zh-TW");
            label += $" · FF14 詞表 {glossary.Matches.Length} 項";
            TranslationStatus.ToolTip = string.Join("\n", glossary.Matches.Select(m => m.Source + " → " + m.Target));
        }
        else TranslationStatus.ToolTip = "Google Translate 網頁引擎唔支援詞表提示；請選 Ollama 或 Gemini。";
        TranslationStatus.Text = label + " · 翻譯中…";
        Status.Text = "翻譯中…";
        var watch = Stopwatch.StartNew();
        string translated;
        try { translated = await translator.Translate(text, SourceLanguage.SelectedValue?.ToString() ?? "en", TargetLanguage.SelectedValue?.ToString() ?? "zh-TW", token, options, Speaker.Text.Trim()); }
        catch (Exception ex) { if (version == generation) TranslationStatus.Text = label + " · " + (ex is OperationCanceledException ? "已取消" : ex.Message); throw; }
        token.ThrowIfCancellationRequested();
        if (version != generation) return;
        Translated.Text = translated;
        TranslationStatus.Text = $"{label} · {watch.Elapsed.TotalSeconds:F2}s";
        history.Insert(0, $"{Speaker.Text}: {translated}");
        while (history.Count > 100) history.RemoveAt(history.Count - 1);
        if (library.NativeAudio is not null && CurrentHasNativeAudio()) Status.Text = NativeAudioStatus;
        else if (streamingPlayer is null) Status.Text = "翻譯完成 · 可按配音";
    }
    private const string NativeAudioStatus = "已有遊戲原錄音 · 交由 GAME 播放；本程式不生成／不播放此句";
    private bool CurrentHasNativeAudio() => library.HasNativeAudio(Speaker.Text.Trim(), Original.Text,
        SourceLanguage.SelectedValue?.ToString() ?? "en");
    private async Task SpeakCurrent(CancellationToken token, int version)
    {
        // Before selection, cache lookup and playback, including manual and translated dubbing.
        if (CurrentHasNativeAudio()) { Status.Text = NativeAudioStatus; return; }
        var name = Speaker.Text.Trim();
        var selection = SpeechSelection.Choose(SpeechMode.SelectedValue?.ToString() ?? "translated", Original.Text, Translated.Text,
            SourceLanguage.SelectedValue?.ToString() ?? "en", TargetLanguage.SelectedValue?.ToString() ?? "zh-TW");
        ObserveNpc();
        settings.DefaultVoiceDescription = DefaultDescription.Text.Trim();
        var assignment = library.ResolveAssignment(name, selection.Language, settings, npcVoices.DescriptionFor(name, settings.DefaultVoiceDescription), npcVoices.Find(name)?.Key);
        var voice = selection.Apply(assignment.Profile);
        ShowVoiceMapping(name, assignment);
        var text = selection.Text;
        var voiceLabel = assignment.Label;
        if (StreamSpeech.IsChecked == true)
        {
            Status.Text = $"{name} · 等候首段聲音…";
            using var playback = new StreamingPcmPlayer { Gain = (float)(SpeechVolume.Value / 100), Levels = CurrentPlaybackLevels(), Speed = SpeechSpeed.Value };
            streamingPlayer = playback;
            var started = Stopwatch.StartNew(); bool first = true;
            try
            {
                var result = await speech.Stream(Server.Text.Trim(), text, voice, async pcm =>
                {
                    token.ThrowIfCancellationRequested();
                    if (version != generation || CurrentHasNativeAudio()) throw new OperationCanceledException(token);
                    await playback.Add(pcm, token);
                    if (first) { first = false; Status.Text = $"正在播放：{name} · 首段 {started.Elapsed.TotalSeconds:F2}s"; }
                }, token, version, SpeechClient.CacheFolder(settings));
                await playback.Finish(token);
                if (version == generation) { RefreshVoiceMapping(); Status.Text = $"配音完成 · {(result.CacheHit ? "快取" : result.Streamed ? "串流" : "完整音檔（backend 未支援串流）")} · 首段 {result.FirstAudioSeconds:F2}s"; }
            }
            finally { if (ReferenceEquals(streamingPlayer, playback)) streamingPlayer = null; }
            return;
        }
        Status.Text = $"{name} · {voiceLabel} · {voice.Language} 生成中…";
        var watch = Stopwatch.StartNew();
        var wav = await speech.Generate(Server.Text.Trim(), text, voice, token, version, SpeechClient.CacheFolder(settings));
        token.ThrowIfCancellationRequested();
        if (version != generation) return;
        if (CurrentHasNativeAudio()) { Status.Text = NativeAudioStatus; return; }
        using var completePlayback = new StreamingPcmPlayer { Gain = (float)(SpeechVolume.Value / 100), Levels = CurrentPlaybackLevels(), Speed = SpeechSpeed.Value };
        streamingPlayer = completePlayback;
        try
        {
            Status.Text = $"正在播放：{name} · {voiceLabel} · 生成 / 快取 {watch.Elapsed.TotalSeconds:F1}s";
            await SpeechClient.DeliverWave(wav, pcm => completePlayback.Add(pcm, token), token);
            await completePlayback.Finish(token);
            if (version == generation) { RefreshVoiceMapping(); Status.Text = "配音完成"; }
        }
        finally { if (ReferenceEquals(streamingPlayer, completePlayback)) streamingPlayer = null; }
    }
    private void VolumePreset_Click(object sender, RoutedEventArgs e) => SpeechVolume.Value = double.Parse(((Button)sender).Tag.ToString()!, System.Globalization.CultureInfo.InvariantCulture);
    private void SpeedReset_Click(object sender, RoutedEventArgs e) => SpeechSpeed.Value = 1;
    private PlaybackLevelSettings CurrentPlaybackLevels() => new(AutoVolume.IsChecked == true, VolumeMinimum.Value, VolumeMaximum.Value, VolumePeak.Value);
    private void UpdatePlaybackLevels()
    {
        if (streamingPlayer is not null) streamingPlayer.Levels = CurrentPlaybackLevels();
        QueueSettingsSave();
    }
    private void UpdateTranslationControls()
    {
        var engine = TranslationEngine.SelectedValue?.ToString();
        var local = engine == "ollama"; var cloud = engine == "gemini";
        OllamaModelPanel.Visibility = local ? Visibility.Visible : Visibility.Collapsed;
        GeminiModelPanel.Visibility = cloud ? Visibility.Visible : Visibility.Collapsed;
        OllamaModel.IsEnabled = local; OllamaUrl.IsEnabled = local; GeminiModel.IsEnabled = cloud;
        RefreshModels.IsEnabled = local || cloud;
        var remote = Uri.TryCreate(OllamaUrl.Text.Trim(), UriKind.Absolute, out var ollamaAddress) && !ollamaAddress.IsLoopback;
        TranslationStatus.Text = cloud ? "Gemini · google-genai SDK · 本句原文及角色名稱會傳到 Google 翻譯。"
            : local ? remote ? $"Ollama · {ollamaAddress!.Host} · 本句原文及角色名稱會傳到呢個服務翻譯。"
                : "本機翻譯 · 首句需要載入模型；失敗會顯示錯誤，唔會自動轉 Google。" : "Google Translate · 網上翻譯";
    }
    private void BrowseGeminiKey_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "選擇 Gemini API key 檔案", Filter = "文字檔|*.txt;*.key|所有檔案|*.*" };
        if (dialog.ShowDialog(this) == true) GeminiKeyFile.Text = dialog.FileName;
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e) => await LoadTranslationModels();
    private CloudflareAccess? CurrentOllamaAccess() => accessSession?.Matches(OllamaUrl.Text) == true ? accessSession : null;
    private void UpdateAccessStatus()
    {
        var session = CurrentOllamaAccess();
        AccessStatus.Text = session is null ? "此 URL 未登入；按網頁登入，喺瀏覽器完成認證。"
            : session.ExpiresAt <= DateTimeOffset.UtcNow.AddSeconds(30) ? "登入已過期，請重新網頁登入。"
            : $"已有登入狀態 · 有效至 {session.ExpiresAt.LocalDateTime:g}";
    }
    private async void AccessLogin_Click(object sender, RoutedEventArgs e)
    {
        if (accessLogin is not null) return;
        var address = OllamaUrl.Text.Trim();
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        accessLogin = pending;
        AccessLogin.IsEnabled = false; AccessCancel.IsEnabled = true;
        AccessStatus.Text = "正在開啟瀏覽器，請完成 Cloudflare 登入…";
        try
        {
            var session = await CloudflareBrowserLogin.Login(address, pending.Token);
            pending.Token.ThrowIfCancellationRequested();
            if (closing || address != OllamaUrl.Text.Trim()) return;
            Cancel(); accessSession = session; UpdateAccessStatus(); SaveCurrentSettings();
            if (TranslationEngine.SelectedValue?.ToString() == "ollama") await LoadTranslationModels();
        }
        catch (OperationCanceledException) { if (!closing) { UpdateAccessStatus(); AccessStatus.Text += "（已取消登入）"; } }
        catch (Exception ex) { if (!closing) AccessStatus.Text = ex.Message; }
        finally { accessLogin = null; if (!closing) { AccessLogin.IsEnabled = true; AccessCancel.IsEnabled = false; } }
    }
    private void AccessCancel_Click(object sender, RoutedEventArgs e) => accessLogin?.Cancel();
    private void AccessClear_Click(object sender, RoutedEventArgs e)
    {
        accessLogin?.Cancel(); Cancel(); accessSession = null; UpdateAccessStatus(); QueueSettingsSave();
    }
    private async Task LoadTranslationModels()
    {
        var engine = TranslationEngine.SelectedValue?.ToString();
        if (engine == "google") return;
        var cloud = engine == "gemini";
        var picker = cloud ? GeminiModel : OllamaModel;
        var address = cloud ? Server.Text.Trim() : OllamaUrl.Text.Trim();
        var keyFile = GeminiKeyFile.Text.Trim(); var selected = picker.Text;
        var access = CurrentOllamaAccess();
        bool IsCurrent() => !closing && address == (cloud ? Server.Text.Trim() : OllamaUrl.Text.Trim()) &&
            keyFile == GeminiKeyFile.Text.Trim() && selected == picker.Text && TranslationEngine.SelectedValue?.ToString() == engine &&
            (cloud || ReferenceEquals(access, CurrentOllamaAccess()));
        var label = cloud ? "Gemini" : "Ollama";
        RefreshModels.IsEnabled = false;
        try
        {
            var models = cloud ? await translator.GeminiModels(address, keyFile, lifetime.Token) : await translator.OllamaModels(address, lifetime.Token, access);
            if (!IsCurrent()) return;
            refreshingModels = true;
            try { picker.ItemsSource = models; picker.Text = selected; }
            finally { refreshingModels = false; }
            TranslationStatus.Text = models.Contains(selected) ? $"{label} 已連接 · {models.Length} 個模型 · {selected}" : $"{label} 已連接 · 請選擇可用模型。";
        }
        catch (Exception ex) { if (IsCurrent()) TranslationStatus.Text = label + " 連接失敗：" + ex.Message; }
        finally { if (!closing) RefreshModels.IsEnabled = TranslationEngine.SelectedValue?.ToString() != "google"; }
    }
    private async void Translate_Click(object sender, RoutedEventArgs e) => await Run(TranslateCurrent);
    private async void Speak_Click(object sender, RoutedEventArgs e) => await Run(SpeakCurrent);
    private async void TranslateSpeak_Click(object sender, RoutedEventArgs e) => await Run((t, v) => ProcessDialogue(t, v, true));
    private Task ProcessDialogue(CancellationToken token, int version, bool speak) => SpeechWorkflow.Run(
        () => TranslateCurrent(token, version), speak ? () => SpeakCurrent(token, version) : null,
        SpeechMode.SelectedValue?.ToString() == "original", library.NativeAudio is not null && CurrentHasNativeAudio());
    private void Stop_Click(object sender, RoutedEventArgs e) { Cancel(); Status.Text = "已停止 / 略過今句配音"; }
    private void Topmost_Checked(object sender, RoutedEventArgs e) { Topmost = true; QueueSettingsSave(); }
    private void Topmost_Unchecked(object sender, RoutedEventArgs e) { Topmost = false; QueueSettingsSave(); }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        timer.Stop(); Cancel();
        var button = (Button)sender;
        button.IsEnabled = false;
        var reader = new GameReader();
        var stage = "VoxCPM 預載";
        try
        {
            game?.Dispose(); game = null;
            var language = SourceLanguage.SelectedValue?.ToString() switch { "ja" => "Japanese", "de" => "German", "fr" => "French", _ => "English" };
            Status.Text = "預載 VoxCPM 模型中…";
            if (AutoSpeak.IsChecked == true) await speech.WarmUp(Server.Text.Trim(), lifetime.Token);
            stage = "連接 FF14";
            Status.Text = "連接遊戲中…";
            await Task.Run(() => reader.Attach(language));
            if (closing) { reader.Dispose(); return; }
            game = reader;
            lastLine = ""; timer.Start(); Status.Text = "已連接 · 等待對白";
        }
        catch (Exception ex) { reader.Dispose(); Status.Text = GameAccess.Record(ex, stage); }
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
                if (lastLine.Length > 0) { lastLine = ""; Cancel(); subtitlesCleared = true; UpdateSubtitles(); }
                return;
            }
            var signature = line.Value.Speaker + "\n" + line.Value.Text;
            if (signature == lastLine) return;
            lastLine = signature;
            Translated.Clear(); Speaker.Text = line.Value.Speaker; Original.Text = line.Value.Text;
            // Start work without blocking the reader: a newer line cancels this one.
            _ = Run((t, v) => ProcessDialogue(t, v, AutoSpeak.IsChecked == true));
        }
        catch (Exception ex) { timer.Stop(); Cancel(); subtitlesCleared = true; UpdateSubtitles(); Status.Text = GameAccess.Record(ex, "讀取 FF14 對白"); }
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
            var previous = settings.Voices.FirstOrDefault(v => string.Equals(v.Speaker, name, StringComparison.OrdinalIgnoreCase));
            var existingVoice = previous ?? library.ResolveAssignment(name, lang, settings, npcIdentity: npcVoices.Find(name)?.Key).Profile;
            var voice = new VoiceProfile { Speaker = name, ReferenceAudio = ReferenceAudio.Text, Transcript = Transcript.Text.Trim(), Control = Control.Text.Trim(), Language = lang,
                Seed = existingVoice.EffectiveSeed, VoiceIdentity = existingVoice.EffectiveVoiceIdentity };
            settings.Voices.RemoveAll(v => string.Equals(v.Speaker, name, StringComparison.OrdinalIgnoreCase));
            settings.Voices.Add(voice); SaveCurrentSettings(); RefreshProfiles(); Profiles.SelectedItem = voice;
            Status.Text = $"已儲存 {name} 嘅聲音";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (Profiles.SelectedItem is not VoiceProfile voice) return;
        try { settings.Voices.Remove(voice); SaveCurrentSettings(); RefreshProfiles(); Status.Text = "已刪除角色設定"; }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings(); Status.Text = "設定已儲存";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void Batch_Click(object sender, RoutedEventArgs e)
    {
        try { SaveCurrentSettings(); new BatchWindow(settings) { Owner = this }.Show(); }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void SpeechCache_Click(object sender, RoutedEventArgs e)
    {
        var resume = timer.IsEnabled; timer.Stop(); Cancel();
        try { SaveCurrentSettings(); new SpeechCacheWindow(settings) { Owner = this }.ShowDialog(); RefreshVoiceMapping(); }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { if (resume && !closing) timer.Start(); }
    }

    private void SaveCurrentSettings()
    {
        settings.Server = Server.Text.Trim(); settings.SourceLanguage = SourceLanguage.SelectedValue?.ToString() ?? "en";
        settings.TargetLanguage = TargetLanguage.SelectedValue?.ToString() ?? "zh-TW";
        settings.TranslationEngine = TranslationEngine.SelectedValue?.ToString() ?? "ollama";
        settings.OllamaUrl = OllamaUrl.Text.Trim();
        settings.OllamaModel = OllamaModel.Text.Trim();
        settings.OllamaBrowserSessionProtected = accessSession?.Protect() ?? "";
        settings.GeminiModel = GeminiModel.Text.Trim();
        settings.GeminiKeyFile = GeminiKeyFile.Text.Trim();
        settings.AutoSpeak = AutoSpeak.IsChecked == true;
        settings.StreamSpeech = StreamSpeech.IsChecked == true;
        settings.SpeechVolumePercent = SpeechVolume.Value;
        settings.SpeechSpeed = SpeechSpeed.Value;
        settings.PlaybackLevels = CurrentPlaybackLevels();
        settings.SpeechMode = SpeechMode.SelectedValue?.ToString() ?? "translated";
        settings.MainTopmost = MainPin.IsChecked == true;
        settings.ReferenceLanguage = ReferenceLanguage.SelectedValue?.ToString() ?? "ja";
        settings.DefaultVoiceDescription = DefaultDescription.Text.Trim();
        settings.Save(settingsFile);
        SettingsStatus.Text = "設定已自動儲存";
        RefreshVoiceMapping();
    }

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSettings();
            var executable = Path.Combine(AppContext.BaseDirectory, "FF14Sound.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("請使用 Start-FF14Sound-Admin.cmd 啟動。", executable);
            using var started = Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory
            });
            if (started is not null) Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { Status.Text = "已取消管理員重啟；目前視窗仍然開啟。"; }
        catch (Exception ex) { Status.Text = "管理員重啟失敗：" + ex.Message; }
    }

    private void LoadLibrary(string path)
    {
        try
        {
            library.Load(path);
            settings.CatalogPath = path;
            var voices = library.Resolver!.Catalog.Voices;
            GameVoices.ItemsSource = voices.OrderBy(v => v.Label).ToArray();
            var evidencePath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "voice-name-candidates.json");
            nameCandidates = File.Exists(evidencePath)
                ? System.Text.Json.JsonSerializer.Deserialize<VoiceNameCandidate[]>(File.ReadAllText(evidencePath)) ?? [] : [];
            LibraryStatus.Text = $"{voices.Length} 個說話者 · {voices.Sum(v => v.References.Length)} 個 clone references\n未配對先用預設聲音，並記入 NPC 表。";
            if (library.NativeAudio is null) LibraryStatus.Text += "\n缺少原錄音索引；配音暫停，請重建聲音庫。";
            try { npcVoices.Load(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "npc-voice-index.json")); }
            catch (Exception ex) { LibraryStatus.Text += "\nNPC 表讀取失敗：" + ex.Message; }
            QueueSettingsSave();
            RefreshVoiceMapping();
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
        QueueSettingsSave();
        UpdateVoiceDetails();
        RefreshVoiceMapping();
    }
    private void GameVoice_Changed(object sender, SelectionChangedEventArgs e) => UpdateVoiceDetails();
    private void UpdateVoiceDetails()
    {
        if (GameVoices.SelectedItem is not GameVoice voice) return;
        var reference = voice.References.FirstOrDefault(r => r.Language == settings.ReferenceLanguage);
        VoiceDetails.Text = reference is null ? "此角色未有所選語言嘅原聲。" : $"{reference.Language} · {reference.Seconds:F1}s · {reference.Sheet}" +
            (reference.Seconds < 3 ? "\n樣本較短，clone 效果可能較弱。" : "") +
            (string.IsNullOrWhiteSpace(reference.Transcript) ? "" : "\n原聲對白：" + reference.Transcript);
        var evidence = nameCandidates.Where(c => c.Token == voice.Token).DistinctBy(c => (string.Join("|", c.Names), c.Quest)).ToArray();
        CandidateEvidence.Header = $"名稱／任務配對證據（{evidence.Length}）";
        NameEvidence.Text = evidence.Length == 0 ? "暫時冇額外名称／任務證據；可先試聽原聲同核對對白。" :
            string.Join("\n\n", evidence.Select(c => $"{string.Join(" / ", c.Names.Take(2))}\n{c.Evidence}" + (c.Quest.Length > 0 ? "\n" + c.Quest : "")));
    }
    private void MapSpeaker_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var name = Speaker.Text.Trim();
            if (name.Length == 0 || GameVoices.SelectedItem is not GameVoice voice) throw new InvalidOperationException("請先填目前 NPC 名稱並選擇角色。");
            settings.SpeakerAliases[name] = voice.Token;
            SaveCurrentSettings();
            Status.Text = $"已記住：{name} → {voice.Token}。自訂聲音覆蓋仍優先使用。";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }

    private void ObserveNpc()
    {
        try { npcVoices.Observe(Speaker.Text.Trim(), SourceLanguage.SelectedValue?.ToString() ?? "en"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { history.Insert(0, "NPC 表未能儲存（繼續配音）：" + ex.Message); }
    }
    private void PreviewReference_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (GameVoices.SelectedItem is not GameVoice voice) throw new InvalidOperationException("請先選擇角色代號。");
            var match = library.Resolver?.Resolve(voice.Token, settings.ReferenceLanguage,
                new Dictionary<string, string> { [voice.Token] = voice.Token });
            if (match is null) throw new InvalidOperationException("所選版本嘅原聲樣本不存在。");
            Cancel();
            // Reference audition uses its own player and never modifies the source sample.
            player.Open(new Uri(match.FullPath)); player.Play();
            Status.Text = $"試聽原聲：{voice.Token} · {settings.ReferenceLanguage}";
        }
        catch (Exception ex) { Status.Text = ex.Message; }
    }
    private void NpcTable_Click(object sender, RoutedEventArgs e)
    {
        ObserveNpc();
        SaveCurrentSettings();
        new NpcVoicesWindow(npcVoices, row => library.DescribeRow(row, settings)) { Owner = this }.ShowDialog();
        RefreshVoiceMapping();
    }

    private void AudioSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        Cancel(); QueueSettingsSave();
        RefreshVoiceMapping();
        if (ReferenceEquals(sender, SourceLanguage) && game is not null)
        {
            timer.Stop(); game.Dispose(); game = null; lastLine = "";
            subtitlesCleared = true; UpdateSubtitles();
            Status.Text = "遊戲文字語言已更改，請重新連接 FF14。";
        }
    }
    private void QueueSettingsSave()
    {
        if (!settingsReady || closing) return;
        SettingsStatus.Text = "正在儲存設定…"; saveTimer.Stop(); saveTimer.Start();
    }
    private void TrySaveSettings()
    {
        if (!settingsReady) return;
        try { SaveCurrentSettings(); }
        catch (Exception ex) { SettingsStatus.Text = "設定儲存失敗：" + ex.Message; }
    }
    private void Subtitles_Click(object sender, RoutedEventArgs e) => OpenSubtitles();
    private void OpenSubtitles()
    {
        if (subtitles is not null) { if (subtitles.WindowState == WindowState.Minimized) subtitles.WindowState = WindowState.Normal; return; }
        settings.Subtitles.Enabled = true;
        subtitles = new SubtitleWindow(settings.Subtitles, QueueSettingsSave);
        subtitles.Closed += (_, _) =>
        {
            subtitles = null;
            if (!closing) { settings.Subtitles.Enabled = false; QueueSettingsSave(); }
        };
        UpdateSubtitles(); subtitles.Show(); QueueSettingsSave();
    }
    private void UpdateSubtitles()
    {
        if (subtitlesCleared) subtitles?.ShowDialogue("", "", "");
        else subtitles?.ShowDialogue(Speaker.Text, Original.Text, Translated.Text);
    }

    private void RefreshVoiceMapping()
    {
        if (!settingsReady) return;
        var name = Speaker.Text.Trim();
        if (name.Length == 0)
        {
            CurrentVoice.Text = "使用聲音：等候 NPC 名稱"; CurrentVoice.ToolTip = null;
            CurrentVoiceDescriptionLabel.Text = "Voice description"; CurrentVoiceDescription.Text = "等候 NPC 名稱"; return;
        }
        try
        {
            var language = (SpeechMode.SelectedValue?.ToString() == "original" ? SourceLanguage : TargetLanguage).SelectedValue?.ToString() ?? "en";
            ShowVoiceMapping(name, library.ResolveAssignment(name, language, settings, npcVoices.DescriptionFor(name, DefaultDescription.Text.Trim()), npcVoices.Find(name)?.Key));
        }
        catch (Exception ex)
        {
            CurrentVoice.Text = "使用聲音：" + ex.Message; CurrentVoice.ToolTip = null;
            CurrentVoiceDescriptionLabel.Text = "Voice description（未能套用）";
            CurrentVoiceDescription.Text = npcVoices.DescriptionFor(name, DefaultDescription.Text.Trim());
        }
    }
    private void ShowVoiceMapping(string name, VoiceAssignment assignment)
    {
        var learned = SpeechCache.LearnedReference(SpeechClient.CacheFolder(settings), assignment.Profile);
        var label = learned is not null ? "default · 沿用首次生成聲線（專屬 REF）" : assignment.Label;
        CurrentVoice.Text = $"使用聲音：{name} → {label} · Seed：{assignment.Profile.EffectiveSeed}";
        CurrentVoice.ToolTip = (learned ?? assignment.Profile.ReferenceAudio) + (assignment.Profile.Control.Length > 0 ? "\nVoice description：" + assignment.Profile.Control : "");
        var applied = !string.IsNullOrWhiteSpace(assignment.Profile.Control);
        CurrentVoiceDescriptionLabel.Text = applied ? "Voice description（套用於配音）" : "Voice description（未套用，使用 reference 音色）";
        CurrentVoiceDescription.Text = applied ? assignment.Profile.Control : npcVoices.DescriptionFor(name, DefaultDescription.Text.Trim());
        CurrentVoiceDescription.ToolTip = npcVoices.Find(name)?.VoiceTraitsBasis;
    }
}
