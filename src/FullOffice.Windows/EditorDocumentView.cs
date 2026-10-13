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
    bool isSaving;
    TaskCompletionSource? saveCompleted;
    public bool IsSaving {
        get => isSaving;
        protected set {
            if (isSaving == value) return;
            isSaving = value;
            if (value) saveCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else { saveCompleted?.TrySetResult(); saveCompleted = null; }
        }
    }
    public virtual bool CanSave => Ready.IsCompleted && !IsSaving;
    public Action? Changed { get; set; }
    public Action? SelectRequested { get; set; }
    public Action<string>? WorkspaceCommandRequested { get; set; }
    protected EditorDocumentView(WorkspaceWindow owner, string path) { Owner = owner; FilePath = Path.GetFullPath(path); }
    public abstract Task<bool> SaveAsync();
    public abstract Task ExportAsync();
    public virtual void Activate() { }
    public virtual void Deactivate() { }
    protected virtual void DiscardChanges() { }
    public async Task<bool> ConfirmClose()
    {
        while (IsSaving) await (saveCompleted?.Task ?? Task.CompletedTask);
        if (!IsModified) return true;
        SelectRequested?.Invoke();
        var answer = MessageBox.Show(Owner, $"“{Path.GetFileName(FilePath)}”还有未保存的修改。\n选择“是”保存，“否”放弃修改，或“取消”继续编辑。",
            "关闭标签页", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.No) { DiscardChanges(); return true; }
        return answer == MessageBoxResult.Yes && await SaveAsync();
    }
    public abstract void Dispose();
    public virtual Task CloseAsync() { Dispose(); return Task.CompletedTask; }
}
