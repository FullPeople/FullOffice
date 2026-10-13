using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowOfficeLab;

public static class DocumentFormats
{
    static readonly HashSet<string> text = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".txt", ".log", ".json", ".yaml", ".yml" };
    public static bool IsText(string path) => text.Contains(Path.GetExtension(path));
    static readonly Dictionary<string, string> office = new(StringComparer.OrdinalIgnoreCase) {
        [".docx"] = "word/document.xml", [".xlsx"] = "xl/workbook.xml", [".pptx"] = "ppt/presentation.xml"
    };
    public static bool IsOffice(string path) => office.ContainsKey(Path.GetExtension(path));
    public static string OfficeContentEntry(string path) => office.TryGetValue(Path.GetExtension(path), out string? entry)
        ? entry : throw new IOException("不支持这个文档格式。");
    public static bool Supported(string path) => IsText(path) || IsOffice(path) || Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
}
public sealed partial class WorkspaceWindow
{
    readonly List<EditorDocumentView> editors = new();
    EditorDocumentView? activeEditor;
    async Task<EditorDocumentView?> OpenEditor(string path)
    {
        if (closingBusy || closingApproved) return null;
        path = Path.GetFullPath(path);
        var existing = editors.FirstOrDefault(v => v.FilePath.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { SelectEditor(existing); return existing; }
        if (!File.Exists(path)) { ShowHome(); homeStatus.Text = "找不到这个文件，可能已移动或删除。"; return null; }
        EditorDocumentView view = DocumentFormats.IsOffice(path) ? new OfficeDocumentView(this, path) : new TextDocumentView(this, path);
        view.Changed = () => EditorChanged(view);
        view.SelectRequested = () => SelectEditor(view);
        view.WorkspaceCommandRequested = command => { _ = RunEditorCommand(view, command); };
        editors.Add(view); readerSurface.Children.Add(view); SelectEditor(view);
        recent.Record(new() { Path = path }, true); RefreshRecent();
        await view.Ready; if (!editors.Contains(view)) return null;
        EditorChanged(view); return view;
    }
    async Task RunEditorCommand(EditorDocumentView view, string command)
    {
        if (activeEditor != view || closingBusy || closingApproved) return;
        try {
            switch (command) {
                case "TAB_NEXT": CycleDocuments(false); break;
                case "TAB_PREVIOUS": CycleDocuments(true); break;
                case "CLOSE_TAB": await CloseEditor(view); break;
                case "OPEN_FILES": await OpenFilesDialog(); break;
                case "HOME": ShowHome(); break;
                case "CLOSE_WORKSPACE": Close(); break;
            }
        } catch (Exception error) { MessageBox.Show(this, error.Message, "FullOffice", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    void EditorChanged(EditorDocumentView view)
    {
        if (!editors.Contains(view)) return;
        if (activeEditor == view) { saveButton.IsEnabled = view.CanSave; Title = Path.GetFileName(view.FilePath) + " · FullOffice"; }
        RefreshTabs();
    }
    void DeactivateEditors()
    {
        foreach (var view in editors) { view.Deactivate(); view.Visibility = Visibility.Collapsed; }
        activeEditor = null;
    }
    void SelectEditor(EditorDocumentView view)
    {
        if (activeEditor == view && homeSurface.Visibility != Visibility.Visible) return;
        if (active != null) { Remember(active); active.DeactivateReader(); active = null; }
        foreach (var pdf in readers) pdf.Visibility = Visibility.Collapsed;
        DeactivateEditors(); activeEditor = view; view.Visibility = Visibility.Visible;
        homeSurface.Visibility = Visibility.Collapsed;
        saveButton.IsEnabled = view.CanSave;
        Title = Path.GetFileName(view.FilePath) + " · FullOffice"; RefreshTabs(); view.Activate();
    }
    async Task<bool> CloseEditor(EditorDocumentView view)
    {
        if (!editors.Contains(view) || !await view.ConfirmClose()) return false;
        await view.CloseAsync();
        int index = editors.IndexOf(view); bool selected = activeEditor == view;
        closedFiles.Push(view.FilePath); editors.Remove(view); readerSurface.Children.Remove(view); view.Dispose();
        if (selected) { activeEditor = null; if (editors.Count > 0) SelectEditor(editors[Math.Clamp(index - 1, 0, editors.Count - 1)]); else if (readers.Count > 0) SelectReader(readers[^1]); else ShowHome(); }
        RefreshTabs(); return true;
    }
    void AppendEditorTabs()
    {
        foreach (var view in editors) {
            var tab = new Border { CornerRadius = new(10, 10, 0, 0), Background = activeEditor == view ? Brushes.White : Brushes.Transparent,
                MinWidth = 128, MaxWidth = 236, Margin = new(0, 0, 4, 0), Padding = new(7, 0, 2, 0), ToolTip = view.FilePath };
            var row = new DockPanel(); tab.Child = row;
            var close = ActionButton("×", async () => await CloseEditor(view)); close.ToolTip = "关闭标签页 · Ctrl+W";
            DockPanel.SetDock(close, Dock.Right); row.Children.Add(close);
            row.Children.Add(new TextBlock { Text = (view.IsModified ? "●  " : "") + Path.GetFileNameWithoutExtension(view.FilePath),
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(8, 0, 7, 0) });
            tab.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not Button) SelectEditor(view); };
            tab.MouseDown += async (_, e) => { if (e.ChangedButton == MouseButton.Middle) { e.Handled = true; await CloseEditor(view); } };
            var menu = new ContextMenu(); var item = new MenuItem { Header = "关闭标签页" }; item.Click += async (_, _) => await CloseEditor(view); menu.Items.Add(item); tab.ContextMenu = menu;
            tabStrip.Children.Add(tab);
        }
    }
    async Task HandleEditorShortcut(KeyEventArgs e)
    {
        if (activeEditor == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.S) { e.Handled = true; if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) await activeEditor.ExportAsync(); else await activeEditor.SaveAsync(); }
        else if (e.Key == Key.W) { e.Handled = true; await CloseEditor(activeEditor); }
    }
    void SelectDocumentAt(int index)
    {
        if (index < 0) return;
        if (index < readers.Count) SelectReader(readers[index]);
        else if (index - readers.Count < editors.Count) SelectEditor(editors[index - readers.Count]);
    }
    void CycleDocuments(bool backwards)
    {
        int count = readers.Count + editors.Count; if (count == 0) return;
        int current = active != null ? readers.IndexOf(active) : activeEditor != null ? readers.Count + editors.IndexOf(activeEditor) : -1;
        SelectDocumentAt(current < 0 ? backwards ? count - 1 : 0 : (current + (backwards ? -1 : 1) + count) % count);
    }
}
