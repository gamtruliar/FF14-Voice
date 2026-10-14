using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace FF14Sound;

public partial class SpeechCacheWindow : Window
{
    private readonly string folder;
    private readonly string planPath;
    private SpeechCacheGroup[] groups = [];
    private bool busy;
    public SpeechCacheWindow(Settings settings)
    {
        InitializeComponent();
        folder = SpeechClient.CacheFolder(settings);
        planPath = BatchDubbing.PlanPath(BatchDubbing.DefaultFolder(settings));
        CacheFolder.Text = folder;
        Loaded += async (_, _) => await RefreshRows();
    }
    private void SetBusy(bool value)
    {
        busy = value; Reload.IsEnabled = ClearAll.IsEnabled = ResetReferences.IsEnabled = !busy;
        ClearSelected.IsEnabled = !busy && Rows.SelectedItem is SpeechCacheGroup;
    }
    private void Filter()
    {
        if (Rows is null || Summary is null) return;
        var text = Search.Text.Trim();
        Rows.ItemsSource = groups.Where(g => g.Speaker.Contains(text, StringComparison.OrdinalIgnoreCase)).ToArray();
        Summary.Text = $"{groups.Length:N0} 個角色／分組 · {groups.Sum(g => g.Sentences):N0} 句 · {groups.Sum(g => g.References):N0} 個首句 REF · {groups.Sum(g => g.Bytes) / 1048576.0:N2} MB";
    }
    private async Task RefreshRows()
    {
        SetBusy(true);
        try
        {
            groups = await Task.Run(() =>
            {
                using var lease = SpeechCache.Lease(folder, exclusive: true);
                SpeechCache.ImportLegacyOwners(folder, planPath);
                return SpeechCache.Scan(folder);
            });
            Filter(); Status.Text = "選擇角色後可按人清除；未識別嘅舊音檔亦可獨立清除。";
        }
        catch (IOException) { Status.Text = "快取正被使用或無法讀取，請先暫停背景預配，再重新整理。"; }
        catch (Exception ex) { Status.Text = "讀取失敗：" + ex.Message; }
        finally { SetBusy(false); }
    }
    private async Task Delete(bool all)
    {
        if (busy) return;
        var selected = Rows.SelectedItem as SpeechCacheGroup;
        if (!all && selected is null) return;
        var reset = ResetReferences.IsChecked == true;
        var label = all ? "全部角色（包括搜尋結果以外）" : selected!.Speaker;
        if (MessageBox.Show(this, $"清除 {label} 嘅已生成對白？\n" + (reset ? "同時刪除自動保存嘅首句 reference，下次重新生成聲線。" : "會保留首句 reference 聲線。"),
            "清除配音快取", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        SetBusy(true);
        try
        {
            var count = await Task.Run(() => SpeechCache.Clear(folder, all ? null : selected!.VoiceKey, reset));
            await RefreshRows(); Status.Text = $"已清除 {count:N0} 個生成音檔。";
        }
        catch (IOException) { Status.Text = "音檔仍在使用或無法刪除；請先暫停背景預配，再重新整理及重試。"; }
        catch (Exception ex) { Status.Text = "清除失敗：" + ex.Message; }
        finally { SetBusy(false); }
    }
    private async void Reload_Click(object sender, RoutedEventArgs e) => await RefreshRows();
    private void Search_Changed(object sender, TextChangedEventArgs e) => Filter();
    private void Rows_Changed(object sender, SelectionChangedEventArgs e)
    { if (ClearSelected is not null) ClearSelected.IsEnabled = !busy && Rows.SelectedItem is SpeechCacheGroup; }
    private async void ClearSelected_Click(object sender, RoutedEventArgs e) => await Delete(false);
    private async void ClearAll_Click(object sender, RoutedEventArgs e) => await Delete(true);
}
