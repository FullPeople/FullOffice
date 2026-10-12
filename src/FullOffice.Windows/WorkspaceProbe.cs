using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowOfficeLab;

public sealed partial class WorkspaceWindow
{
    async Task WorkspaceProbe(string fixture,string directory){
        Directory.CreateDirectory(directory);var checks=new Dictionary<string,bool>();byte[] before=Program.HashFile(fixture);var previousClipboard=Clipboard.GetDataObject();
        try{
            checks["HomeVisibleBeforeOpening"]=homeSurface.Visibility==Visibility.Visible&&active==null;
            checks["WindowIconAssigned"]=Icon!=null;
            string second=Path.Combine(directory,"second.pdf");File.Copy(fixture,second,true);
            var first=await OpenDocument(fixture)??throw new IOException("First document did not open.");await first.PrepareWorkspaceProbe(1,1.4,false,true);
            var a=first.ProbeSnapshot();var pending=OpenDocument(second);var duplicate=OpenDocument(second);var secondView=await pending??throw new IOException("Second document did not open.");checks["OpeningPendingFileReusesTab"]=ReferenceEquals(secondView,await duplicate)&&readers.Count==2;await secondView.PrepareWorkspaceProbe(2,.65,true,false);var b=secondView.ProbeSnapshot();
            checks["TwoIndependentTabs"]=readers.Count==2&&first!=secondView&&first.FilePath!=secondView.FilePath;
            checks["EditsRemainInFirstTab"]=first.IsModified&&!secondView.IsModified;
            var cancelTask=Task.Run(()=>{var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<5000){var dialog=FindWindow("#32770","关闭标签页");if(dialog!=IntPtr.Zero){GetWindowThreadProcessId(dialog,out uint pid);if(pid==Environment.ProcessId){var button=GetDlgItem(dialog,2);if(button!=IntPtr.Zero){SendMessage(button,0x00F5,IntPtr.Zero,IntPtr.Zero);return true;}}}Thread.Sleep(15);}return false;});
            bool closed=await CloseReader(first);checks["CancelUnsavedCloseKeepsTabAndEdits"]=await cancelTask&&!closed&&readers.Contains(first)&&first.IsModified;
            SelectReader(first);await first.ActivateReader();var restored=first.ProbeSnapshot();checks["FirstTabPageZoomSelectionRestored"]=a==restored;
            SelectReader(secondView);await secondView.ActivateReader();checks["SecondTabPageZoomSelectionRestored"]=b==secondView.ProbeSnapshot();
            await OpenDocument(fixture);checks["OpeningSameFileReusesTab"]=readers.Count==2&&active==first;
            var radial=await first.ProbeRadial(directory);foreach(var check in radial)checks[check.Key]=check.Value;
            string saved=Path.Combine(directory,"tab-edited-copy.pdf");await first.SaveProbeCopy(saved);checks["CopyExportKeepsOriginalUnsavedAndOtherTabClean"]=first.IsModified&&!secondView.IsModified;
            var last=first.ReadingState();ShowHome();checks["HomeKeepsOpenTabs"]=readers.Count==2&&active==null;
            var restoredStore=new RecentStore(Path.Combine(directory,"isolated-profile"));var remembered=restoredStore.Find(first.FilePath);checks["RecentHistoryWrittenWithReadingState"]=remembered!=null&&remembered.Page==last.Page&&Math.Abs(remembered.Zoom-last.Zoom)<.001;
            int recentBefore=recent.Items.Count;recent.Remove(second);checks["RemovingHistoryKeepsDocumentFile"]=File.Exists(second)&&recent.Items.Count==recentBefore-1;
            recent.Record(secondView.ReadingState(),false);await CloseReader(secondView);checks["ClosingTabKeepsOtherTab"]=readers.Count==1&&readers.Contains(first);
            var reopened=await OpenDocument(second);checks["ClosedTabCanReopen"]=reopened!=null&&readers.Count==2;
            // Restore the saved state through the same method used for normal recent-file opening.
            await reopened!.RestoreReading(restoredStore.Find(second)!);checks["RecentFileRestoresPageAndZoom"]=reopened.ReadingPage==b.Page&&Math.Abs(reopened.ReadingState().Zoom-b.Zoom)<.001;
            ShowHome();await Task.Delay(80);CaptureWorkspace(Path.Combine(directory,"home.png"));SelectReader(first);await first.ActivateReader();await first.ShowRadialProbe();CaptureWorkspace(Path.Combine(directory,"radial-menu.png"));first.HideRadialProbe();
            checks["OriginalFixtureUnchanged"]=before.SequenceEqual(Program.HashFile(fixture));using var process=Process.GetCurrentProcess();File.WriteAllText(Path.Combine(directory,"workspace-probe.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,PeakWorkingSetBytes=process.PeakWorkingSet64,Scope="Actual WPF tab views, native PDF documents, recent-file persistence and radial command execution. Direction geometry and Escape routing are exercised; physical mouse drag/release and rendered FPS are not measured."},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"workspace-probe.json"),JsonSerializer.Serialize(new{Error=ex.ToString(),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));}
        finally{if(previousClipboard!=null)try{Clipboard.SetDataObject(previousClipboard,true);}catch{}closingApproved=true;Close();}
    }
    void CaptureWorkspace(string path){var target=(FrameworkElement)Content;target.UpdateLayout();var dpi=VisualTreeHelper.GetDpi(target);var image=new RenderTargetBitmap((int)Math.Ceiling(target.ActualWidth*dpi.DpiScaleX),(int)Math.Ceiling(target.ActualHeight*dpi.DpiScaleY),96*dpi.DpiScaleX,96*dpi.DpiScaleY,PixelFormats.Pbgra32);image.Render(target);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));using var output=File.Create(path);png.Save(output);}
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string className,string title);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] static extern IntPtr GetDlgItem(IntPtr dialog,int id);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
    async Task RealTabsProbe(string listPath,string directory){
        Directory.CreateDirectory(directory);var checks=new Dictionary<string,bool>();var records=new List<object>();
        try{
            var paths=JsonSerializer.Deserialize<string[]>(File.ReadAllText(listPath))!;var hashes=paths.Select(Program.HashFile).ToArray();var clock=Stopwatch.StartNew();
            foreach(var path in paths){var view=await OpenDocument(path,probe:true)??throw new IOException("Could not open "+Path.GetFileName(path));await view.PrepareWorkspaceProbe(Math.Min(11,view.PageTotal-1),1.1,false,false);records.Add(new{File=Path.GetFileName(path),Pages=view.PageTotal,State=view.ProbeSnapshot()});}
            checks["ThreeRealFilesOpenInIndependentTabs"]=readers.Count==paths.Length&&readers.Count==3;
            var expected=readers.ToDictionary(r=>r,r=>r.ProbeSnapshot());foreach(var view in readers){SelectReader(view);await view.ActivateReader();checks["Restored-"+Path.GetFileName(view.FilePath)]=view.ProbeSnapshot()==expected[view];}
            SelectReader(readers[0]);await readers[0].ActivateReader();await readers[0].RunControlProbe(directory);checks["AllOriginalFilesUnchanged"]=paths.Select((p,i)=>hashes[i].SequenceEqual(Program.HashFile(p))).All(x=>x);
            var controls=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(Path.Combine(directory,"ui-controls-diagnostics.json")))!;checks["RealFileFitQualityWheelAndBookmarks"]=new[]{"fullFits","fullResolution","highQualityScaling","sharperCachePreserved","physicalPixelsMatch","widthFits","pageForward","pageBack","folding","reveal","bookmarkClicked","resizedFits"}.All(k=>controls[k].GetBoolean());
            using var process=Process.GetCurrentProcess();File.WriteAllText(Path.Combine(directory,"tabs-probe.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,Documents=records,ElapsedMs=clock.Elapsed.TotalMilliseconds,PeakWorkingSetBytes=process.PeakWorkingSet64,Scope="Three user PDFs opened together read-only, tab state restored, first document fit/quality/wheel/bookmark controls invoked through WPF accessibility. No physical mouse or compositor FPS acceptance."},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"tabs-probe.json"),JsonSerializer.Serialize(new{Error=ex.ToString(),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));}finally{closingApproved=true;Close();}
    }
}

public sealed partial class PdfReaderView
{
    internal record WorkspaceSnapshot(int Page,double Zoom,bool Fit,bool Wheel,bool Selected,int Bookmarks);
    internal WorkspaceSnapshot ProbeSnapshot()=>new(currentPage,zoom,fitWholePage,wheelPageMode,copyRegion!=null,bookmarks.Count);
    internal async Task PrepareWorkspaceProbe(int page,double scale,bool fit,bool edit){await bookmarkAppearanceTask;zoom=scale;fitWholePage=fit;await ShowPage(Math.Min(page,engine.PageCount-1));SmoothScroll.Set(scroll,scroll.ScrollableHeight*.3);SetCopySelection(new(.1,.1),new(.4,.3));if(edit)ChangeBookmarks(()=>bookmarks.Add(new Bookmark("Tab-only edit",currentPage,0)));}
    internal Task SaveProbeCopy(string path)=>engine.SaveCopyAsync(path);
    internal async Task RunControlProbe(string directory)=>_ = await ProbeControls(directory);
    internal void HideRadialProbe()=>CloseRadial();
    internal async Task ShowRadialProbe(){BeginRadial(new(ActualWidth*.63,ActualHeight*.47),(FrameworkElement)Content);radialSurface.Select(1);await Task.Delay(110);}
    internal async Task<Dictionary<string,bool>> ProbeRadial(string directory){
        await ActivateReader();await bookmarkAppearanceTask;var result=new Dictionary<string,bool>();
        result["TwoRadialActionsOnly"]=RadialActions().Count==2;
        result["RadialLeftIsAnnotation"]=RadialMenuSurface.Direction(new(-70,0))==0;
        result["RadialRightIsTag"]=RadialMenuSurface.Direction(new(70,0))==1;
        result["RadialDeadZoneCancels"]=RadialMenuSurface.Direction(new(20,10))==-1;
        StartRightGesture(new(250,200));MoveRightGesture(new(260,205));result["NoWheelInsideDeadZone"]=radialOverlay.Visibility!=Visibility.Visible;await FinishRightGesture(new(260,205));result["OrdinaryRightClickOpensMenu"]=mainMenu.IsOpen;mainMenu.IsOpen=false;
        StartRightGesture(new(250,200));MoveRightGesture(new(320,200));result["DragOutsideDeadZoneOpensWheel"]=radialOverlay.Visibility==Visibility.Visible;MoveRightGesture(new(252,202));await FinishRightGesture(new(252,202));result["ReturningToDeadZoneCancelsWithoutMenu"]=!mainMenu.IsOpen&&radialOverlay.Visibility!=Visibility.Visible;
        BeginRadial(new(2,2),(FrameworkElement)Content);result["RadialFitsNearWindowEdge"]=System.Windows.Controls.Canvas.GetLeft(radialSurface)>=0&&System.Windows.Controls.Canvas.GetTop(radialSurface)>=0;
        var source=PresentationSource.FromVisual(Host)??throw new IOException("No keyboard source.");RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,0,Key.Escape){RoutedEvent=Keyboard.PreviewKeyDownEvent});result["EscapeClosesRadial"]=radialOverlay.Visibility==Visibility.Collapsed;
        return result;
    }
}
