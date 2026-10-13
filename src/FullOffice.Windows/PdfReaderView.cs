using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView : UserControl
{
    readonly PdfEngine engine=new();
    readonly SmoothList navigation=new();
    readonly TextBlock fileLabel=new(){Text="打开一本 PDF",FontSize=20,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis};
    readonly TextBlock status=new(){Text="准备好了",Foreground=new SolidColorBrush(Color.FromRgb(99,112,131))};
    readonly TextBlock metrics=new(){Foreground=new SolidColorBrush(Color.FromRgb(99,112,131)),HorizontalAlignment=HorizontalAlignment.Right};
    readonly TextBlock pageCount=new(){Text="/ 0",VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,0,0)};
    readonly TextBox pageNumber=new(){Width=46,Text="1",TextAlignment=TextAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
    readonly TextBox search=new(){Width=200,VerticalContentAlignment=VerticalAlignment.Center};
    readonly ScrollViewer scroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,CanContentScroll=false,Background=new SolidColorBrush(Color.FromRgb(235,238,244))};
    readonly Image pageImage=new(){Stretch=Stretch.Fill,SnapsToDevicePixels=true};
    readonly Canvas canvas=new();
    readonly Border selection=new(){IsHitTestVisible=false,CornerRadius=new(4),BorderThickness=new(1.5),BorderBrush=new SolidColorBrush(Color.FromRgb(64,112,237)),Background=new SolidColorBrush(Color.FromArgb(40,64,112,237)),Visibility=Visibility.Collapsed};
    readonly Border empty=new(){Background=Brushes.Transparent};
    readonly TextBlock emptyNavigation=new(){Text="此文件没有书签",Foreground=Brushes.SlateGray,Margin=new(14,18,14,0),IsHitTestVisible=false};
    readonly List<Bookmark> bookmarks=new();
    readonly List<Annotation> annotations=new();
    readonly Stopwatch openTimer=new();
    readonly string? startingFile;
    readonly string? smokeDirectory;
    readonly bool readOnlyProbe;
    CancellationTokenSource documentCancellation=new(),renderCancellation=new(),searchCancellation=new();
    string currentFile="";int currentPage;double zoom=1;double openedMs;bool annotationTab;int mode;Point dragStart;bool dragging;
    readonly Button highlightButton;readonly Button noteButton;
    readonly Button fitWidthButton;readonly Button fitPageButton;readonly Button wheelModeButton;
    readonly TextBlock zoomLabel=new(){Text="100%",Width=48,TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Foreground=Brushes.SlateGray};
    readonly TextBlock loadingText=new();
    readonly Border loadingBadge=new(){CornerRadius=new(9),Background=new SolidColorBrush(Color.FromArgb(238,255,255,255)),BorderBrush=new SolidColorBrush(Color.FromRgb(210,220,238)),BorderThickness=new(1),Padding=new(14,8,14,8),Margin=new(0,12,20,0),HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Top,IsHitTestVisible=false,Visibility=Visibility.Collapsed};
    readonly DispatcherTimer resizeTimer=new(){Interval=TimeSpan.FromMilliseconds(180)};
    bool fitWholePage;bool wheelPageMode;int presentedNativeWidth;double firstPreviewMs;double feedbackSynchronousMs;
    int generation;int renderRevision;bool updatingNavigation;bool opening;bool annotationsScanStarted;int displayedPage=-1;

    public PdfReaderView(WorkspaceWindow host,string? file,string? smoke=null,bool probe=false,string? features=null){
        Host=host;featureProbeDirectory=features;startingFile=file;smokeDirectory=smoke;readOnlyProbe=probe;
        FontFamily=new("Microsoft YaHei UI");FontSize=13;Background=Brushes.White;Foreground=new SolidColorBrush(Color.FromRgb(36,46,64));
        Resources.Add(typeof(Button),ButtonStyle());Resources.Add(typeof(TextBox),TextBoxStyle());
        var root=new Grid{Background=Brushes.White};root.RowDefinitions.Add(new(){Height=new GridLength(0)});root.RowDefinitions.Add(new(){Height=new GridLength(54)});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});root.RowDefinitions.Add(new(){Height=new GridLength(44)});Content=root;
        var toolbar=new DockPanel{Margin=new(22,0,18,0),LastChildFill=false};Grid.SetRow(toolbar,1);root.Children.Add(toolbar);
        var tools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};toolbar.Children.Add(tools);search.ToolTip="搜索文字，Enter 跳到下一页结果";tools.Children.Add(search);tools.Children.Add(Button("查找",async()=>await FindNext()));
        highlightButton=Button("文字高亮",()=>{SetMode(mode==1?0:1);return Task.CompletedTask;});noteButton=Button("添加批注",()=>{SetMode(mode==2?0:2);return Task.CompletedTask;});tools.Children.Add(highlightButton);tools.Children.Add(noteButton);
        tools.Children.Add(Button("选择复制",()=>{SetMode(3);return Task.CompletedTask;}));tools.Children.Add(Button("悬停来源",()=>{SetMode(4);return Task.CompletedTask;}));
        var pageTools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(pageTools,Dock.Right);toolbar.Children.Add(pageTools);pageTools.Children.Add(Button("‹",async()=>await ShowPage(currentPage-1)));pageTools.Children.Add(pageNumber);pageTools.Children.Add(pageCount);pageTools.Children.Add(Button("›",async()=>await ShowPage(currentPage+1)));
        var body=new Grid();body.ColumnDefinitions.Add(new(){Width=new GridLength(270)});body.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});Grid.SetRow(body,2);root.Children.Add(body);
        var sidebar=new Grid{Margin=new(14,14,12,14)};sidebar.RowDefinitions.Add(new(){Height=new GridLength(55)});sidebar.RowDefinitions.Add(new(){Height=new GridLength(39)});sidebar.RowDefinitions.Add(new(){Height=new GridLength(33)});sidebar.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});body.Children.Add(sidebar);fileLabel.Margin=new(10,4,6,10);sidebar.Children.Add(fileLabel);
        var tabs=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,0,0,5)};Grid.SetRow(tabs,1);sidebar.Children.Add(tabs);tabs.Children.Add(Button("书签",()=>{tagTab=false;annotationTab=false;RefreshNavigation();return Task.CompletedTask;}));tabs.Children.Add(Button("注释",()=>{tagTab=false;annotationTab=true;RefreshNavigation();if(!annotationsScanStarted){annotationsScanStarted=true;_ = ReadAnnotations(documentCancellation.Token,generation);}return Task.CompletedTask;}));tabs.Children.Add(Button("标签",()=>{tagTab=true;UpdateTagDisplays();return Task.CompletedTask;}));tabs.Children.Add(Button("＋",()=>CreateBookmark(CurrentReadingPosition(),null,"root")));Grid.SetRow(navigation,3);sidebar.Children.Add(navigation);Grid.SetRow(emptyNavigation,3);sidebar.Children.Add(emptyNavigation);
        Grid.SetColumn(scroll,1);body.Children.Add(scroll);var pageHost=new Grid{SnapsToDevicePixels=true,UseLayoutRounding=true,Margin=new(24),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,Background=Brushes.White};pageHost.Children.Add(pageImage);canvas.Children.Add(selection);pageHost.Children.Add(canvas);scroll.Content=pageHost;loadingBadge.Child=loadingText;Grid.SetColumn(loadingBadge,1);body.Children.Add(loadingBadge);
        empty.Child=new StackPanel{HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Children={new TextBlock{Text="让阅读更轻快",FontSize=30,FontWeight=FontWeights.SemiBold,HorizontalAlignment=HorizontalAlignment.Center},new TextBlock{Text="打开或拖入 PDF，开始阅读与标注。",Foreground=Brushes.SlateGray,Margin=new(0,16,0,24),HorizontalAlignment=HorizontalAlignment.Center},Button("选择 PDF",async()=>await ChooseFile())}};Grid.SetColumn(empty,1);body.Children.Add(empty);
        InitializeBookmarkCommands(body,pageHost);InitializeHoverPreview();InitializePageTags(body,sidebar);InitializeBookmarkSidebar(body,sidebar);
        var footer=new DockPanel{Margin=new(22,0,18,0)};Grid.SetRow(footer,3);root.Children.Add(footer);
        var viewTools=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(viewTools,Dock.Right);footer.Children.Add(viewTools);
        viewTools.Children.Add(Button("－",async()=>await ChangeZoom(-.15)));viewTools.Children.Add(zoomLabel);viewTools.Children.Add(Button("＋",async()=>await ChangeZoom(.15)));
        fitWidthButton=Button("适宽",async()=>await SetFit(false));fitPageButton=Button("整页适应",async()=>await SetFit(true));wheelModeButton=Button("滚动翻页",async()=>await SetWheelMode(!wheelPageMode));
        viewTools.Children.Add(fitWidthButton);viewTools.Children.Add(fitPageButton);viewTools.Children.Add(wheelModeButton);status.VerticalAlignment=VerticalAlignment.Center;status.TextTrimming=TextTrimming.CharacterEllipsis;footer.Children.Add(status);UpdateViewButtons();
        resizeTimer.Tick+=async(_,_)=>{resizeTimer.Stop();if(isActiveView&&!disposed&&currentFile!=""&&!opening)await ShowPage(currentPage,false,true,true);};
        scroll.SizeChanged+=(_,_)=>{if(isActiveView&&!disposed&&currentFile!=""&&!opening){resizeTimer.Stop();resizeTimer.Start();}};
        Host.DpiChanged+=HostDpiChanged;
        navigation.List.SelectionChanged+=async(_,_)=>{if(updatingNavigation||navigation.IsUpdating)return;try{if(navigation.List.SelectedItem is Bookmark b)await GoToBookmark(b);else if(navigation.List.SelectedItem is Annotation a)await GoToAnnotation(a);}catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}};
        pageNumber.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter&&int.TryParse(pageNumber.Text,out int n)){await ShowPage(n-1);Keyboard.ClearFocus();}};
        search.KeyDown+=async(_,e)=>{if(e.Key==Key.Enter)await FindNext();};
        AllowDrop=true;Drop+=async(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] files){e.Handled=true;await Host.OpenPaths(files);}};
        PreviewKeyDown+=async(_,e)=>{if(e.OriginalSource is TextBox)return;if(e.Key==Key.Right||e.Key==Key.PageDown){e.Handled=true;await ShowPage(currentPage+1);}else if(e.Key==Key.Left||e.Key==Key.PageUp){e.Handled=true;await ShowPage(currentPage-1);}else if(e.Key==Key.Escape)SetMode(0);else if(e.Key==Key.O&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control))await ChooseFile();else if(e.Key==Key.F&&Keyboard.Modifiers.HasFlag(ModifierKeys.Control)){search.Focus();search.SelectAll();}};
        scroll.PreviewMouseWheel+=async(_,e)=>{e.Handled=true;await HandleWheel(e.Delta,Keyboard.Modifiers.HasFlag(ModifierKeys.Control));};
        scroll.PreviewMouseDown+=(_,_)=>{ResetWheelGoal();SmoothScroll.Set(scroll,scroll.VerticalOffset);};
        pageImage.MouseLeftButtonDown+=async(_,e)=>{
            if(currentFile==""||displayedPage!=currentPage||opening||saving)return;var p=e.GetPosition(pageImage);dragStart=new(p.X/pageImage.ActualWidth,p.Y/pageImage.ActualHeight);copyDrag=mode==0||mode==3;copyRegion=null;
            if(mode==2){var text=InputNote();if(text!=null)await AddAnnotation(dragStart,dragStart,false,text);}
            else{dragging=true;pageImage.CaptureMouse();selection.Visibility=Visibility.Visible;DrawSelection(p,p);}e.Handled=true;
        };
        pageImage.MouseMove+=(_,e)=>{if(dragging)DrawSelection(new(dragStart.X*pageImage.ActualWidth,dragStart.Y*pageImage.ActualHeight),e.GetPosition(pageImage));};
        pageImage.MouseLeftButtonUp+=async(_,e)=>{if(!dragging)return;dragging=false;pageImage.ReleaseMouseCapture();selection.Visibility=Visibility.Collapsed;var p=e.GetPosition(pageImage);var end=new Point(Math.Clamp(p.X/pageImage.ActualWidth,0,1),Math.Clamp(p.Y/pageImage.ActualHeight,0,1));if(Math.Abs(end.X-dragStart.X)*pageImage.ActualWidth>3&&Math.Abs(end.Y-dragStart.Y)*pageImage.ActualHeight>3){if(mode==4)SetHoverSource(dragStart,end);else if(copyDrag)SetCopySelection(dragStart,end);else await AddAnnotation(dragStart,end,true,$"高亮 · 第 {currentPage+1} 页");}};
        InitializeRadialMenu(root,pageHost);
        Loaded+=async(_,_)=>{if(initialized)return;initialized=true;try{if(startingFile!=null)await Open(startingFile);ready.TrySetResult();if(featureProbeDirectory!=null)await FeatureProbe(featureProbeDirectory);else if(smokeDirectory!=null){if(readOnlyProbe)await UiProbe(smokeDirectory);else await UiSmoke(smokeDirectory);}}catch(Exception ex){ready.TrySetException(ex);}};
    }

    internal static Style ButtonStyle(){
        var style=new Style(typeof(Button));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(13,8,13,8)));style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(3,0,3,0)));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));style.Setters.Add(new Setter(Control.CursorProperty,Cursors.Hand));
        var template=new ControlTemplate(typeof(Button));var border=new FrameworkElementFactory(typeof(Border));border.Name="surface";border.SetValue(Border.CornerRadiusProperty,new CornerRadius(9));border.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});var cp=new FrameworkElementFactory(typeof(ContentPresenter));cp.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);cp.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);cp.SetBinding(FrameworkElement.MarginProperty,new System.Windows.Data.Binding("Padding"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});border.AppendChild(cp);template.VisualTree=border;
        var hover=new Trigger{Property=System.Windows.Controls.Button.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromRgb(237,242,251)),"surface"));template.Triggers.Add(hover);var down=new Trigger{Property=System.Windows.Controls.Button.IsPressedProperty,Value=true};down.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromRgb(218,229,250)),"surface"));template.Triggers.Add(down);style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;
    }
    static Style TextBoxStyle(){var style=new Style(typeof(TextBox));style.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(8,5,8,5)));style.Setters.Add(new Setter(Control.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(219,225,236))));style.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(1)));style.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(247,249,253))));return style;}
    static Button Button(string label,Func<Task> action){var button=new Button{Content=label};button.Click+=async(_,_)=>{try{await action();}catch(OperationCanceledException){}catch(Exception ex){MessageBox.Show(ex.Message,"FlowOffice",MessageBoxButton.OK,MessageBoxImage.Information);}};return button;}
    void SetMode(int value){mode=value;pageImage.Cursor=mode==0?Cursors.Arrow:Cursors.Cross;highlightButton.Background=mode==1?new SolidColorBrush(Color.FromRgb(254,241,187)):Brushes.Transparent;noteButton.Background=mode==2?new SolidColorBrush(Color.FromRgb(219,232,255)):Brushes.Transparent;status.Text=mode==1?"拖动选择文字区域，添加高亮":mode==2?"点击页面，添加批注":mode==3?"拖动框选区域，然后右键或按 Ctrl+C 复制":mode==4?"框选需要预览的正文区域，再到其他位置右键放置悬停标":"阅读模式";}
    Task ChooseFile()=>Host.OpenFilesDialog();
    async Task Open(string path){
        if(saving||disposed)return;
        if(engine.Modified&&MessageBox.Show(Host,"当前书签或标注尚未保存。仍然打开其他文件？","未保存的修改",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        renderCancellation.Cancel();searchCancellation.Cancel();documentCancellation.Cancel();documentCancellation=new();var token=documentCancellation.Token;int documentGeneration=++generation;
        status.Text="正在打开…";openTimer.Restart();opening=true;
        try{var info=await engine.OpenAsync(path,token);if(token.IsCancellationRequested)return;currentFile=path;currentPage=0;zoom=1;displayedPage=-1;pageImage.Source=null;openedMs=info.OpenMilliseconds;bookmarks.Clear();bookmarks.AddRange(info.Bookmarks);bookmarkUndo.Clear();bookmarkRedo.Clear();bookmarkClipboard=null;pendingHoverSource=null;ClearCopySelection();ClearHoverIcons();annotations.Clear();pageTags.Clear();tagTab=false;annotationTab=false;annotationsScanStarted=false;fileLabel.Text=Path.GetFileNameWithoutExtension(path);Title=$"{Path.GetFileName(path)} · FullOffice v2.5";pageCount.Text=$"/ {info.Pages}";RefreshNavigation();UpdateTagDisplays();empty.Visibility=Visibility.Collapsed;opening=false;await ShowPage(0);bookmarkAppearanceTask=ReadBookmarkAppearance(path,token,documentGeneration);Host.ReaderOpened(this);}
        catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}finally{if(documentGeneration==generation)opening=false;}
    }
    double PageDisplayWidth(int page){
        double available=Math.Max(80,scroll.ActualWidth-50-(fitWholePage?0:SystemParameters.VerticalScrollBarWidth));if(scroll.ActualWidth<=0)available=Math.Max(200,ActualWidth-340);
        if(fitWholePage)available=Math.Min(available,Math.Max(80,scroll.ActualHeight-50)/engine.PageAspect(page));
        return Math.Max(40,available*zoom);
    }
    void UpdateViewButtons(){
        var active=new SolidColorBrush(Color.FromRgb(225,235,254));fitWidthButton.Background=!fitWholePage&&Math.Abs(zoom-1)<.001?active:Brushes.Transparent;fitPageButton.Background=fitWholePage&&Math.Abs(zoom-1)<.001?active:Brushes.Transparent;
        wheelModeButton.Content=wheelPageMode?"仅页内滚动":"到页尾翻页";wheelModeButton.ToolTip=wheelPageMode?"只滚动当前页，不自动翻页；点击切换为到页尾翻页":"滚动触及页尾或页首时自动翻页；点击切换为仅页内滚动";wheelModeButton.Background=wheelPageMode?active:Brushes.Transparent;zoomLabel.Text=$"{zoom:P0}";
    }
    async Task SetFit(bool whole){fitWholePage=whole;zoom=1;UpdateViewButtons();await ShowPage(currentPage,false,true,true);}
    async Task ChangeZoom(double delta){zoom=Math.Clamp(zoom+delta,.4,3);UpdateViewButtons();await ShowPage(currentPage,false,true,true);}
    Task SetWheelMode(bool stayOnPage){wheelPageMode=stayOnPage;ResetWheelGoal();UpdateViewButtons();status.Text=stayOnPage?"仅滚动当前页，不自动翻页":"到页尾或页首时自动翻页";return Task.CompletedTask;}
    async Task ShowPage(int page,bool atBottom=false,bool followBookmark=true,bool keepPosition=false,Bookmark? destination=null,bool fromWheel=false){
        if(disposed||!isActiveView||opening||currentFile==""||page<0||page>=engine.PageCount)return;
        if(!fromWheel)ResetWheelGoal();
        var display=Stopwatch.StartNew();searchCancellation.Cancel();resizeTimer.Stop();
        bool samePage=page==displayedPage;double priorOffset=scroll.VerticalOffset,priorHeight=pageImage.Height;if(!samePage)ClearCopySelection();ClearHoverIcons();
        currentPage=page;UpdateTagDisplays();int revision=++renderRevision;pageNumber.Text=(page+1).ToString();renderCancellation.Cancel();renderCancellation.Dispose();renderCancellation=CancellationTokenSource.CreateLinkedTokenSource(documentCancellation.Token);var token=renderCancellation.Token;int documentGeneration=generation;pageImage.IsHitTestVisible=false;
        if(dragging){dragging=false;pageImage.ReleaseMouseCapture();}selection.Visibility=Visibility.Collapsed;
        var dpiInfo=VisualTreeHelper.GetDpi(this);double dpi=dpiInfo.DpiScaleX;
        double logicalWidth=PageDisplayWidth(page),logicalHeight=logicalWidth*engine.PageAspect(page);
        if(fitWholePage){logicalWidth=Math.Max(40,Math.Floor(logicalWidth*dpi)/dpi);logicalHeight=Math.Floor(logicalWidth*engine.PageAspect(page)*dpiInfo.DpiScaleY)/dpiInfo.DpiScaleY;}
        if(pageImage.Parent is FrameworkElement host){host.UseLayoutRounding=fitWholePage;host.SnapsToDevicePixels=fitWholePage;}
        int pixelWidth=RasterQuality.TargetWidth(logicalWidth,dpi,fitWholePage);
        bool sharpWholePage=fitWholePage;int physicalWidth=(int)Math.Round(logicalWidth*dpi),physicalHeight=(int)Math.Round(logicalHeight*dpiInfo.DpiScaleY);
        RenderOptions.SetBitmapScalingMode(pageImage,fitWholePage?BitmapScalingMode.HighQuality:BitmapScalingMode.Linear);
        status.Text=$"正在显示第 {page+1} 页…";loadingText.Text=status.Text;loadingBadge.Visibility=Visibility.Visible;firstPreviewMs=0;
        if(!annotationTab&&followBookmark){var bookmark=bookmarks.Where(b=>b.Page>=0&&b.Page<=page).OrderBy(b=>b.Page).LastOrDefault();if(bookmark!=null&&!Equals(navigation.List.SelectedItem,bookmark)){updatingNavigation=true;try{navigation.Reveal(bookmark);navigation.List.SelectedItem=bookmark;navigation.List.ScrollIntoView(bookmark);}finally{updatingNavigation=false;}}}
        bool hasPreview=engine.TryGetPreview(page,out var preview),initialPositionApplied=false,destinationApplied=false;
        if(hasPreview)firstPreviewMs=display.Elapsed.TotalMilliseconds;pageImage.Source=preview?.Image;if(sharpWholePage&&preview!=null&&RasterQuality.TryGetDisplay(preview.Image,physicalWidth,physicalHeight,out var sharpPreview))pageImage.Source=sharpPreview;pageImage.Width=logicalWidth;pageImage.Height=logicalHeight;canvas.Width=logicalWidth;canvas.Height=logicalHeight;UpdateTagDisplays();
        pageImage.BeginAnimation(OpacityProperty,null);pageImage.Opacity=1;
        SmoothScroll.Set(scroll,scroll.VerticalOffset);double earlyOffset=atBottom?double.MaxValue:samePage&&keepPosition?priorOffset*logicalHeight/Math.Max(1,priorHeight):0;
        if(!samePage&&destination!=null&&engine.TryGetBookmarkPosition(destination,out var earlyPoint)&&earlyPoint!=null)earlyOffset=Math.Max(0,earlyPoint.Value.Y*logicalHeight+8);
        scroll.ScrollToVerticalOffset(earlyOffset);feedbackSynchronousMs=display.Elapsed.TotalMilliseconds;
        try{
            // Let WPF paint the selected row, requested page and loading indicator
            // before any cached bitmap layout or native rendering continuation.
            await Dispatcher.Yield(DispatcherPriority.Background);if(token.IsCancellationRequested||revision!=renderRevision)return;
            void Present(RenderedPage? frame,BitmapSource? displayImage=null){
                if(frame!=null){presentedNativeWidth=frame.Width;pageImage.Source=displayImage??frame.Image;displayedPage=page;firstPreviewMs=firstPreviewMs==0?display.Elapsed.TotalMilliseconds:firstPreviewMs;}
                scroll.UpdateLayout();
                if(!initialPositionApplied){SmoothScroll.Set(scroll,atBottom?scroll.ScrollableHeight:samePage&&keepPosition?priorOffset*logicalHeight/Math.Max(1,priorHeight):0);initialPositionApplied=true;}
                if(destination!=null&&!destinationApplied&&engine.TryGetBookmarkPosition(destination,out var point)&&point!=null){double offset=Math.Max(0,point.Value.Y*logicalHeight+8);if(samePage)SmoothScroll.To(scroll,offset);else SmoothScroll.Set(scroll,offset);if(destination.X!=null)scroll.ScrollToHorizontalOffset(Math.Max(0,point.Value.X*logicalWidth+8));destinationApplied=true;}
            }
            Present(preview);
            if(!hasPreview&&pixelWidth>384){var coarse=await engine.RenderAsync(page,384,token);if(token.IsCancellationRequested||revision!=renderRevision)return;Present(coarse);loadingText.Text=$"第 {page+1} 页 · 正在清晰化…";}
            var rendered=await engine.RenderAsync(page,pixelWidth,token,allowLarger:fitWholePage);if(token.IsCancellationRequested||documentGeneration!=generation||revision!=renderRevision)return;
            BitmapSource? finalImage=sharpWholePage?await RasterQuality.DownsampleAsync(rendered.Image,physicalWidth,physicalHeight,token):null;
            if(token.IsCancellationRequested||documentGeneration!=generation||revision!=renderRevision)return;
            Present(rendered,finalImage);pageImage.IsHitTestVisible=true;loadingBadge.Visibility=Visibility.Collapsed;
            RenderCopySelection();UpdateTagDisplays();Host.ReaderChanged(this);_ = RefreshHoverIcons(page,documentGeneration,revision,token);status.Text=engine.Modified?"有未保存的书签或标注":wheelPageMode?"仅页内滚动":"到页尾翻页";metrics.Text=$"打开 {openedMs:F0} ms  ·  首次预览 {firstPreviewMs:F0} ms  ·  清晰页面 {display.Elapsed.TotalMilliseconds:F0} ms";
            _ = Prefetch(page+1,pixelWidth,token);_ = Prefetch(page-1,pixelWidth,token);
        }catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested&&revision==renderRevision){status.Text=ex.Message;loadingBadge.Visibility=Visibility.Collapsed;}}
    }
    async Task Prefetch(int page,int width,CancellationToken token){if(page<0||page>=engine.PageCount)return;try{await Task.Delay(320,token);await engine.RenderAsync(page,width,token,true);}catch(OperationCanceledException){}catch(Exception){}}
    async Task ReadAnnotations(CancellationToken token,int documentGeneration){
        try{for(int p=0;p<engine.PageCount;p++){if(token.IsCancellationRequested||documentGeneration!=generation)return;var items=await engine.AnnotationsAsync(p,token,true);if(token.IsCancellationRequested||documentGeneration!=generation)return;annotations.RemoveAll(x=>x.Page==p);annotations.AddRange(items);if(annotationTab&&items.Count>0)RefreshNavigation();await Task.Delay(1,token);}}
        catch(OperationCanceledException){}catch(Exception ex){if(!token.IsCancellationRequested)status.Text="部分注释读取失败："+ex.Message;}
    }
    void RefreshNavigation(){updatingNavigation=true;try{navigation.SetItems(annotationTab?annotations.Cast<object>():bookmarks.Cast<object>());}finally{updatingNavigation=false;}emptyNavigation.Text=annotationTab?"此文件还没有注释":"此文件没有书签";emptyNavigation.Visibility=(annotationTab?annotations.Count:bookmarks.Count)==0?Visibility.Visible:Visibility.Collapsed;UpdateTagDisplays();}
    async Task GoToBookmark(Bookmark item){if(!item.CanNavigate){if(item.HasChildren)navigation.Toggle(item);status.Text="这是目录分组或非页内目标，可以为它设置跳转位置";return;}int revision=renderRevision+1;await ShowPage(item.Page,false,false,item.Y!=null,item);if(revision!=renderRevision||displayedPage!=item.Page)return;var position=await engine.LocateBookmarkAsync(item,renderCancellation.Token);if(revision!=renderRevision||displayedPage!=item.Page||position==null)return;item.VisualY=position.Value.Y;SmoothScroll.To(scroll,Math.Max(0,position.Value.Y*pageImage.Height+8));if(item.X!=null)scroll.ScrollToHorizontalOffset(Math.Max(0,position.Value.X*pageImage.Width+8));UpdateTagDisplays();}
    async Task GoToAnnotation(Annotation item){int revision=renderRevision+1;await ShowPage(item.Page,false,true,true);if(revision!=renderRevision||displayedPage!=item.Page)return;var rect=await engine.LocateAsync(item);if(revision!=renderRevision||displayedPage!=item.Page)return;selection.BeginAnimation(OpacityProperty,null);selection.Opacity=1;selection.Visibility=Visibility.Visible;Canvas.SetLeft(selection,rect.X*pageImage.Width);Canvas.SetTop(selection,rect.Y*pageImage.Height);selection.Width=Math.Max(20,rect.Width*pageImage.Width);selection.Height=Math.Max(20,rect.Height*pageImage.Height);SmoothScroll.To(scroll,Math.Max(0,rect.Y*pageImage.Height-scroll.ViewportHeight*.35));selection.BeginAnimation(OpacityProperty,new DoubleAnimation(1,0,TimeSpan.FromMilliseconds(950)){BeginTime=TimeSpan.FromMilliseconds(350)});}
    async Task FindNext(){if(currentFile==""||string.IsNullOrWhiteSpace(search.Text))return;searchCancellation.Cancel();searchCancellation=new();var token=searchCancellation.Token;status.Text="正在查找…";try{int hit=await engine.FindNextAsync(search.Text,currentPage+1,token);if(token.IsCancellationRequested)return;if(hit<0)status.Text="没有找到匹配文字";else await ShowPage(hit);}catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}}
    async Task AddAnnotation(Point a,Point b,bool highlight,string text){if(!CanEditDocument)return;int page=currentPage,documentGeneration=generation;try{renderCancellation.Cancel();await engine.AnnotateAsync(page,a,b,highlight,text);var items=await engine.AnnotationsAsync(page,documentCancellation.Token);if(documentGeneration!=generation)return;annotations.RemoveAll(x=>x.Page==page);annotations.AddRange(items);RefreshNavigation();if(currentPage==page)await ShowPage(page,false,true,true);}catch(Exception ex){status.Text=ex.Message;}}
    void DrawSelection(Point a,Point b){selection.BeginAnimation(OpacityProperty,null);selection.Opacity=1;Canvas.SetLeft(selection,Math.Min(a.X,b.X));Canvas.SetTop(selection,Math.Min(a.Y,b.Y));selection.Width=Math.Abs(a.X-b.X);selection.Height=Math.Abs(a.Y-b.Y);}
    string? InputNote(){var dialog=new Window{Owner=Host,Title="添加批注",Width=420,Height=245,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,FontFamily=FontFamily};var panel=new DockPanel{Margin=new(18)};var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,12,0,0)};DockPanel.SetDock(buttons,Dock.Bottom);panel.Children.Add(buttons);var text=new TextBox{AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};panel.Children.Add(text);var ok=new Button{Content="添加",Padding=new(18,8,18,8),Margin=new(5,0,0,0)};ok.Click+=(_,_)=>{if(!string.IsNullOrWhiteSpace(text.Text))dialog.DialogResult=true;};buttons.Children.Add(ok);var cancel=new Button{Content="取消",Padding=new(18,8,18,8),Margin=new(5,0,0,0)};cancel.Click+=(_,_)=>dialog.DialogResult=false;buttons.Children.Add(cancel);dialog.Content=panel;dialog.Loaded+=(_,_)=>text.Focus();return dialog.ShowDialog()==true?text.Text:null;}
    async Task UiSmoke(string directory){
        var originalHash=Program.HashFile(currentFile);
        var heartbeats=new List<double>();var heartbeatClock=Stopwatch.StartNew();double lastTick=0;var heartbeat=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(16)};heartbeat.Tick+=(_,_)=>{double now=heartbeatClock.Elapsed.TotalMilliseconds;if(lastTick>0)heartbeats.Add(now-lastTick);lastTick=now;};heartbeat.Start();
        try{
            Directory.CreateDirectory(directory);await Task.Delay(400);await ShowPage(Math.Min(7,engine.PageCount-1));await Task.Delay(450);if(bookmarks.Count>2)navigation.List.SelectedItem=bookmarks[2];await Task.Delay(600);
            heartbeat.Stop();CapturePreview(directory);
            await ShowPage(0);await AddAnnotation(new(.15,.20),new(.72,.24),true,"UI verification highlight");await engine.SaveCopyAsync(Path.Combine(directory,"ui-annotated-copy.pdf"));
            File.WriteAllText(Path.Combine(directory,"ui-smoke.json"),JsonSerializer.Serialize(new{Window=Title,PageCount=engine.PageCount,Bookmarks=bookmarks.Count,Annotations=annotations.Count,CurrentPage=currentPage+1,UiNavigationHeartbeatTicks=heartbeats.Count,UiNavigationHeartbeatMaxGapMs=heartbeats.Count==0?0:heartbeats.Max(),HeartbeatScope="Short synthetic navigation probe; excludes initial load, screenshot and annotation save. Not a compositor FPS or real-file acceptance measurement.",Metrics=metrics.Text,OriginalUnchanged=originalHash.SequenceEqual(Program.HashFile(currentFile))}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"ui-smoke.json"),JsonSerializer.Serialize(new{error=ex.ToString()}));}finally{heartbeat.Stop();Close();}
    }
    void CapturePreview(string directory){var surface=(FrameworkElement)Content;var target=new RenderTargetBitmap((int)surface.ActualWidth,(int)surface.ActualHeight,96,96,PixelFormats.Pbgra32);target.Render(surface);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(target));using var f=File.Create(Path.Combine(directory,"desktop-preview.png"));png.Save(f);}
    async Task RenderCycle(){
        var ready=new TaskCompletionSource();EventHandler? callback=null;callback=(_,_)=>{CompositionTarget.Rendering-=callback;ready.TrySetResult();};CompositionTarget.Rendering+=callback;
        try{if(await Task.WhenAny(ready.Task,Task.Delay(1000))!=ready.Task)throw new TimeoutException("The WPF rendering callback did not arrive.");}finally{CompositionTarget.Rendering-=callback;}
    }
    async Task<object> ProbeControls(string directory){
        async Task WaitReady(int page){var timeout=Stopwatch.StartNew();while((displayedPage!=page||loadingBadge.Visibility==Visibility.Visible||resizeTimer.IsEnabled)&&timeout.ElapsedMilliseconds<10000)await Task.Delay(16);if(timeout.ElapsedMilliseconds>=10000)throw new TimeoutException("The requested control action did not settle.");await RenderCycle();}
        await ShowPage(Math.Min(currentPage,Math.Max(0,engine.PageCount-2)));
        var clickTimes=new List<object>();
        async Task ClickAndCheck(Button button,Func<bool> changed,string label){var timer=Stopwatch.StartNew();UiInputProbe.Click(this,button);while(!changed()&&timer.ElapsedMilliseconds<3000)await Task.Delay(1);if(!changed())throw new IOException("The input click did not activate "+label);double stateMs=timer.Elapsed.TotalMilliseconds;await RenderCycle();clickTimes.Add(new{Control=label,AutomationActionUntilStateChangedMs=stateMs,AutomationActionUntilNextWpfRenderCallbackMs=timer.Elapsed.TotalMilliseconds});await WaitReady(currentPage);}
        var imageBeforeFit=pageImage.Source as BitmapSource;
        await ClickAndCheck(fitPageButton,()=>fitWholePage,"Fit whole page");
        var fitImage=pageImage.Source as BitmapSource??throw new IOException("The fit-page bitmap is missing.");
        double fitDpi=VisualTreeHelper.GetDpi(this).DpiScaleX;double fitPhysicalWidth=pageImage.Width*fitDpi;int fitRequiredWidth=RasterQuality.TargetWidth(pageImage.Width,fitDpi,true);
        int fitNativeWidth=presentedNativeWidth;bool fullResolution=fitNativeWidth>=fitRequiredWidth,highQualityScaling=RenderOptions.GetBitmapScalingMode(pageImage)==BitmapScalingMode.HighQuality;
        bool reuseRequired=imageBeforeFit!=null&&imageBeforeFit.PixelWidth>=fitRequiredWidth;
        bool sharperCachePreserved=!reuseRequired||fitNativeWidth>=imageBeforeFit!.PixelWidth;
        bool physicalPixelsMatch=fitImage.PixelWidth==(int)Math.Round(fitPhysicalWidth)&&fitImage.PixelHeight==(int)Math.Round(pageImage.Height*VisualTreeHelper.GetDpi(this).DpiScaleY);
        bool fullFits=pageImage.Height+48<=scroll.ViewportHeight+2&&pageImage.Width+48<=scroll.ViewportWidth+2;
        int pageBefore=currentPage;await ClickAndCheck(wheelModeButton,()=>wheelPageMode,"Stay on current page");await HandleWheel(-120,false);await WaitReady(currentPage);bool manualStayed=currentPage==pageBefore;
        await ClickAndCheck(wheelModeButton,()=>!wheelPageMode,"Advance at page end");await HandleWheel(-120,false);await WaitReady(currentPage);bool pageForward=currentPage==Math.Min(pageBefore+1,engine.PageCount-1);await HandleWheel(120,false);await WaitReady(currentPage);bool pageBack=currentPage==pageBefore;
        await ClickAndCheck(fitWidthButton,()=>!fitWholePage,"Fit width");
        bool widthFits=Math.Abs(pageImage.Width-(scroll.ActualWidth-SystemParameters.VerticalScrollBarWidth-50-TagWidth(currentPage)))<2&&pageImage.Width+48<=scroll.ViewportWidth+2;
        var parent=bookmarks.FirstOrDefault(b=>b.HasChildren);bool folding=true,reveal=true;if(parent!=null&&!parent.Expanded)navigation.Toggle(parent);int beforeCount=navigation.List.Items.Count;
        if(parent!=null){navigation.Toggle(parent);int collapsedCount=navigation.List.Items.Count;folding=collapsedCount<beforeCount&&currentPage==pageBefore;var descendant=bookmarks.SkipWhile(b=>!ReferenceEquals(b,parent)).Skip(1).First(b=>b.Depth>parent.Depth);navigation.Reveal(descendant);reveal=navigation.List.Items.Contains(descendant);}
        var target=bookmarks.FirstOrDefault(b=>b.Depth>=2&&b.Page!=currentPage)??bookmarks.FirstOrDefault(b=>b.Page!=currentPage);
        bool bookmarkClicked=target==null;double bookmarkClickStateMs=0,bookmarkClickFrameMs=0;
        if(target!=null){updatingNavigation=true;navigation.Reveal(target);navigation.List.ScrollIntoView(target);updatingNavigation=false;await RenderCycle();await Task.Delay(100);var item=navigation.List.ItemContainerGenerator.ContainerFromItem(target) as ListBoxItem;if(item==null)throw new IOException("The bookmark row was not realized.");var timer=Stopwatch.StartNew();UiInputProbe.Click(this,item);while((currentPage!=target.Page||!Equals(navigation.List.SelectedItem,target))&&timer.ElapsedMilliseconds<3000)await Task.Delay(1);bookmarkClickStateMs=timer.Elapsed.TotalMilliseconds;bookmarkClicked=currentPage==target.Page&&Equals(navigation.List.SelectedItem,target);await RenderCycle();bookmarkClickFrameMs=timer.Elapsed.TotalMilliseconds;await WaitReady(target.Page);}
        await SetFit(true);Host.Width=1040;Host.Height=700;await Task.Delay(230);await WaitReady(currentPage);bool resizedFits=pageImage.Height+48<=scroll.ViewportHeight+2&&pageImage.Width+48<=scroll.ViewportWidth+2;
        CapturePreview(directory);File.Move(Path.Combine(directory,"desktop-preview.png"),Path.Combine(directory,"compact-fit-preview.png"),true);
        Host.Width=1280;Host.Height=820;await Task.Delay(230);await WaitReady(currentPage);CapturePreview(directory);
        File.WriteAllText(Path.Combine(directory,"ui-controls-diagnostics.json"),JsonSerializer.Serialize(new{fullFits,fullResolution,highQualityScaling,sharperCachePreserved,physicalPixelsMatch,fitNativeWidth,fitBitmapWidth=fitImage.PixelWidth,fitDisplayPhysicalWidth=fitPhysicalWidth,fitRequiredWidth,widthFits,manualStayed,pageForward,pageBack,folding,reveal,bookmarkClicked,resizedFits,pageBefore,currentPage,viewportWidth=scroll.ViewportWidth,viewportHeight=scroll.ViewportHeight,pageWidth=pageImage.Width,pageHeight=pageImage.Height,clickTimes}));
        if(!fullFits||!fullResolution||!highQualityScaling||!sharperCachePreserved||!physicalPixelsMatch||!widthFits||!manualStayed||!pageForward||!pageBack||!folding||!reveal||!bookmarkClicked||!resizedFits)throw new IOException("A fit, wheel, bookmark or window-resize interaction check failed.");
        return new{FullPageSupersamplingPassed=fullResolution,WholePageHighQualityScalingPassed=highQualityScaling,HigherResolutionCachePreservedPassed=sharperCachePreserved,WholePageBitmapWidth=fitImage.PixelWidth,WholePageNativeRenderWidth=fitNativeWidth,WholePagePhysicalPixelsMatchPassed=physicalPixelsMatch,WholePagePhysicalDisplayWidth=fitPhysicalWidth,WholePageRequiredWidth=fitRequiredWidth,FullPageFitsViewport=fullFits,WidthFitsViewport=widthFits,ManualWheelStaysOnPagePassed=manualStayed,PageWheelForwardPassed=pageForward,PageWheelBackwardPassed=pageBack,BookmarkFoldPassed=folding,BookmarkRevealPassed=reveal,BookmarkAutomationActionPassed=bookmarkClicked,BookmarkAutomationActionUntilStateChangedMs=bookmarkClickStateMs,BookmarkAutomationActionUntilNextWpfRenderCallbackMs=bookmarkClickFrameMs,ResizedWholePageFitsViewport=resizedFits,BookmarkRowHeight=SmoothList.RowHeight,ControlClicks=clickTimes,Scope="Synthetic WPF accessibility invocation and selection; no physical-pointer click measurement. Rendering callback precedes compositor presentation."};
    }
    async Task UiProbe(string directory){
        Directory.CreateDirectory(directory);var initialOpenMs=openTimer.Elapsed.TotalMilliseconds;var initialProcessMs=(DateTime.UtcNow-Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
        var before=Program.HashFile(currentFile);var gaps=new List<double>();var gapDetails=new List<object>();string phase="initial";var clock=Stopwatch.StartNew();double last=0;var pulse=new DispatcherTimer(DispatcherPriority.Input){Interval=TimeSpan.FromMilliseconds(16)};pulse.Tick+=(_,_)=>{double now=clock.Elapsed.TotalMilliseconds;if(last>0){double gap=now-last;gaps.Add(gap);if(gap>50)gapDetails.Add(new{Phase=phase,GapMs=gap,AtMs=now});}last=now;};
        try{
            if(displayedPage<0)throw new IOException("The initial page was not displayed.");
            await RenderCycle();await Task.Delay(200);pulse.Start();var unique=bookmarks.Where(b=>b.Page>0).GroupBy(b=>b.Page).Select(g=>g.First()).ToArray();
            var targets=unique.Length==0?new[]{new Bookmark("First page",0,0)}:Enumerable.Range(0,Math.Min(6,unique.Length)).Select(i=>unique[(int)((long)i*(unique.Length-1)/Math.Max(1,Math.Min(6,unique.Length)-1))]).ToArray();
            var jumps=new List<object>();for(int pass=0;pass<2;pass++)foreach(var bookmark in targets){
                phase="bookmark-"+pass+"-"+(bookmark.Page+1);
                updatingNavigation=true;try{navigation.List.SelectedItem=bookmark;navigation.List.ScrollIntoView(bookmark);}finally{updatingNavigation=false;}
                var timer=Stopwatch.StartNew();await GoToBookmark(bookmark);double submitted=timer.Elapsed.TotalMilliseconds;double previewMs=firstPreviewMs,synchronousFeedback=feedbackSynchronousMs;await RenderCycle();
                jumps.Add(new{Page=bookmark.Page+1,Pass=pass,BookmarkHasVerticalCoordinate=bookmark.Y!=null,BitmapAndDestinationSubmittedMs=submitted,FirstPreviewSubmittedMs=previewMs,SynchronousFeedbackUpdatedMs=synchronousFeedback,NextWpfRenderCallbackMs=timer.Elapsed.TotalMilliseconds,DisplayedPage=displayedPage+1});await Task.Delay(200);
            }
            phase="viewport";await ShowPage(Math.Min(7,engine.PageCount-1));SmoothScroll.Set(scroll,scroll.ScrollableHeight*.4);await RenderCycle();double preservedOffset=scroll.VerticalOffset;await ShowPage(currentPage,false,false,true);await RenderCycle();bool samePagePreserved=Math.Abs(scroll.VerticalOffset-preservedOffset)<1;
            SmoothScroll.Set(scroll,0);await Task.Delay(200);double previousGoal=0;int previousPage=currentPage;bool goalsMonotonic=true;var wheelSteps=new List<object>();
            phase="wheel";for(int i=0;i<8;i++){await HandleWheel(-120,false);double goal=SmoothScroll.Goal(scroll);if(currentPage==previousPage)goalsMonotonic&=goal>=previousGoal;wheelSteps.Add(new{Page=currentPage+1,Goal=goal,Offset=scroll.VerticalOffset});previousGoal=goal;previousPage=currentPage;await Task.Delay(16);}
            await Task.Delay(250);double finalWheelOffset=scroll.VerticalOffset;int pageBeforeBoundary=currentPage;SmoothScroll.Set(scroll,scroll.ScrollableHeight);await Task.Delay(200);await HandleWheel(-120,false);await RenderCycle();bool forwardPassed=currentPage==Math.Min(pageBeforeBoundary+1,engine.PageCount-1);
            await Task.Delay(220);SmoothScroll.Set(scroll,0);await HandleWheel(120,false);await RenderCycle();bool backwardPassed=currentPage==pageBeforeBoundary;phase="burst";
            var burst=new List<Task>();var requestTimes=new List<object>();var burstClock=Stopwatch.StartNew();for(int i=0;i<12;i++){var call=Stopwatch.StartNew();burst.Add(ShowPage(targets[i%targets.Length].Page));requestTimes.Add(new{Index=i,Page=targets[i%targets.Length].Page+1,SynchronousMs=call.Elapsed.TotalMilliseconds,AtMs=clock.Elapsed.TotalMilliseconds});await Task.Delay(10);}await Task.WhenAll(burst);int finalRequested=targets[11%targets.Length].Page;bool latestPagePassed=displayedPage==finalRequested;double burstMs=burstClock.Elapsed.TotalMilliseconds;await RenderCycle();await Task.Delay(200);pulse.Stop();File.WriteAllText(Path.Combine(directory,"burst-calls.json"),JsonSerializer.Serialize(requestTimes,new JsonSerializerOptions{WriteIndented=true}));CapturePreview(directory);
            File.WriteAllText(Path.Combine(directory,"dispatcher-gaps.json"),JsonSerializer.Serialize(gapDetails,new JsonSerializerOptions{WriteIndented=true}));
            var controls=await ProbeControls(directory);
            using var process=Process.GetCurrentProcess();var sorted=gaps.OrderBy(x=>x).ToArray();double Quantile(double p)=>sorted.Length==0?0:sorted[(int)Math.Ceiling((sorted.Length-1)*p)];
            File.WriteAllText(Path.Combine(directory,"ui-probe.json"),JsonSerializer.Serialize(new{File=Path.GetFileName(currentFile),Sha256=Convert.ToHexString(before).ToLowerInvariant(),Pages=engine.PageCount,Bookmarks=bookmarks.Count,BookmarksWithPosition=bookmarks.Count(b=>b.Y!=null),OpenUntilBitmapSubmittedMs=initialOpenMs,ProcessStartUntilBitmapSubmittedMs=initialProcessMs,Jumps=jumps,SamePageViewportPreserved=samePagePreserved,WheelGoalsMonotonicWithinPage=goalsMonotonic,WheelSteps=wheelSteps,WheelFinalOffset=finalWheelOffset,WheelForwardBoundaryPassed=forwardPassed,WheelBackwardBoundaryPassed=backwardPassed,LatestRequestWinsPassed=latestPagePassed,Controls=controls,BurstRequests=12,BurstUntilSettledMs=burstMs,DispatcherHeartbeatTicks=gaps.Count,DispatcherHeartbeatP95GapMs=Quantile(.95),DispatcherHeartbeatMaxGapMs=gaps.Count==0?0:gaps.Max(),WorkingSetBytes=process.WorkingSet64,PeakWorkingSetBytes=process.PeakWorkingSet64,OriginalUnchanged=before.SequenceEqual(Program.HashFile(currentFile)),Scope="Real-file read-only WPF interaction probe. Rendering callback occurs before compositor presentation; heartbeat is dispatcher responsiveness, not rendered FPS. Initial time ends at bitmap submission. OS file cache uncontrolled. No ONLYOFFICE comparison or sustained 60/120 fps acceptance."},new JsonSerializerOptions{WriteIndented=true}));
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"ui-probe.json"),JsonSerializer.Serialize(new{error=ex.ToString()}));}finally{pulse.Stop();Close();}
    }
}
