using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed class TextDocumentView : EditorDocumentView
{
    readonly TextBox editor = new() { AcceptsReturn = true, AcceptsTab = true, FontFamily = new("Cascadia Mono, Consolas"), FontSize = 14,
        BorderThickness = new(0), Padding = new(22), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        TextWrapping = TextWrapping.NoWrap, UndoLimit = 200 };
    readonly FlowDocumentScrollViewer preview = new() { IsToolBarVisible = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly Grid split = new();
    readonly TextBlock status = new() { Margin = new(14, 7, 14, 7), Foreground = Brushes.SlateGray };
    readonly TextBox query = new() { Width = 190, Padding = new(7, 4, 7, 4) };
    readonly DispatcherTimer previewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly CancellationTokenSource lifetime = new();
    static readonly MarkdownPipeline pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    Encoding encoding = new UTF8Encoding(false, true);
    string newline = "\r\n";
    long savedLength;
    DateTime savedWrite;
    bool initialized, disposed;
    int previewRevision;
    int editRevision;
    readonly bool markdown;
    string previewText = "";
    public string DocumentText { get => editor.Text; set => editor.Text = value; }
    internal FlowDocument? PreviewDocument => preview.Document;
    public TextDocumentView(WorkspaceWindow owner, string path) : base(owner, path)
    {
        markdown = Path.GetExtension(path).Equals(".md", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(path).Equals(".markdown", StringComparison.OrdinalIgnoreCase);
        var root = new DockPanel(); Content = root;
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new(12, 6, 12, 6) };
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        void Button(string label, Action action) { var button = new Button { Content = label, Padding = new(10, 5, 10, 5) }; button.Click += (_, _) => action(); toolbar.Children.Add(button); }
        Button("编辑", () => SetMode(0));
        if (markdown) { Button("预览", () => SetMode(1)); Button("分栏", () => SetMode(2)); }
        toolbar.Children.Add(query); Button("查找下一个", FindNext);
        DockPanel.SetDock(status, Dock.Bottom); root.Children.Add(status); root.Children.Add(split);
        split.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        split.ColumnDefinitions.Add(new() { Width = new(0) });
        split.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        split.Children.Add(editor); Grid.SetColumn(preview, 2); split.Children.Add(preview);
        var divider = new Border { Background = new SolidColorBrush(Color.FromRgb(226, 231, 240)), Width = 1 };
        Grid.SetColumn(divider, 1); split.Children.Add(divider);
        editor.TextChanged += (_, _) => { if (!initialized) return; ++editRevision; IsModified = true; Changed?.Invoke(); UpdateStatus(); previewTimer.Stop(); if (markdown && preview.Visibility == Visibility.Visible) previewTimer.Start(); };
        previewTimer.Tick += async (_, _) => { previewTimer.Stop(); await RenderPreview(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { e.Handled = true; query.Focus(); query.SelectAll(); } };
        SetMode(markdown ? 2 : 0); Loaded += async (_, _) => { if (!initialized && !disposed) await LoadText(); };
    }
    async Task LoadText()
    {
        try {
            var info = new FileInfo(FilePath); if (info.Length > 50 * 1024 * 1024) throw new IOException("文本文件超过当前编辑器的容量，请分段打开。");
            var bytes = await File.ReadAllBytesAsync(FilePath, lifetime.Token);
            string text = await Task.Run(() => {
                int skip = 0;
                if (bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) { encoding = new UTF8Encoding(true, true); skip = 3; }
                else if (bytes.AsSpan().StartsWith(new byte[] { 255, 254 })) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
                else if (bytes.AsSpan().StartsWith(new byte[] { 254, 255 })) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
                try { return encoding.GetString(bytes, skip, bytes.Length - skip); }
                catch (DecoderFallbackException) { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); encoding = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); return encoding.GetString(bytes); }
            }, lifetime.Token);
            if (disposed) return;
            newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            editor.Text = text; savedLength = info.Length; savedWrite = info.LastWriteTimeUtc;
            initialized = true; IsModified = false; UpdateStatus(); Changed?.Invoke();
            if (markdown) await RenderPreview();
        } catch (OperationCanceledException) { }
        catch (Exception error) { status.Text = "文件暂时无法打开：" + error.Message; editor.IsReadOnly = true; }
        finally { ready.TrySetResult(); }
    }
    void SetMode(int mode)
    {
        editor.Visibility = mode == 1 ? Visibility.Collapsed : Visibility.Visible;
        preview.Visibility = mode == 0 ? Visibility.Collapsed : Visibility.Visible;
        split.ColumnDefinitions[0].Width = mode == 1 ? new(0) : new(1, GridUnitType.Star);
        split.ColumnDefinitions[1].Width = mode == 2 ? new(1) : new(0);
        split.ColumnDefinitions[2].Width = mode == 0 ? new(0) : new(1, GridUnitType.Star);
        if (initialized && markdown && mode != 0) { previewTimer.Stop(); previewTimer.Start(); }
    }
    void UpdateStatus() => status.Text = $"{encoding.WebName.ToUpperInvariant()}  ·  {editor.Text.Length:N0} 个字符" + (IsModified ? "  ·  尚未保存" : "");
    void FindNext()
    {
        if (query.Text.Length == 0) return;
        int begin = Math.Min(editor.Text.Length, editor.SelectionStart + Math.Max(1, editor.SelectionLength));
        int found = editor.Text.IndexOf(query.Text, begin, StringComparison.OrdinalIgnoreCase);
        if (found < 0) found = editor.Text.IndexOf(query.Text, StringComparison.OrdinalIgnoreCase);
        if (found < 0) { status.Text = "没有找到匹配的文字。"; return; }
        if (editor.Visibility != Visibility.Visible) SetMode(2);
        editor.Select(found, query.Text.Length); editor.ScrollToLine(editor.GetLineIndexFromCharacterIndex(found)); editor.Focus();
    }
    public override async Task<bool> SaveAsync()
    {
        if (!initialized || IsSaving || disposed) return false;
        IsSaving = true; Changed?.Invoke(); string? temporary = null;
        int savedRevision = editRevision;
        try {
            var text = editor.Text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", newline);
            var current = new FileInfo(FilePath);
            if (!current.Exists || current.Length != savedLength || current.LastWriteTimeUtc != savedWrite) throw new IOException("文件已在其他程序中更改，请保留当前编辑并另存为。");
            if (current.IsReadOnly) throw new IOException("文件为只读，请修改权限或另存为。");
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("链接文件请使用另存为，当前修改仍保留。");
            temporary = Path.Combine(current.DirectoryName!, ".FullOffice-text-" + Guid.NewGuid().ToString("N") + ".tmp");
            var preamble = encoding.GetPreamble(); var data = await Task.Run(() => encoding.GetBytes(text), lifetime.Token);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                await file.WriteAsync(preamble, lifetime.Token); await file.WriteAsync(data, lifetime.Token); await file.FlushAsync(lifetime.Token);
            }
            current.Refresh(); if (current.Length != savedLength || current.LastWriteTimeUtc != savedWrite) throw new IOException("保存期间文件发生变化，当前修改仍保留。");
            string backups = Path.Combine(current.DirectoryName!, ".FlowOfficeBackups"); Directory.CreateDirectory(backups); File.SetAttributes(backups, File.GetAttributes(backups) | FileAttributes.Hidden);
            string backup = Path.Combine(backups, current.Name + ".previous"); File.Replace(temporary, FilePath, backup, true); temporary = null;
            current.Refresh(); savedLength = current.Length; savedWrite = current.LastWriteTimeUtc; IsModified = editRevision != savedRevision; UpdateStatus(); return !IsModified;
        } catch (Exception error) { status.Text = "保存失败，修改仍保留：" + error.Message; return false; }
        finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } } IsSaving = false; Changed?.Invoke(); }
    }
    public override async Task ExportAsync()
    {
        if (!initialized || disposed) return;
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(FilePath), Filter = "文本文件|*" + Path.GetExtension(FilePath) + "|所有文件|*.*", OverwritePrompt = true };
        if (dialog.ShowDialog(Owner) != true) return;
        if (Path.GetFullPath(dialog.FileName).Equals(FilePath, StringComparison.OrdinalIgnoreCase)) { await SaveAsync(); return; }
        await File.WriteAllTextAsync(dialog.FileName, editor.Text, encoding, lifetime.Token);
    }
    async Task RenderPreview()
    {
        try { await RenderPreviewCore(); }
        catch (OperationCanceledException) { }
        catch (Exception error) { status.Text = "预览暂时无法显示：" + error.Message; }
    }
    async Task RenderPreviewCore()
    {
        if (disposed || preview.Visibility != Visibility.Visible) return;
        string text = editor.Text; int revision = ++previewRevision;
        if (text.Length > 1024 * 1024) { preview.Document = null; status.Text = "文档较长，暂不生成预览；仍可编辑和保存全部内容。"; return; }
        var syntax = await Task.Run(() => Markdown.Parse(text, pipeline), lifetime.Token);
        if (disposed || revision != previewRevision) return;
        previewText = text;
        var document = new FlowDocument { FontFamily = new("Microsoft YaHei UI"), FontSize = 15, PagePadding = new(28), Foreground = Brushes.Black };
        int count = 0;
        foreach (var block in syntax) { AppendBlock(document.Blocks, block); if (++count % 50 == 0) { await Dispatcher.Yield(DispatcherPriority.Background); if (disposed || revision != previewRevision) return; } }
        preview.Document = document;
    }
    string Raw(MarkdownObject node) => node.Span.Start >= 0 && node.Span.End < previewText.Length ? previewText.Substring(node.Span.Start, node.Span.End - node.Span.Start + 1) : "";
    void AppendBlock(BlockCollection target, Markdig.Syntax.Block block)
    {
        if (block is ListBlock list) { var output = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.None : TextMarkerStyle.Disc }; int number = 1; if (list.IsOrdered) int.TryParse(System.Text.RegularExpressions.Regex.Match(Raw(list), @"^\s*(\d+)").Groups[1].Value, out number); foreach (ListItemBlock item in list) { var entry = new ListItem(); foreach (var child in item) AppendBlock(entry.Blocks, child); if (list.IsOrdered && entry.Blocks.FirstBlock is Paragraph p) { var marker = new Run((number++) + ". "); if (p.Inlines.FirstInline is System.Windows.Documents.Inline first) p.Inlines.InsertBefore(first, marker); else p.Inlines.Add(marker); } output.ListItems.Add(entry); } target.Add(output); return; }
        if (block is Markdig.Extensions.Tables.Table table) {
            var result = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new(0, 10, 0, 16) }; var group = new TableRowGroup(); result.RowGroups.Add(group);
            foreach (Markdig.Extensions.Tables.TableRow row in table) { var output = new System.Windows.Documents.TableRow(); group.Rows.Add(output); foreach (Markdig.Extensions.Tables.TableCell cell in row) { var content = new System.Windows.Documents.TableCell { BorderThickness = new(0.5), BorderBrush = Brushes.LightGray, Padding = new(10, 7, 10, 7), Background = row.IsHeader ? Brushes.WhiteSmoke : Brushes.White }; if (row.IsHeader) content.FontWeight = FontWeights.SemiBold; foreach (var child in cell) AppendBlock(content.Blocks, child); output.Cells.Add(content); } }
            target.Add(result); return;
        }
        if (block is QuoteBlock quote) { var section = new Section { BorderThickness = new(3, 0, 0, 0), BorderBrush = Brushes.LightSteelBlue, Padding = new(14, 6, 8, 6), Background = Brushes.WhiteSmoke }; foreach (var child in quote) AppendBlock(section.Blocks, child); target.Add(section); return; }
        if (block is CodeBlock code) { target.Add(new Paragraph(new Run(code.Lines.ToString())) { FontFamily = new("Consolas"), FontSize = 13, Background = Brushes.WhiteSmoke, Padding = new(12) }); return; }
        if (block is ThematicBreakBlock) { target.Add(new BlockUIContainer(new Border { Height = 1, Background = Brushes.LightGray, Margin = new(0, 12, 0, 12) })); return; }
        if (block is LeafBlock leaf && leaf.Inline != null) { var paragraph = new Paragraph { Margin = new(0, 4, 0, 12) }; if (block is HeadingBlock heading) { paragraph.FontSize = 30 - Math.Min(5, heading.Level) * 2; paragraph.FontWeight = FontWeights.SemiBold; } AppendInline(paragraph.Inlines, leaf.Inline); target.Add(paragraph); return; }
        target.Add(new Paragraph(new Run(Raw(block))));
    }
    void AppendInline(InlineCollection target, Markdig.Syntax.Inlines.Inline node)
    {
        if (node is LiteralInline literal) { target.Add(new Run(literal.Content.ToString())); return; }
        if (node is LineBreakInline line) { target.Add(line.IsHard ? new LineBreak() : new Run(" ")); return; }
        if (node is CodeInline code) { target.Add(new Run(code.Content) { FontFamily = new("Consolas"), Background = Brushes.WhiteSmoke }); return; }
        if (node is LinkInline link) { var span = new Hyperlink(); foreach (var child in link) AppendInline(span.Inlines, child); if (link.IsImage) {
                if (LocalImage(link.Url) is Image image) target.Add(new InlineUIContainer(image)); else target.Add(new Run("[图片：" + new TextRange(span.ContentStart, span.ContentEnd).Text + "]")); return;
            } if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && (uri.Scheme == "https" || uri.Scheme == "http")) { span.NavigateUri = uri; span.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; }; } target.Add(span); return; }
        if (node is EmphasisInline emphasis) { var span = new Span(); if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough; else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; else span.FontStyle = FontStyles.Italic; foreach (var child in emphasis) AppendInline(span.Inlines, child); target.Add(span); return; }
        if (node is ContainerInline container) { foreach (var child in container) AppendInline(target, child); return; }
        target.Add(new Run(Raw(node)));
    }
    public override void Activate() { if (initialized && editor.Visibility == Visibility.Visible) editor.Focus(); }
    public override void Deactivate() { previewTimer.Stop(); }
    Image? LocalImage(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || Uri.TryCreate(reference, UriKind.Absolute, out _)) return null;
        try {
            string directory = Path.GetDirectoryName(FilePath)!; string path = Path.GetFullPath(Path.Combine(directory, Uri.UnescapeDataString(reference)));
            if (!path.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
            var file = new FileInfo(path); if (!file.Exists || file.Length > 8 * 1024 * 1024) return null;
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1400; bitmap.UriSource = new(path); bitmap.EndInit(); bitmap.Freeze();
            return new Image { Source = bitmap, MaxWidth = 550, MaxHeight = 700, Stretch = Stretch.Uniform, Margin = new(0, 8, 0, 8) };
        } catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException) { return null; }
    }
    public override void Dispose() { if (disposed) return; disposed = true; previewTimer.Stop(); lifetime.Cancel(); ready.TrySetResult(); }
}
