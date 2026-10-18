using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace FF14Sound;

public partial class SubtitleWindow : Window
{
    private readonly SubtitleSettings options;
    private readonly Action changed;
    private readonly Func<string, Task<string>>? play;
    private int playbackRequest;
    private bool ready;
    private string speaker = "", original = "", translated = "";
    public SubtitleWindow(SubtitleSettings options, Action changed, Func<string, Task<string>>? play = null)
    {
        InitializeComponent(); this.options = options; this.changed = changed; this.play = play;
        var area = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = Place(options, options.Left is null || options.Top is null ? SystemParameters.WorkArea : area);
        Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
        DisplayMode.SelectedValue = options.ContentMode;
        if (DisplayMode.SelectedIndex < 0) DisplayMode.SelectedValue = "translated";
        Pin.IsChecked = options.Topmost; TextSize.Value = Math.Clamp(options.FontSize, 18, 48);
        Shade.Value = Math.Clamp(options.BackgroundOpacity, 0.1, 1);
        ready = true; ApplyAppearance();
        LocationChanged += (_, _) => RememberBounds(); SizeChanged += (_, _) => RememberBounds();
    }
    public static Rect Place(SubtitleSettings value, Rect area)
    {
        var width = Math.Clamp(value.Width, Math.Min(480, area.Width), area.Width);
        var height = Math.Clamp(value.Height, Math.Min(120, area.Height), area.Height);
        return new(Math.Clamp(value.Left ?? area.Left + (area.Width - width) / 2, area.Left, area.Right - width),
            Math.Clamp(value.Top ?? area.Bottom - height - 30, area.Top, area.Bottom - height), width, height);
    }
    public void ShowDialogue(string name, string source, string translation)
    {
        if (speaker != name || original != source)
        { playbackRequest++; PlaybackStatus.Text = ""; }
        speaker = name; original = source; translated = translation; RefreshText();
    }
    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        var mode = ((System.Windows.Controls.Button)sender).Tag?.ToString() ?? "translated";
        if (play is null || string.IsNullOrWhiteSpace(mode == "original" ? original : translated)) return;
        var request = ++playbackRequest;
        PlaybackStatus.Text = "準備配音…";
        try
        {
            var status = await play(mode);
            if (request == playbackRequest) PlaybackStatus.Text = status;
        }
        catch (Exception ex) { if (request == playbackRequest) PlaybackStatus.Text = ex.Message; }
    }
    private void RefreshText()
    {
        SpeakerText.Text = speaker;
        OriginalText.Text = original; TranslatedText.Text = translated;
        OriginalText.Visibility = options.ContentMode == "translated" ? Visibility.Collapsed : Visibility.Visible;
        TranslatedText.Visibility = options.ContentMode == "original" ? Visibility.Collapsed : Visibility.Visible;
        OriginalText.FontSize = options.ContentMode == "original" ? options.FontSize : Math.Max(16, options.FontSize * 0.72);
        TranslatedText.FontSize = options.FontSize;
        PlayOriginal.IsEnabled = play is not null && !string.IsNullOrWhiteSpace(original);
        PlayTranslated.IsEnabled = play is not null && !string.IsNullOrWhiteSpace(translated);
    }
    private void ApplyAppearance()
    {
        Topmost = options.Topmost;
        Panel.Background = new SolidColorBrush(Color.FromArgb((byte)(255 * options.BackgroundOpacity), 13, 19, 29));
        Toolbar.Visibility = options.Compact ? Visibility.Collapsed : Visibility.Visible;
        CompactHint.Visibility = options.Compact ? Visibility.Visible : Visibility.Collapsed;
        RefreshText();
    }
    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        options.ContentMode = DisplayMode.SelectedValue?.ToString() ?? "translated";
        options.FontSize = TextSize.Value; options.BackgroundOpacity = Shade.Value; options.Topmost = Pin.IsChecked == true;
        ApplyAppearance(); changed();
    }
    private void RememberBounds()
    {
        if (!ready || !IsLoaded || WindowState != WindowState.Normal) return;
        options.Left = Left; options.Top = Top; options.Width = ActualWidth; options.Height = ActualHeight; changed();
    }
    private void Compact_Click(object sender, RoutedEventArgs e) { options.Compact = !options.Compact; ApplyAppearance(); changed(); }
    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Compact_Click(sender, e); e.Handled = true; return; }
        if (e.LeftButton == MouseButtonState.Pressed) { DragMove(); e.Handled = true; }
    }
    private void Bottom_Click(object sender, RoutedEventArgs e)
    {
        var savedLeft = options.Left; var savedTop = options.Top;
        options.Left = null; options.Top = null; var bounds = Place(options, SystemParameters.WorkArea);
        options.Left = savedLeft; options.Top = savedTop;
        Width = bounds.Width; Height = bounds.Height; Left = bounds.Left; Top = bounds.Top;
        RememberBounds();
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
