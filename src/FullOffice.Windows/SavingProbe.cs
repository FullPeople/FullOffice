using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FlowOfficeLab;

public sealed partial class WorkspaceWindow
{
    async Task SavingProbe(string fixture,string directory){
        Directory.CreateDirectory(directory);var checks=new Dictionary<string,bool>();var original=Program.HashFile(fixture);
        try{
            string current=Path.Combine(directory,"当前文件.pdf");File.Copy(fixture,current,true);var initial=Program.HashFile(current);var siblings=Directory.GetFiles(directory,"*.pdf").Select(Path.GetFileName).Order().ToArray();
            var reader=await OpenDocument(current)??throw new IOException("Save fixture did not open.");await reader.PrepareSavingProbe();
            foreach(var pair in await reader.TagInteractionProbe(directory,CaptureWorkspace))checks[pair.Key]=pair.Value;
            UiInputProbe.Click(this,saveButton);await reader.WaitSavingProbe();checks["HeaderSaveWritesCurrentFileWithoutDialog"]=!reader.IsModified&&string.Equals(reader.FilePath,Path.GetFullPath(current),StringComparison.OrdinalIgnoreCase)&&!initial.SequenceEqual(Program.HashFile(current));
            checks["DefaultSaveCreatesNoSiblingPdfCopy"]=siblings.SequenceEqual(Directory.GetFiles(directory,"*.pdf").Select(Path.GetFileName).Order());
            checks["RecoveryBackupContainsPreviousOriginal"]=initial.SequenceEqual(Program.HashFile(reader.BackupProbePath!));
            using(var verify=new PdfEngine()){var info=await verify.OpenAsync(current);var metadata=await PdfOutlineStore.ReadMetadataAsync(current,CancellationToken.None);checks["TagsBookmarksNotesAndHoverSavedInOriginal"]=metadata.Tags.Count==3&&info.Bookmarks.Any(b=>b.Title=="保存验证书签")&&(await verify.AnnotationsAsync(0,CancellationToken.None)).Any(a=>a.Title=="原文件批注")&&(await verify.HoverMarksAsync(0,CancellationToken.None)).Any(h=>h.Reference.Title=="原文件悬停标");}
            var previous=await reader.RepeatSavingProbe();checks["RepeatedSaveAndUndoPreserveBookmarkDestinationsAndStyles"]=await reader.VerifySavingBookmarks();checks["LatestBackupTracksPreviousSave"]=previous.SequenceEqual(Program.HashFile(reader.BackupProbePath!));checks["BackupRetentionUsesOnePreviousVersion"]=Directory.GetFiles(Path.GetDirectoryName(reader.BackupProbePath!)!,Path.GetFileName(current)+".*").Length==1;
            var noChanges=Program.HashFile(current);await reader.SaveFromShell();checks["CleanSaveDoesNotRewritePdf"]=noChanges.SequenceEqual(Program.HashFile(current));
            await reader.AddClosingSaveProbe();var confirm=ClickOwnedDialog("关闭标签页",6);bool closed=await CloseReader(reader);checks["ClosePdfSaveWritesSameFileAndClosesTab"]=await confirm&&closed&&!readers.Contains(reader);var savedMetadata=await PdfOutlineStore.ReadMetadataAsync(current,CancellationToken.None);checks["ClosePdfSavePersistsLatestTag"]=savedMetadata.Tags.Any(t=>t.Title=="关闭时保存");
            string rejected=Path.Combine(directory,"readonly-close.pdf");File.Copy(fixture,rejected,true);var rejectedView=await OpenDocument(rejected)??throw new IOException("Read-only close fixture did not open.");await rejectedView.AddClosingSaveProbe();File.SetAttributes(rejected,File.GetAttributes(rejected)|FileAttributes.ReadOnly);
            try{var chooseSave=ClickOwnedDialog("关闭标签页",6);var acknowledge=ClickOwnedDialog("保存失败",2);bool rejectedClose=await CloseReader(rejectedView);checks["FailedSaveDuringCloseKeepsDocumentAndEdits"]=await chooseSave&&await acknowledge&&!rejectedClose&&readers.Contains(rejectedView)&&rejectedView.IsModified;}finally{File.SetAttributes(rejected,File.GetAttributes(rejected)&~FileAttributes.ReadOnly);}
            await rejectedView.SaveFromShell();await CloseReader(rejectedView);
            foreach(var pair in await SaveFailureChecks(fixture,directory))checks[pair.Key]=pair.Value;
            checks["ProtectedInputFixtureUnchanged"]=original.SequenceEqual(Program.HashFile(fixture));
            File.WriteAllText(Path.Combine(directory,"saving-probe.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,Scope="Actual WPF button and close-confirmation routing; native in-place save/reopen, backup, undo and controlled failure cases. Animation frames sampled from WPF without moving the physical mouse. Not physical input latency or sustained FPS acceptance."},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"saving-probe.json"),JsonSerializer.Serialize(new{Error=ex.ToString(),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));}
        finally{closingApproved=true;Close();}
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool PostMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    static Task<bool> ClickOwnedDialog(string title,int buttonId)=>Task.Run(()=>{var timer=Stopwatch.StartNew();while(timer.ElapsedMilliseconds<10000){var dialog=FindWindow("#32770",title);if(dialog!=IntPtr.Zero){GetWindowThreadProcessId(dialog,out uint pid);if(pid==Environment.ProcessId){var button=GetDlgItem(dialog,buttonId);if(button==IntPtr.Zero&&title=="保存失败")button=GetDlgItem(dialog,1);if(button!=IntPtr.Zero)return PostMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);}}Thread.Sleep(15);}return false;});
    static async Task<Dictionary<string,bool>> SaveFailureChecks(string fixture,string directory){
        var checks=new Dictionary<string,bool>();string locked=Path.Combine(directory,"locked.pdf");File.Copy(fixture,locked,true);var original=Program.HashFile(locked);
        using(var pdf=new PdfEngine()){
            await pdf.OpenAsync(locked);await pdf.AnnotateAsync(0,new(.2,.2),new(.2,.2),false,"Pending after failure");
            using(var blocker=new FileStream(locked,FileMode.Open,FileAccess.Read,FileShare.Read)){
                bool failed=false;try{await pdf.SaveInPlaceAsync();}catch(IOException){failed=true;}catch(UnauthorizedAccessException){failed=true;}
                checks["LockedFileFailureLeavesOriginalUnchanged"]=failed&&original.SequenceEqual(Program.HashFile(locked));checks["LockedFileFailureKeepsUnsavedEdits"]=pdf.Modified&&(await pdf.AnnotationsAsync(0,CancellationToken.None)).Any(a=>a.Title=="Pending after failure");checks["DocumentStillRendersAfterFailedReplacement"]=(await pdf.RenderAsync(0,500,CancellationToken.None)).Width==500;
            }
            await pdf.SaveInPlaceAsync();checks["RetryAfterUnlockSavesOriginalSuccessfully"]=!pdf.Modified&&!original.SequenceEqual(Program.HashFile(locked));
        }
        string readOnly=Path.Combine(directory,"readonly.pdf");File.Copy(fixture,readOnly,true);original=Program.HashFile(readOnly);
        using(var pdf=new PdfEngine()){
            await pdf.OpenAsync(readOnly);await pdf.AnnotateAsync(0,new(.2,.2),new(.2,.2),false,"Read-only pending");File.SetAttributes(readOnly,File.GetAttributes(readOnly)|FileAttributes.ReadOnly);
            try{bool failed=false;try{await pdf.SaveInPlaceAsync();}catch(IOException){failed=true;}checks["ReadOnlySaveFailureKeepsFileAndEdits"]=failed&&pdf.Modified&&original.SequenceEqual(Program.HashFile(readOnly));}finally{File.SetAttributes(readOnly,File.GetAttributes(readOnly)&~FileAttributes.ReadOnly);}
        }
        string changed=Path.Combine(directory,"external.pdf");File.Copy(fixture,changed,true);original=Program.HashFile(changed);
        using(var pdf=new PdfEngine()){
            await pdf.OpenAsync(changed);await pdf.AnnotateAsync(0,new(.2,.2),new(.2,.2),false,"External pending");File.SetLastWriteTimeUtc(changed,File.GetLastWriteTimeUtc(changed).AddSeconds(3));bool failed=false;try{await pdf.SaveInPlaceAsync();}catch(IOException){failed=true;}checks["ExternalChangeIsNotOverwritten"]=failed&&pdf.Modified&&original.SequenceEqual(Program.HashFile(changed));string export=Path.Combine(directory,"explicit-export.pdf");await pdf.SaveCopyAsync(export);checks["ExplicitExportDoesNotMarkOriginalSaved"]=pdf.Modified&&File.Exists(export)&&original.SequenceEqual(Program.HashFile(changed));
        }
        checks["TemporaryFilesCleanedAfterSaveFailures"]=Directory.GetFiles(directory,"*.tmp*").Length==0;return checks;
    }
}

public sealed partial class PdfReaderView
{
    internal string? BackupProbePath=>engine.LastBackupPath;
    internal async Task PrepareSavingProbe(){
        await bookmarkAppearanceTask;ChangeBookmarks(()=>{if(bookmarks.Count>0)bookmarks.RemoveAt(0);bookmarks.Add(new Bookmark("保存验证书签",2,0){ColorHex="#297E8F",Bold=true});});
        pageTags.AddRange(new[]{new PageTag(Guid.NewGuid(),"第一章",0,"#8151C8",.1),new PageTag(Guid.NewGuid(),"魔法物品",1,"#297E8F",.2),new PageTag(Guid.NewGuid(),"怪物数据",2,"#A3503F",.3)});TagsChanged();await AddAnnotation(new(.2,.25),new(.2,.25),false,"原文件批注");await engine.AddHoverAsync(0,new(.7,.2),new HoverReference(Guid.NewGuid(),"原文件悬停标",2,.1,.1,.5,.3));await SetFit(false);await ShowPage(0);
    }
    internal async Task<Dictionary<string,bool>> TagInteractionProbe(string directory,Action<string> capture){
        var checks=new Dictionary<string,bool>();var tag=pageTags.First(t=>t.Page==1);var button=(ColorTagButton)stickyButtons[tag.Id];double start=button.Width;capture(Path.Combine(directory,"motion-00.png"));
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseEnterEvent});double instant=button.Width;
        await Task.Delay(35);await RenderCycle();double middle=button.Width;capture(Path.Combine(directory,"motion-01.png"));
        await Task.Delay(190);await RenderCycle();File.WriteAllText(Path.Combine(directory,"tag-motion-values.json"),JsonSerializer.Serialize(new{Enabled=SystemParameters.ClientAreaAnimation,Start=start,Instant=instant,Middle=middle,Finish=button.Width}));checks["HoverTitleExpandsSmoothly"]=SystemParameters.ClientAreaAnimation?start<120&&middle>start&&middle<120&&Math.Abs(button.Width-120)<.1:button.Width==120;capture(Path.Combine(directory,"motion-02.png"));
        checks["BothTagLocationsHaveDraggingDisabled"]=stickyButtons.Values.Concat(tagRows.Children.OfType<Button>()).All(b=>b is ColorTagButton&&!b.AllowDrop)&&typeof(PdfReaderView).Assembly.GetType("FlowOfficeLab.PageTagDrag")==null;
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseDownEvent});await Task.Delay(45);await RenderCycle();checks["PressedStateShrinksTag"]=!SystemParameters.ClientAreaAnimation||button.InteractiveScale.ScaleX<1;capture(Path.Combine(directory,"motion-03.png"));
        button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left){RoutedEvent=Mouse.PreviewMouseUpEvent});UiInputProbe.Click(this,button);await ProbeWait(()=>currentPage==1&&displayedPage==1&&loadingBadge.Visibility!=Visibility.Visible);await Task.Delay(220);await RenderCycle();checks["ClickJumpsAndShowsSelectedState"]=button.Selected&&button.BorderThickness.Left>1&&currentPage==1;capture(Path.Combine(directory,"motion-04.png"));
        button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,Environment.TickCount){RoutedEvent=Mouse.MouseLeaveEvent});await Task.Delay(220);await RenderCycle();capture(Path.Combine(directory,"motion-05.png"));return checks;
    }
    internal Task WaitSavingProbe()=>ProbeWait(()=>!saving&&!engine.Modified);
    internal async Task<byte[]> RepeatSavingProbe(){var target=bookmarks.First(b=>b.Title=="保存验证书签");ChangeBookmarks(()=>target.Title="保存验证书签二");BuildMainMenu();UiInputProbe.Click(this,mainMenu.Items.OfType<MenuItem>().Single(i=>i.Header?.ToString()=="保存"));await WaitSavingProbe();var previous=Program.HashFile(currentFile);UndoBookmarks(false);await SaveCurrent();return previous;}
    internal async Task<bool> VerifySavingBookmarks(){using var pdf=new PdfEngine();var info=await pdf.OpenAsync(currentFile);var styles=await PdfOutlineStore.ReadMetadataAsync(currentFile,CancellationToken.None);var b=info.Bookmarks.SingleOrDefault(b=>b.Title=="保存验证书签");return b!=null&&b.Page==2&&info.Bookmarks.Any(b=>b.Title=="Fixture page 2"&&b.Page==1)&&styles.Outlines[b.SourceIndex].Color=="#297E8F"&&styles.Outlines[b.SourceIndex].Bold;}
    internal async Task AddClosingSaveProbe(){await bookmarkAppearanceTask;pageTags.Add(new(Guid.NewGuid(),"关闭时保存",currentPage,"#D94262",.4));TagsChanged();await ShowPage(currentPage,false,true,true);}
}
