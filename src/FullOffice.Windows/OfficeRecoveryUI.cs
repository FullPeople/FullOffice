using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed partial class WorkspaceWindow
{
    async Task ShowOfficeRecovery()
    {
        var store = new OfficeRecoveryStore();
        OfficeRecoveryScan scan;
        try { scan = await store.ScanAsync(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "无法读取恢复记录"); return; }
        var dialog = new Window { Owner = this, Title = "恢复编辑会话", Width = 700, Height = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, FontFamily = FontFamily, FontSize = 13 };
        var panel = new StackPanel { Margin = new(20) };
        dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(new TextBlock { Text = "选择保留的文档，恢复到你指定的位置。", FontSize = 18, Margin = new(0, 0, 0, 8) });
        panel.Children.Add(new TextBlock { Text = "可恢复内容为编辑器已写入临时文件的最近一次保存。尚未写入的键入内容可能不包含在内。",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.SlateGray, Margin = new(0, 0, 0, 18) });
        if (scan.Warnings.Count > 0) panel.Children.Add(new TextBlock { Text = "部分会话无法读取，已保留：\n" + string.Join("\n", scan.Warnings),
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DarkGoldenrod, Margin = new(0, 0, 0, 12) });
        if (scan.Entries.Count == 0) panel.Children.Add(new TextBlock { Text = "没有可恢复的编辑会话。", Foreground = Brushes.SlateGray });
        foreach (var entry in scan.Entries) {
            var card = new Border { BorderBrush = Brushes.LightGray, BorderThickness = new(1), CornerRadius = new(8),
                Padding = new(14), Margin = new(0, 0, 0, 10) };
            var row = new StackPanel(); card.Child = row; panel.Children.Add(card);
            row.Children.Add(new TextBlock { Text = Path.GetFileName(entry.Source), FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = entry.Source, Foreground = Brushes.SlateGray,
                TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) });
            row.Children.Add(new TextBlock { Text = entry.HasSnapshot ? "临时保存：" + entry.SavedUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : "打开时的文档临时文件：" + entry.OpenedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Foreground = Brushes.SlateGray });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(0, 9, 0, 0) }; row.Children.Add(actions);
            var recover = new Button { Content = "恢复文档…", Padding = new(12, 5, 12, 5) };
            var remove = new Button { Content = "清理此会话", Padding = new(12, 5, 12, 5), Margin = new(8, 0, 0, 0) };
            actions.Children.Add(recover); actions.Children.Add(remove);
            recover.Click += async (_, _) => {
                var picker = new SaveFileDialog { FileName = Path.GetFileNameWithoutExtension(entry.Source) + "（恢复）" + Path.GetExtension(entry.Source),
                    Filter = "当前文档格式|*" + Path.GetExtension(entry.Source), OverwritePrompt = true };
                if (picker.ShowDialog(dialog) != true) return;
                string target = Path.GetFullPath(picker.FileName), temporary = Path.Combine(Path.GetDirectoryName(target)!,
                    ".FullOffice-recovery-" + Guid.NewGuid().ToString("N") + ".tmp");
                recover.IsEnabled = false; remove.IsEnabled = false;
                try {
                    OfficeRecoveryStore.RejectLinks(target);
                    if (File.Exists(target) && (File.GetAttributes(target) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                        throw new IOException("目标是链接或只读文件，请选择其他位置。");
                    byte[]? previous = File.Exists(target) ? await Task.Run(() => Program.HashFile(target)) : null;
                    await store.CopySnapshotAsync(entry, temporary);
                    await Task.Run(() => ValidateRecoveredDocument(temporary, entry.Source));
                    if (previous != null) {
                        if (!File.Exists(target) || !previous.SequenceEqual(await Task.Run(() => Program.HashFile(target))))
                            throw new IOException("目标文件在恢复期间发生了变化，请重新选择。");
                        var backup = target + ".recovery-backup-" + Guid.NewGuid().ToString("N");
                        File.Replace(temporary, target, backup);
                    } else File.Move(temporary, target);
                    dialog.Close();
                    try { await OpenEditor(target); }
                    catch (Exception error) { MessageBox.Show(this, "文档已恢复到：" + target + "\n打开失败：" + error.Message, "恢复文档"); }
                } catch (Exception error) { MessageBox.Show(dialog, error.Message, "恢复未完成", MessageBoxButton.OK, MessageBoxImage.Information); }
                finally {
                    recover.IsEnabled = true; remove.IsEnabled = true;
                    try { if (File.Exists(temporary)) File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                        MessageBox.Show(dialog.IsVisible ? dialog : this, "暂存文件未能清理：" + temporary + "\n" + error.Message, "恢复文档");
                    }
                }
            };
            remove.Click += (_, _) => {
                if (MessageBox.Show(dialog, "清理后无法再从此会话恢复编辑内容。确定清理？", "清理恢复会话",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                try { store.Retire(entry.Directory); panel.Children.Remove(card); }
                catch (Exception error) { MessageBox.Show(dialog, error.Message, "清理未完成"); }
            };
        }
        dialog.ShowDialog();
    }
    static void ValidateRecoveredDocument(string path, string source)
    {
        using var archive = ZipFile.OpenRead(path);
        string content = Path.GetExtension(source).Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ? "xl/workbook.xml" : "word/document.xml";
        foreach (string name in new[] { "[Content_Types].xml", content }) {
            using var data = (archive.GetEntry(name) ?? throw new IOException("恢复文档缺少必要内容。")).Open();
            using var reader = XmlReader.Create(data, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (reader.Read()) { }
        }
    }
}
