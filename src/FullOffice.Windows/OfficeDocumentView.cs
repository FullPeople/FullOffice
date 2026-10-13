using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Xml;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed class OfficeDocumentView : EditorDocumentView
{
    const string CoreSHA256 = "3D00C5773094FFEC137A7B15E862319F211C952786AB7AAAC1C3F2A6AB3D3415";
    static readonly ConcurrentDictionary<string, Lazy<Task>> verifiedRuntimes = new(StringComparer.OrdinalIgnoreCase);
    readonly TextBlock status = new() { Text = "正在打开文档…", Margin = new(14, 7, 14, 7), Foreground = Brushes.SlateGray };
    readonly CancellationTokenSource lifetime = new();
    readonly SemaphoreSlim saveGate = new(1, 1);
    readonly Grid surface = new();
    NativeOfficeHost? host;
    Process? process;
    nint editorWindow;
    TaskCompletionSource<bool>? nativeSave;
    byte[] originalHash = Array.Empty<byte>();
    long originalLength;
    DateTime originalWrite;
    string session = "", workingFile = "";
    readonly OfficeRecoveryStore recovery = new();
    FileStream? sessionLease;
    bool discarded, retireOnClose;
    Task closeTask = Task.CompletedTask;
    bool started, disposed, nativeDirty;
    int revision;
    public override bool CanSave => base.CanSave && !disposed && editorWindow != 0 && process is { HasExited: false };
    public OfficeDocumentView(WorkspaceWindow owner, string path) : base(owner, path)
    {
        var root = new DockPanel(); Content = root;
        var footer = new DockPanel(); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var credits = new Button { Content = OfficeLicenses.OriginalLogo(15), ToolTip = "文档、表格与演示文稿编辑器及许可证", Background = Brushes.Transparent,
            BorderThickness = new(0), Padding = new(1), Margin = new(8, 0, 8, 0) };
        credits.Click += (_, _) => OfficeLicenses.Show(Owner); DockPanel.SetDock(credits, Dock.Right); footer.Children.Add(credits);
        footer.Children.Add(status); root.Children.Add(surface);
        Loaded += async (_, _) => { if (!started && !disposed) { started = true; await OpenNative(); } };
    }
    async Task OpenNative()
    {
        try {
            string runtime = Environment.GetEnvironmentVariable("FULLOFFICE_ENGINE_RUNTIME") ?? Path.Combine(AppContext.BaseDirectory, "office-engine");
            string engine = Environment.GetEnvironmentVariable("FULLOFFICE_ENGINE_ROOT") ?? runtime;
            string executable = Path.Combine(runtime, "FullOfficeOfficeHost.exe");
            if (!File.Exists(executable) || !File.Exists(Path.Combine(engine, "ascdocumentscore.dll")))
                throw new IOException("缺少文档编辑组件，请安装包含文档和表格编辑器的完整版本。");
            await verifiedRuntimes.GetOrAdd(Path.GetFullPath(engine), path => new Lazy<Task>(() => Task.Run(() => {
                if (!Convert.ToHexString(Program.HashFile(Path.Combine(path, "ascdocumentscore.dll"))).Equals(CoreSHA256, StringComparison.Ordinal))
                    throw new IOException("文档编辑组件版本不匹配，请重新安装完整版本。");
            }))).Value;
            var current = new FileInfo(FilePath); originalLength = current.Length; originalWrite = current.LastWriteTimeUtc;
            originalHash = await Task.Run(() => Program.HashFile(FilePath), lifetime.Token);
            string sessions = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FullOffice", "EditingSessions");
            session = Path.Combine(sessions, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(session, "document"));
            sessionLease = recovery.Acquire(session);
            workingFile = Path.Combine(session, "document", Path.GetFileName(FilePath));
            await CopyFileAsync(FilePath, workingFile);
            if (!originalHash.SequenceEqual(await Task.Run(() => Program.HashFile(workingFile), lifetime.Token)))
                throw new IOException("文件在打开过程中发生了变化，请重新打开。");
            File.SetAttributes(workingFile, FileAttributes.Normal);
            await recovery.InitializeAsync(session, FilePath, 0, 0);
            host = new NativeOfficeHost(); surface.Children.Add(host);
            await host.HandleReady;
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = runtime };
            foreach (string value in new[] { engine, Path.Combine(session, "profile"), workingFile, host.NativeHandle.ToInt64().ToString() }) start.ArgumentList.Add(value);
            start.Environment["PATH"] = runtime + Path.PathSeparator + engine + Path.PathSeparator + Path.Combine(engine, "converter") + Path.PathSeparator + (start.Environment["PATH"] ?? "");
            process = Process.Start(start) ?? throw new IOException("无法启动文档编辑器。");
            await recovery.InitializeAsync(session, FilePath, process.Id, process.StartTime.ToUniversalTime().Ticks);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => Dispatcher.BeginInvoke(() => { if (!disposed) { ready.TrySetResult(); nativeSave?.TrySetResult(false); status.Text = "编辑器已退出。当前会话文件已保留。"; Changed?.Invoke(); } });
            _ = ReadOutput(process.StandardOutput);
            _ = ReadErrors(process.StandardError);
        } catch (Exception error) { status.Text = "打开失败：" + error.Message; ready.TrySetResult(); Changed?.Invoke(); }
    }
    async Task ReadOutput(StreamReader reader)
    {
        try {
            while (!lifetime.IsCancellationRequested && await reader.ReadLineAsync(lifetime.Token) is string line) {
                await Dispatcher.InvokeAsync(async () => {
                    if (disposed) return;
                    if (line.StartsWith("HWND ", StringComparison.Ordinal) && long.TryParse(line.AsSpan(5), out long value)) {
                        editorWindow = new(value); host?.AttachEditor(editorWindow); ready.TrySetResult(); if (IsVisible) Activate(); Changed?.Invoke();
                    } else if (line == "VIEW_LOADED") { status.Text = ""; }
                    else if (line == "DIRTY 1") { nativeDirty = true; IsModified = true; ++revision; status.Text = "尚未保存"; Changed?.Invoke(); }
                    else if (line == "DIRTY 0") { nativeDirty = false; }
                    else if (line == "SAVED OK") {
                        try { await recovery.SnapshotSavedAsync(session, workingFile); }
                        catch (Exception error) {
                            IsModified = true; status.Text = "恢复记录更新失败，临时文件已保留：" + error.Message;
                            nativeSave?.TrySetException(error); Changed?.Invoke(); return;
                        }
                        if (nativeSave != null) nativeSave.TrySetResult(true);
                        else { IsModified = true; await CommitWorkingFile(); }
                    } else if (line == "SAVED CANCEL") { nativeSave?.TrySetResult(false); status.Text = "保存未完成，修改仍保留。"; }
                    else if (line.StartsWith("COMMAND ", StringComparison.Ordinal)) WorkspaceCommandRequested?.Invoke(line[8..]);
                }).Task.Unwrap();
            }
        } catch (OperationCanceledException) { }
        catch (IOException error) { await Dispatcher.InvokeAsync(() => { if (!disposed) status.Text = "编辑器连接中断：" + error.Message; }); }
    }
    async Task ReadErrors(StreamReader reader)
    {
        try { while (!lifetime.IsCancellationRequested && await reader.ReadLineAsync(lifetime.Token) is string line)
            if (line.Contains("FATAL", StringComparison.Ordinal)) await Dispatcher.InvokeAsync(() => { if (!disposed) status.Text = "编辑器运行失败。会话文件已保留。"; }); }
        catch (OperationCanceledException) { } catch (IOException) { }
    }
    async Task<bool> FlushNative()
    {
        if (editorWindow == 0 || process is null || process.HasExited) throw new IOException("文档编辑器未运行，当前会话已保留。");
        if (!nativeDirty && File.Exists(workingFile)) return true;
        nativeSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try {
            if (!PostMessage(editorWindow, 0x8000 + 20, 0, 0)) throw new IOException("无法提交保存请求。");
            return await nativeSave.Task.WaitAsync(TimeSpan.FromSeconds(35), lifetime.Token);
        } finally { nativeSave = null; }
    }
    public override async Task<bool> SaveAsync()
    {
        if (disposed || IsSaving || editorWindow == 0) return false;
        IsSaving = true; Changed?.Invoke();
        try { if (!await FlushNative()) return false; return await CommitWorkingFile(true); }
        catch (Exception error) { IsModified = true; status.Text = "保存失败，修改仍保留：" + error.Message; return false; }
        finally { IsSaving = false; Changed?.Invoke(); }
    }
    async Task<bool> CommitWorkingFile(bool alreadySaving = false)
    {
        await saveGate.WaitAsync(lifetime.Token);
        int savedRevision = revision; string? temporary = null;
        if (!alreadySaving) { IsSaving = true; Changed?.Invoke(); }
        try {
            var current = new FileInfo(FilePath);
            await CheckOriginal(current);
            temporary = Path.Combine(current.DirectoryName!, ".FullOffice-office-" + Guid.NewGuid().ToString("N") + ".tmp");
            await CopyFileAsync(workingFile, temporary);
            await Task.Run(() => ValidateDocument(temporary), lifetime.Token);
            await CheckOriginal(current);
            string backups = Path.Combine(current.DirectoryName!, ".FlowOfficeBackups"); Directory.CreateDirectory(backups);
            if ((File.GetAttributes(backups) & FileAttributes.ReparsePoint) != 0) throw new IOException("备份目录是链接，请先选择其他保存位置。");
            File.SetAttributes(backups, File.GetAttributes(backups) | FileAttributes.Hidden);
            File.Replace(temporary, FilePath, Path.Combine(backups, current.Name + ".previous"), true); temporary = null;
            current.Refresh(); originalLength = current.Length; originalWrite = current.LastWriteTimeUtc;
            originalHash = await Task.Run(() => Program.HashFile(FilePath), lifetime.Token);
            IsModified = revision != savedRevision || nativeDirty;
            status.Text = IsModified ? "已保存上一轮修改，当前还有新修改。" : "已保存到原文件";
            return !IsModified;
        } catch (Exception error) { IsModified = true; status.Text = "保存失败，修改仍保留：" + error.Message; return false; }
        finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } }
            saveGate.Release(); if (!alreadySaving) IsSaving = false; Changed?.Invoke(); }
    }
    async Task CheckOriginal(FileInfo current)
    {
        current.Refresh();
        if (!current.Exists || current.Length != originalLength || current.LastWriteTimeUtc != originalWrite ||
            !originalHash.SequenceEqual(await Task.Run(() => Program.HashFile(FilePath), lifetime.Token)))
            throw new IOException("原文件已在其他程序中更改，请保留当前修改并另存为。");
        if (current.IsReadOnly) throw new IOException("原文件为只读，请修改权限或另存为。");
        if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("链接文件请使用另存为。");
    }
    void ValidateDocument(string path) => OfficePackage.Validate(path, FilePath);
    async Task CopyFileAsync(string from, string to)
    {
        await using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        await using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await input.CopyToAsync(output, lifetime.Token); await output.FlushAsync(lifetime.Token);
    }
    public override async Task ExportAsync()
    {
        var dialog = new SaveFileDialog { FileName = Path.GetFileName(FilePath), Filter = "当前格式|*" + Path.GetExtension(FilePath), OverwritePrompt = true };
        if (dialog.ShowDialog(Owner) != true) return;
        if (Path.GetFullPath(dialog.FileName).Equals(FilePath, StringComparison.OrdinalIgnoreCase)) { await SaveAsync(); return; }
        string? temporary = null;
        try { if (!await FlushNative()) return;
            string destination = Path.GetFullPath(dialog.FileName);
            if (File.Exists(destination) && (File.GetAttributes(destination) & (FileAttributes.ReparsePoint | FileAttributes.ReadOnly)) != 0)
                throw new IOException("目标文件是链接或只读文件，请选择其他位置。");
            temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".FullOffice-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            await CopyFileAsync(workingFile, temporary);
            await Task.Run(() => ValidateDocument(temporary), lifetime.Token);
            if (File.Exists(destination)) File.Replace(temporary, destination, null);
            else File.Move(temporary, destination);
            temporary = null; status.Text = "已导出副本";
        } catch (Exception error) { status.Text = "导出失败，修改仍保留：" + error.Message; }
        finally { if (temporary != null) { try { File.Delete(temporary); } catch (IOException) { } } }
    }
    public override void Activate() { host?.Focus(); if (editorWindow != 0) PostMessage(editorWindow, 0x8000 + 22, 0, 0); }
    public override void Deactivate() { if (editorWindow != 0) PostMessage(editorWindow, 0x8000 + 23, 0, 0); }
    public override void Dispose()
    {
        if (disposed) return; retireOnClose = !IsModified || discarded; disposed = true; lifetime.Cancel(); nativeSave?.TrySetCanceled();
        if (editorWindow != 0) PostMessage(editorWindow, 0x0010, 0, 0);
        closeTask = CloseHostAfterExit();
    }
    public override Task CloseAsync() { Dispose(); return closeTask; }
    protected override void DiscardChanges() { discarded = true; }
    async Task CloseHostAfterExit()
    {
        try { if (process != null) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20)); }
        catch (TimeoutException) {
            // The process belongs exclusively to this document view; preserve its recovery files.
            try { process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        finally {
            await Dispatcher.InvokeAsync(() => host?.Dispose());
            sessionLease?.Dispose(); sessionLease = null;
            if (retireOnClose && session.Length > 0) {
                try { recovery.Retire(session); }
                catch (Exception error) { Debug.WriteLine("Session retirement deferred: " + error.Message); }
            }
        }
    }
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)] static extern bool PostMessage(nint hwnd, uint message, nint wparam, nint lparam);
}

sealed class NativeOfficeHost : HwndHost
{
    readonly TaskCompletionSource handleReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    nint window, editor;
    public nint NativeHandle => window;
    public Task HandleReady => handleReady.Task;
    public void AttachEditor(nint value) { editor = value; ResizeEditor(); }
    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        window = CreateWindow(0, "STATIC", "", 0x40000000 | 0x10000000 | 0x02000000, 0, 0, 1, 1, parent.Handle, 0, 0, 0);
        if (window == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        handleReady.TrySetResult(); return new(this, window);
    }
    protected override void DestroyWindowCore(HandleRef handle) { DestroyWindow(handle.Handle); window = 0; }
    protected override void OnWindowPositionChanged(Rect rectangle) { base.OnWindowPositionChanged(rectangle); ResizeEditor(); }
    void ResizeEditor() { if (window != 0 && editor != 0 && GetClientRect(window, out NativeRect bounds)) SetWindowPos(editor, 0, 0, 0, bounds.Right, bounds.Bottom, 0x0014); }
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint="CreateWindowExW", CharSet=CharSet.Unicode, SetLastError=true)] static extern nint CreateWindow(uint ex, string type, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint data);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetClientRect(nint hwnd, out NativeRect bounds);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
