using System.Windows;
using System.Windows.Threading;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    public WorkspaceWindow Host {get;}
    readonly TaskCompletionSource ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
    bool initialized,disposed;
    bool isActiveView=true;
    double? resumedOffset;
    void HostDpiChanged(object? sender,DpiChangedEventArgs e){if(isActiveView&&!disposed&&currentFile!=""&&!opening){resizeTimer.Stop();resizeTimer.Start();}}
    public Task Ready=>ready.Task;
    public string FilePath=>currentFile;
    public string RequestedFilePath=>currentFile==""?startingFile??"":currentFile;
    public bool IsModified=>engine.Modified;
    public bool IsSaving=>saving;
    public int ReadingPage=>currentPage;
    public int PageTotal=>engine.PageCount;
    public string Title {get=>Host.Title;set{if(isActiveView)Host.Title=value;}}
    public async Task ActivateReader(){isActiveView=true;await Dispatcher.Yield(DispatcherPriority.Loaded);if(!initialized||disposed||!isActiveView)return;if(currentFile!=""){await ShowPage(currentPage,false,true,true);if(isActiveView&&resumedOffset is double offset){scroll.UpdateLayout();SmoothScroll.Set(scroll,offset*pageImage.Height);resumedOffset=null;}}}
    public void DeactivateReader(){isActiveView=false;if(dragging){dragging=false;pageImage.ReleaseMouseCapture();}renderCancellation.Cancel();searchCancellation.Cancel();resizeTimer.Stop();CloseHoverPopup();CloseRadial();}
    public RecentDocument ReadingState()=>new(){Path=currentFile,Page=currentPage,Zoom=zoom,FitWholePage=fitWholePage,WheelPageMode=wheelPageMode,SidebarCollapsed=sidebarCollapsed,Offset=pageImage.Height>0?scroll.VerticalOffset/pageImage.Height:0};
    public async Task RestoreReading(RecentDocument entry){if(currentFile==""||disposed)return;SetSidebarCollapsed(entry.SidebarCollapsed);fitWholePage=entry.FitWholePage;wheelPageMode=entry.WheelPageMode;zoom=Math.Clamp(entry.Zoom,.4,3);currentPage=Math.Clamp(entry.Page,0,engine.PageCount-1);resumedOffset=Math.Clamp(entry.Offset,0,1);UpdateViewButtons();if(!isActiveView)return;await ShowPage(currentPage,false,true,true);if(isActiveView&&resumedOffset is double offset){scroll.UpdateLayout();SmoothScroll.Set(scroll,offset*pageImage.Height);resumedOffset=null;}}
    public Task SaveFromShell()=>SaveCurrent();
    public Task ExportCopyFromShell()=>SaveCopy();
    public async Task<bool> ConfirmClose(){
        if(saving){status.Text="正在保存，请稍候再关闭";return false;}
        if(!engine.Modified||featureProbeDirectory!=null||smokeDirectory!=null)return true;
        Host.SelectReader(this);
        var result=MessageBox.Show(Host,$"“{Path.GetFileName(currentFile)}”还有未保存的修改。\n选择“是”保存到当前 PDF，“否”放弃修改，或“取消”继续阅读。","关闭标签页",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
        if(result==MessageBoxResult.Cancel)return false;if(result==MessageBoxResult.No)return true;
        return await SaveCurrent();
    }
    public void DisposeReader(){if(disposed)return;disposed=true;ready.TrySetResult();isActiveView=false;Host.DpiChanged-=HostDpiChanged;Host.Deactivated-=HostDeactivated;resizeTimer.Stop();CloseRadial();CloseHoverPopup();foreach(var pin in pins.ToArray())pin.Close();documentCancellation.Cancel();renderCancellation.Cancel();searchCancellation.Cancel();engine.Dispose();pageImage.Source=null;}
    void Close()=>Host.Close();
}
