using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    readonly string? featureProbeDirectory;
    static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject{for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T item)yield return item;foreach(var descendant in Descendants<T>(child))yield return descendant;}}
    async Task ProbeDialog(Func<Task> operation,Action<Window> fill){
        var closed=new TaskCompletionSource();Exception? failure=null;var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(40)};int ticks=0;
        timer.Tick+=(_,_)=>{var dialog=Application.Current.Windows.Cast<Window>().FirstOrDefault(w=>w.Owner==Host&&w is not RegionPinWindow);if(dialog==null){if(++ticks>125){timer.Stop();closed.TrySetException(new TimeoutException("The editing dialog did not open."));}return;}timer.Stop();try{fill(dialog);UiInputProbe.Click(dialog,Descendants<Button>(dialog).First(b=>b.IsDefault));closed.TrySetResult();}catch(Exception ex){failure=ex;dialog.Close();closed.TrySetException(ex);}};timer.Start();try{await operation();await closed.Task;if(failure!=null)throw failure;}finally{timer.Stop();}
    }
    async Task InvokeProbeMenu(MenuItem item,Func<bool> completed){UiInputProbe.Click(this,item);var clock=Stopwatch.StartNew();while(!completed()&&clock.ElapsedMilliseconds<4000)await Task.Delay(10);if(!completed())throw new IOException("Menu action did not complete: "+item.Header);}
    async Task ProbeWait(Func<bool> completed){var clock=Stopwatch.StartNew();while(!completed()&&clock.ElapsedMilliseconds<5000)await Task.Delay(10);if(!completed())throw new TimeoutException("The editing action did not settle.");}
    static MenuItem FindMenu(ContextMenu menu,string prefix)=>menu.Items.OfType<MenuItem>().First(i=>i.Header.ToString()!.StartsWith(prefix,StringComparison.Ordinal));
    async Task FeatureProbe(string directory){
        Directory.CreateDirectory(directory);var before=Program.HashFile(currentFile);IDataObject? previousClipboard=null;var checks=new Dictionary<string,bool>();
        try{
            await bookmarkAppearanceTask;await Task.Delay(100);previousClipboard=Clipboard.GetDataObject();int originalCount=bookmarks.Count;
            contextPosition=new(.23,.38);BuildMainMenu();
            await ProbeDialog(async()=>{UiInputProbe.Click(this,FindMenu(mainMenu,"在此处添加书签"));await Task.Delay(100);},dialog=>{foreach(var box in Descendants<TextBox>(dialog)){string id=AutomationProperties.GetAutomationId(box);if(id=="BookmarkTitle")box.Text="Probe root";if(id=="BookmarkColor")box.Text="#8151C8";}});
            await ProbeWait(()=>bookmarks.Any(b=>b.Title=="Probe root"));
            var root=bookmarks.Single(b=>b.Title=="Probe root");checks["BookmarkAddedByContextMenu"]=bookmarks.Count==originalCount+1;
            await ProbeDialog(async()=>{UiInputProbe.Click(this,FindMenu(BuildBookmarkMenu(root),"添加子书签"));await Task.Delay(100);},dialog=>{foreach(var box in Descendants<TextBox>(dialog)){string id=AutomationProperties.GetAutomationId(box);if(id=="BookmarkTitle")box.Text="Probe child";if(id=="BookmarkPage")box.Text=Math.Min(2,engine.PageCount).ToString();if(id=="BookmarkColor")box.Text="#27855D";}});
            await ProbeWait(()=>bookmarks.Any(b=>b.Title=="Probe child"));
            var child=bookmarks.Single(b=>b.Title=="Probe child");checks["ChildCreatedAtCorrectDepth"]=child.Depth==root.Depth+1&&root.HasChildren&&root.DescendantCount==1;
            SelectBookmark(child);int pageBefore=currentPage;navigation.Toggle(root);checks["CollapseHidesChildWithoutNavigation"]=!navigation.List.Items.Contains(child)&&ReferenceEquals(navigation.List.SelectedItem,root)&&currentPage==pageBefore;navigation.Toggle(root);checks["ReexpandKeepsChild"]=navigation.List.Items.Contains(child);
            await InvokeProbeMenu(FindMenu(BuildBookmarkMenu(child),"删除书签"),()=>!bookmarks.Contains(child));checks["DeleteByContextMenu"]=bookmarks.Count==originalCount+1;UndoBookmarks(false);checks["UndoDeletion"]=bookmarks.Count==originalCount+2;UndoBookmarks(true);checks["RedoDeletion"]=bookmarks.Count==originalCount+1;UndoBookmarks(false);root=bookmarks.Single(b=>b.Title=="Probe root");child=bookmarks.Single(b=>b.Title=="Probe child");
            await UpdateBookmarkDestination(root,new(.33,.61));var located=await engine.LocateBookmarkAsync(root,documentCancellation.Token);checks["BookmarkTargetOffsetUpdated"]=located!=null&&Math.Abs(located.Value.X-.33)<.0002&&Math.Abs(located.Value.Y-.61)<.0002;
            await ShowPage(0);SetCopySelection(new(.05,.08),new(.46,.42));await CopyText(false);checks["TextClipboardFilled"]=Clipboard.ContainsText()&&Clipboard.GetText().Length>20;CopyImage();var copied=Clipboard.GetImage();checks["SelectedImageClipboardFilled"]=copied!=null&&copied.PixelWidth>40;ClearCopySelection();contextPosition=new(.2,.75);await CopyEmbeddedImage();checks["OriginalImageCopied"]=Clipboard.ContainsImage()&&Clipboard.GetImage()?.PixelWidth==120;
            SetCopySelection(new(.05,.08),new(.46,.42));await CopyRecognizedText();checks["OcrClipboardChineseText"]=Clipboard.GetText().Count(c=>c>='\u4E00'&&c<='\u9FFF')>20;ClearCopySelection();
            SetHoverSource(new(.08,.1),new(.75,.45));contextPosition=new(.82,.2);await ProbeDialog(PlaceHoverMark,dialog=>Descendants<TextBox>(dialog).First().Text="Probe preview");await ProbeWait(()=>displayedPage==currentPage&&loadingBadge.Visibility!=Visibility.Visible);await RefreshHoverIcons(currentPage,generation,renderRevision,renderCancellation.Token);checks["HoverMarkPlaced"]=hoverIcons.Count==1;var mark=(await engine.HoverMarksAsync(0,documentCancellation.Token)).Single(h=>h.Reference.Title=="Probe preview");await ShowHoverPreview(mark.Reference,hoverIcons[0]);checks["HoverPreviewImageGenerated"]=hoverPopup.IsOpen&&Descendants<Image>(hoverPopup.Child).Any(i=>i.Source!=null);CloseHoverPopup();
            await PinRegion(0,new Rect(.06,.1,.38,.32),"Probe map one");await PinRegion(Math.Min(1,engine.PageCount-1),new Rect(.1,.15,.6,.5),"Probe map two");checks["MultiplePinsOpened"]=pins.Count==2&&pins.All(p=>p.Topmost);var pin=pins[0];pin.ZoomAt(3,new Point(60,60));checks["PinZoomed"]=Math.Abs(Descendants<Image>(pin).First().Width-pin.Snapshot.PixelWidth*3)<.1;await RenderCycle();var pinScroll=Descendants<ScrollViewer>(pin).First();double offset=pinScroll.HorizontalOffset;pin.PanBy(new(60,40));await RenderCycle();checks["PinPanned"]=pinScroll.HorizontalOffset>offset;checks["PinsIndependentFromMainMinimization"]=pins.All(p=>p.Owner==null);
            CapturePreview(directory);for(int i=0;i<pins.Count;i++){var surface=(FrameworkElement)pins[i].Content;var screenshot=new RenderTargetBitmap((int)surface.ActualWidth,(int)surface.ActualHeight,96,96,PixelFormats.Pbgra32);screenshot.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(screenshot));using var stream=File.Create(Path.Combine(directory,$"pin-{i+1}.png"));encoder.Save(stream);}foreach(var window in pins.ToArray())window.Close();
            var originalEntry=bookmarks.First(b=>b.SourceIndex==0);ChangeBookmarkColor(originalEntry,"#D94262");string copy=Path.Combine(directory,"feature-copy.pdf");engine.SetBookmarks(bookmarks,false);await engine.SaveCopyAsync(copy);using var verify=new PdfEngine();var restored=await verify.OpenAsync(copy);checks["BookmarkPersistence"]=restored.Bookmarks.Any(b=>b.Title=="Probe root")&&restored.Bookmarks.Any(b=>b.Title=="Probe child");var appearance=await PdfOutlineStore.ReadAppearanceAsync(copy,CancellationToken.None);checks["ExistingBookmarkRecolored"]=appearance[0].Color=="#D94262";checks["HoverPersistence"]= (await verify.HoverMarksAsync(0,CancellationToken.None)).Any(m=>m.Reference.Title=="Probe preview");checks["OriginalUnchanged"]=before.SequenceEqual(Program.HashFile(currentFile));
            File.WriteAllText(Path.Combine(directory,"feature-probe.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(b=>b),Checks=checks,Scope="Synthetic WPF menu accessibility invocation, actual dialog edits, clipboard, rendering, pin windows and PDF save/reopen. No physical mouse or compositor FPS acceptance."},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"feature-probe.json"),JsonSerializer.Serialize(new{error=ex.ToString(),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));}finally{if(previousClipboard!=null)try{Clipboard.SetDataObject(previousClipboard,true);}catch{}foreach(var pin in pins.ToArray())pin.Close();Close();}
    }
}
