using System.Windows;
using System.Windows.Controls;

namespace FlowOfficeLab;

public abstract class EditorDocumentView : UserControl, IDisposable
{
    protected readonly WorkspaceWindow Owner;
    protected readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string FilePath { get; protected set; }
    public Task Ready => ready.Task;
    public bool IsModified { get; protected set; }
    public bool IsSaving { get; protected set; }
    public virtual bool CanSave => Ready.IsCompleted && !IsSaving;
    public Action? Changed { get; set; }
    public Action? SelectRequested { get; set; }
    public Action<string>? WorkspaceCommandRequested { get; set; }
    protected EditorDocumentView(WorkspaceWindow owner, string path) { Owner = owner; FilePath = Path.GetFullPath(path); }
    public abstract Task<bool> SaveAsync();
    public abstract Task ExportAsync();
    public virtual void Activate() { }
    public virtual void Deactivate() { }
    public async Task<bool> ConfirmClose()
    {
        if (IsSaving) return false;
        if (!IsModified) return true;
        SelectRequested?.Invoke();
        var answer = MessageBox.Show(Owner, $"“{Path.GetFileName(FilePath)}”还有未保存的修改。\n选择“是”保存，“否”放弃修改，或“取消”继续编辑。",
            "关闭标签页", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && await SaveAsync();
    }
    public abstract void Dispose();
}
