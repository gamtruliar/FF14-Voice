using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using FF14Sound.Voices;
using Microsoft.Win32;

namespace FF14Sound;

public partial class NpcVoicesWindow : Window
{
    private readonly NpcVoiceRegistry registry;
    private readonly List<NpcVoiceEntry> rows;
    private ListCollectionView? view;
    public NpcVoicesWindow(NpcVoiceRegistry registry, Action<NpcVoiceEntry>? describeMapping = null)
    {
        InitializeComponent(); this.registry = registry;
        rows = registry.Rows.OrderByDescending(r => r.LastSeen).ThenBy(r => r.Name).Select(r => r.Copy()).ToList();
        foreach (var row in rows) describeMapping?.Invoke(row);
        CvColumn.ItemsSource = new[] { "未確認", "確認無原聲", "有原聲待配對" };
        view = new ListCollectionView(rows) { Filter = Matches };
        Table.ItemsSource = view; Filter.SelectedIndex = 0;
    }
    private bool Matches(object value)
    {
        var row = (NpcVoiceEntry)value;
        if (Filter.SelectedIndex == 0 && row.ReferenceTokens.Length > 0) return false;
        if (Filter.SelectedIndex == 2 && row.LastSeen is null) return false;
        if (Filter.SelectedIndex == 3 && row.CvMark != "確認無原聲") return false;
        var query = Search.Text.Trim();
        return query.Length == 0 || row.Names.Append(row.Key).Append(string.Join(" ", row.NpcIds)).Append(row.VoiceDescription).Append(row.Notes).Append(row.VoiceMapping)
            .Any(n => n.Contains(query, StringComparison.OrdinalIgnoreCase));
    }
    private void Commit() { Table.CommitEdit(DataGridEditingUnit.Cell, true); Table.CommitEdit(DataGridEditingUnit.Row, true); }
    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (view is null) return;
        Commit(); view.Refresh(); Summary.Text = $"顯示 {view.Count:N0} / {rows.Count:N0} 個 NPC 名稱組；相同多語言名稱共用描述，所有 NPC IDs 保留於 CSV。";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try { Commit(); registry.ApplyEdits(rows); Table.Items.Refresh(); Summary.Text = "已儲存；下一句未配對 NPC 配音會套用描述。已有原聲／自訂配音繼續優先。"; }
        catch (Exception ex) { Summary.Text = "儲存失敗：" + ex.Message; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var picker = new SaveFileDialog { Filter = "CSV|*.csv", FileName = "npc-voices.csv" };
        if (picker.ShowDialog() != true) return;
        try { Commit(); NpcVoiceIndex.ExportCsv(picker.FileName, rows.Where(r => Matches(r))); Summary.Text = "已匯出：" + picker.FileName; }
        catch (Exception ex) { Summary.Text = "匯出失敗：" + ex.Message; }
    }
}
