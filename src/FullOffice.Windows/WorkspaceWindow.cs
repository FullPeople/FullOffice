using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace FlowOfficeLab;

public sealed partial class WorkspaceWindow : Window
{
    readonly List<PdfReaderView> readers=new();
    readonly Dictionary<PdfReaderView,bool> dirtyMarks=new();
    readonly Stack<string> closedFiles=new();
    readonly Grid readerSurface=new();
    readonly StackPanel tabStrip=new(){Orientation=Orientation.Horizontal};
    readonly Border homeSurface=new(){Background=new SolidColorBrush(Color.FromRgb(245,247,252))};
    readonly TextBox recentSearch=new(){Width=230,Padding=new(11,7,11,7),BorderBrush=new SolidColorBrush(Color.FromRgb(221,227,239)),BorderThickness=new(1)};
    readonly StackPanel recentRows=new();
    readonly TextBlock homeStatus=new(){Foreground=Brushes.SlateGray,Margin=new(0,12,0,0),TextWrapping=TextWrapping.Wrap};
    readonly Button saveButton;
    readonly TextBlock windowSubtitle=new(){Text="PDF 内核预览 · 0.1.0",Foreground=Brushes.SlateGray,VerticalAlignment=VerticalAlignment.Center,Margin=new(16,0,0,0)};
    readonly RecentStore recent;
    readonly string? initialFile,smokeDirectory,featuresDirectory;
    readonly bool readOnlyProbe;
    readonly string[] startupArgs;
    PdfReaderView? active;
    bool closingApproved,closingBusy;
    public bool RadialEnabled=>recent.RadialEnabled;
    public bool IsProbe=>smokeDirectory!=null||featuresDirectory!=null||startupArgs.Contains("--workspace-probe")||startupArgs.Contains("--tabs-probe")||startupArgs.Contains("--revision-probe")||startupArgs.Contains("--saving-probe");
    public WorkspaceWindow(string? file,string? smoke,bool probe,string? features,string[] args){
        initialFile=file;smokeDirectory=smoke;readOnlyProbe=probe;featuresDirectory=features;startupArgs=args;
        string profile=IsProbe?Path.Combine(args.Length>=3?args[2]:Path.GetTempPath(),"isolated-profile"):Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FlowOfficeLab");
        recent=new(profile);Title="FullOffice · 开始阅读";Width=1320;Height=900;MinWidth=980;MinHeight=630;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        FontFamily=new("Microsoft YaHei UI");FontSize=13;Foreground=new SolidColorBrush(Color.FromRgb(36,46,64));Background=Brushes.White;
        Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/icon-64.png"));Resources.Add(typeof(Button),PdfReaderView.ButtonStyle());
        var root=new Grid{Background=Brushes.White};root.RowDefinitions.Add(new(){Height=new GridLength(32)});root.RowDefinitions.Add(new(){Height=new GridLength(46)});root.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Content=root;
        var header=new DockPanel{Margin=new(22,3,20,3)};root.Children.Add(header);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);header.Children.Add(actions);actions.Children.Add(ActionButton("打开 PDF",OpenFilesDialog));saveButton=ActionButton("保存",async()=>{if(active!=null)await active.SaveFromShell();});actions.Children.Add(saveButton);saveButton.ToolTip="保存到当前 PDF · Ctrl+S";saveButton.IsEnabled=false;
        var options=new ContextMenu();var gestures=new MenuItem{Header="启用右键圆形菜单",IsCheckable=true,IsChecked=recent.RadialEnabled};gestures.Click+=(_,_)=>{recent.RadialEnabled=gestures.IsChecked;recent.Save();};options.Items.Add(gestures);var traditional=new MenuItem{Header="普通右键：完整菜单",IsEnabled=false};options.Items.Add(traditional);var settings=ActionButton("设置",()=>{options.IsOpen=true;return Task.CompletedTask;});settings.ContextMenu=options;options.PlacementTarget=settings;actions.Children.Add(settings);
        foreach(var item in actions.Children.OfType<Button>())item.Padding=new(11,4,11,4);
        var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};brand.Children.Add(new Image{Source=Icon,Width=22,Height=22,Margin=new(0,0,11,0)});brand.Children.Add(new TextBlock{Text="FullOffice",FontWeight=FontWeights.SemiBold,FontSize=16,VerticalAlignment=VerticalAlignment.Center});brand.Children.Add(windowSubtitle);header.Children.Add(brand);
        var tabArea=new DockPanel{Background=new SolidColorBrush(Color.FromRgb(246,248,252)),LastChildFill=true};Grid.SetRow(tabArea,1);root.Children.Add(tabArea);var plus=ActionButton("＋",()=>{ShowHome();return Task.CompletedTask;});plus.ToolTip="开始阅读 · Ctrl+T";DockPanel.SetDock(plus,Dock.Right);tabArea.Children.Add(plus);tabArea.Children.Add(new ScrollViewer{Content=tabStrip,HorizontalScrollBarVisibility=ScrollBarVisibility.Hidden,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new(12,4,8,0)});
        var content=new Grid();Grid.SetRow(content,2);root.Children.Add(content);content.Children.Add(readerSurface);content.Children.Add(homeSurface);BuildHome();RefreshTabs();
        AllowDrop=true;Drop+=async(_,e)=>{if(e.Data.GetData(DataFormats.FileDrop) is string[] files){e.Handled=true;await OpenPaths(files);}};
        PreviewKeyDown+=OnWorkspaceKey;
        Closing+=async(_,e)=>{if(closingApproved)return;e.Cancel=true;if(closingBusy)return;closingBusy=true;try{await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);foreach(var view in readers.ToArray()){if(!await view.ConfirmClose())return;}closingApproved=true;Close();}finally{closingBusy=false;}};
        Closed+=(_,_)=>{foreach(var view in readers.ToArray()){Remember(view);view.DisposeReader();}};
        Loaded+=async(_,_)=>{if(startupArgs.Length>=3&&startupArgs[0]=="--saving-probe"){await SavingProbe(startupArgs[1],startupArgs[2]);return;}if(startupArgs.Length>=3&&startupArgs[0]=="--revision-probe"){await RevisionProbe(startupArgs[1],startupArgs[2]);return;}if(startupArgs.Length>=3&&startupArgs[0]=="--workspace-probe"){await WorkspaceProbe(startupArgs[1],startupArgs[2]);return;}if(startupArgs.Length>=3&&startupArgs[0]=="--tabs-probe"){await RealTabsProbe(startupArgs[1],startupArgs[2]);return;}if(initialFile!=null)await OpenDocument(initialFile,smokeDirectory,readOnlyProbe,featuresDirectory);};
    }
    static Button ActionButton(string text,Func<Task> action){var button=new Button{Content=text};button.Click+=async(_,_)=>{try{await action();}catch(OperationCanceledException){}catch(Exception ex){MessageBox.Show(ex.Message,"FlowOffice",MessageBoxButton.OK,MessageBoxImage.Information);}};return button;}
    public async Task OpenFilesDialog(){var dialog=new OpenFileDialog{Filter="PDF 文件|*.pdf",Multiselect=true};if(dialog.ShowDialog(this)==true)await OpenPaths(dialog.FileNames);}
    public async Task OpenPaths(IEnumerable<string> paths){foreach(var file in paths){if(string.Equals(Path.GetExtension(file),".pdf",StringComparison.OrdinalIgnoreCase))await OpenDocument(file);}}
    async Task<PdfReaderView?> OpenDocument(string path,string? smoke=null,bool probe=false,string? features=null){
        if(closingBusy||closingApproved)return null;path=Path.GetFullPath(path);var existing=readers.FirstOrDefault(v=>string.Equals(v.RequestedFilePath,path,StringComparison.OrdinalIgnoreCase));if(existing!=null){SelectReader(existing);return existing;}
        if(!File.Exists(path)){ShowHome();homeStatus.Text="找不到这个文件，可能已移动或删除。可以移除旧记录，或重新选择文件。";RefreshRecent();return null;}
        var saved=recent.Find(path);var reader=new PdfReaderView(this,path,smoke,probe,features);readers.Add(reader);readerSurface.Children.Add(reader);SelectReader(reader);await reader.Ready;if(!readers.Contains(reader))return null;
        if(reader.FilePath==""){homeStatus.Text="这个 PDF 暂时无法打开，请检查文件是否有效。";readers.Remove(reader);readerSurface.Children.Remove(reader);reader.DisposeReader();ShowHome();return null;}
        if(!IsProbe&&saved!=null)await reader.RestoreReading(saved);ReaderChanged(reader);Remember(reader);return reader;
    }
    public void ReaderOpened(PdfReaderView reader){recent.Record(reader.ReadingState(),true);RefreshTabs();RefreshRecent();}
    public void ReaderChanged(PdfReaderView reader){if(!readers.Contains(reader))return;if(!dirtyMarks.TryGetValue(reader,out bool wasModified)||wasModified!=reader.IsModified)RefreshTabs();if(reader==active){saveButton.IsEnabled=reader.FilePath!=""&&!reader.IsSaving;Title=$"{Path.GetFileName(reader.FilePath)} · FullOffice";}}
    void Remember(PdfReaderView reader){if(reader.FilePath!="")recent.Record(reader.ReadingState(),false);}
    public void SelectReader(PdfReaderView reader){
        if(active==reader&&homeSurface.Visibility!=Visibility.Visible)return;
        if(active!=null){Remember(active);active.DeactivateReader();}
        active=reader;foreach(var item in readers)item.Visibility=item==reader?Visibility.Visible:Visibility.Collapsed;homeSurface.Visibility=Visibility.Collapsed;saveButton.IsEnabled=reader.FilePath!=""&&!reader.IsSaving;
        RefreshTabs();_ = ActivateSafely(reader);
    }
    async Task ActivateSafely(PdfReaderView reader){try{await reader.ActivateReader();}catch(OperationCanceledException){}catch(Exception ex){homeStatus.Text=ex.Message;}}
    void ShowHome(){if(active!=null){Remember(active);active.DeactivateReader();}active=null;foreach(var item in readers)item.Visibility=Visibility.Collapsed;homeSurface.Visibility=Visibility.Visible;saveButton.IsEnabled=false;Title="FullOffice · 开始阅读";RefreshTabs();RefreshRecent();}
    async Task<bool> CloseReader(PdfReaderView reader){if(!readers.Contains(reader)||!await reader.ConfirmClose()||!readers.Contains(reader))return false;int index=readers.IndexOf(reader);Remember(reader);closedFiles.Push(reader.RequestedFilePath);readers.Remove(reader);readerSurface.Children.Remove(reader);reader.DisposeReader();if(active==reader){active=null;if(readers.Count>0)SelectReader(readers[Math.Clamp(index-1,0,readers.Count-1)]);else ShowHome();}RefreshTabs();return true;}
    void RefreshTabs(){
        dirtyMarks.Clear();foreach(var view in readers)dirtyMarks[view]=view.IsModified;
        tabStrip.Children.Clear();var home=ActionButton("⌂  开始阅读",()=>{ShowHome();return Task.CompletedTask;});home.Margin=new(0,0,5,0);home.Background=active==null?Brushes.White:Brushes.Transparent;tabStrip.Children.Add(home);
        foreach(var reader in readers){var tab=new Border{CornerRadius=new(10,10,0,0),Background=reader==active?Brushes.White:Brushes.Transparent,BorderBrush=new SolidColorBrush(Color.FromRgb(225,231,243)),BorderThickness=reader==active?new Thickness(1,1,1,0):new Thickness(0),Margin=new(0,0,4,0),Padding=new(7,0,2,0),MinWidth=128,MaxWidth=236,ToolTip=reader.FilePath==""?"正在打开…":reader.FilePath,AllowDrop=true};
            var row=new DockPanel();var close=ActionButton("×",async()=>await CloseReader(reader));close.Padding=new(7,4,7,4);close.Margin=new(2,0,0,0);close.ToolTip="关闭标签页 · Ctrl+W";DockPanel.SetDock(close,Dock.Right);row.Children.Add(close);var title=new TextBlock{Text=(reader.IsModified?"●  ":"")+(reader.FilePath==""?"正在打开…":Path.GetFileNameWithoutExtension(reader.FilePath)),TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,7,0),FontWeight=reader==active?FontWeights.SemiBold:FontWeights.Normal};row.Children.Add(title);tab.Child=row;tabStrip.Children.Add(tab);
            tab.MouseLeftButtonDown+=(_,e)=>{if(e.OriginalSource is not Button)SelectReader(reader);};tab.MouseDown+=async(_,e)=>{if(e.ChangedButton==MouseButton.Middle){e.Handled=true;await CloseReader(reader);}};
            Point start=default;tab.PreviewMouseLeftButtonDown+=(_,e)=>start=e.GetPosition(tab);tab.MouseMove+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed&&Math.Abs(e.GetPosition(tab).X-start.X)>SystemParameters.MinimumHorizontalDragDistance){DragDrop.DoDragDrop(tab,new DataObject("FlowOfficeTab",reader),DragDropEffects.Move);}};
            tab.Drop+=(_,e)=>{if(e.Data.GetData("FlowOfficeTab") is PdfReaderView moving&&moving!=reader){readers.Remove(moving);readers.Insert(readers.IndexOf(reader),moving);RefreshTabs();e.Handled=true;}};
            var menu=new ContextMenu();void Add(string label,Func<Task> action){var item=new MenuItem{Header=label};item.Click+=async(_,_)=>await action();menu.Items.Add(item);}Add("关闭标签页",async()=>await CloseReader(reader));Add("关闭其他标签页",async()=>{foreach(var other in readers.Where(v=>v!=reader).ToArray())if(!await CloseReader(other))break;});Add("关闭右侧标签页",async()=>{foreach(var other in readers.Skip(readers.IndexOf(reader)+1).ToArray())if(!await CloseReader(other))break;});tab.ContextMenu=menu;
        }
    }
    async void OnWorkspaceKey(object sender,KeyEventArgs e){
        if(!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))return;
        try{if(e.Key==Key.O){e.Handled=true;await OpenFilesDialog();}else if(e.Key==Key.S&&active!=null){e.Handled=true;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))await active.ExportCopyFromShell();else await active.SaveFromShell();}else if(e.Key==Key.W&&active!=null){e.Handled=true;await CloseReader(active);}else if(e.Key==Key.T){e.Handled=true;if(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&closedFiles.TryPop(out var file))await OpenDocument(file);else ShowHome();}else if(e.Key==Key.Tab&&readers.Count>0){e.Handled=true;int i=active==null?-1:readers.IndexOf(active),direction=Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)?-1:1;SelectReader(readers[(i+direction+readers.Count)%readers.Count]);}else if(e.Key>=Key.D1&&e.Key<=Key.D9){int i=e.Key-Key.D1;if(i<readers.Count){e.Handled=true;SelectReader(readers[i]);}}}catch(Exception ex){homeStatus.Text=ex.Message;}
    }
    void BuildHome(){
        var scroller=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};homeSurface.Child=scroller;
        var content=new StackPanel{Margin=new(56,40,56,28),MaxWidth=1060,HorizontalAlignment=HorizontalAlignment.Stretch};scroller.Content=content;
        var welcome=new Border{CornerRadius=new(22),Background=Brushes.White,Padding=new(30,25,30,25),BorderBrush=new SolidColorBrush(Color.FromRgb(229,234,244)),BorderThickness=new(1)};content.Children.Add(welcome);var hero=new DockPanel();welcome.Child=hero;var open=ActionButton("打开 PDF  ＋",OpenFilesDialog);open.Background=new SolidColorBrush(Color.FromRgb(67,99,224));open.Foreground=Brushes.White;open.Padding=new(24,13,24,13);open.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(open,Dock.Right);hero.Children.Add(open);var heading=new StackPanel();heading.Children.Add(new TextBlock{Text="开始阅读",FontSize=29,FontWeight=FontWeights.SemiBold});heading.Children.Add(new TextBlock{Text="地图、正文和笔记，随时切换查看",Foreground=Brushes.SlateGray,FontSize=14,Margin=new(0,12,0,0)});hero.Children.Add(heading);
        var tools=new DockPanel{Margin=new(0,30,0,16)};content.Children.Add(tools);var clear=ActionButton("清空记录",()=>{recent.Clear();RefreshRecent();return Task.CompletedTask;});DockPanel.SetDock(clear,Dock.Right);tools.Children.Add(clear);DockPanel.SetDock(recentSearch,Dock.Right);tools.Children.Add(recentSearch);recentSearch.ToolTip="按文件名或文件夹筛选";recentSearch.TextChanged+=(_,_)=>RefreshRecent();tools.Children.Add(new TextBlock{Text="最近打开",FontSize=20,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center});content.Children.Add(recentRows);content.Children.Add(homeStatus);homeStatus.Text=recent.Error??"";RefreshRecent();
        var hint=new Border{Margin=new(0,26,0,0),Padding=new(20,16,20,16),CornerRadius=new(14),Background=new SolidColorBrush(Color.FromRgb(234,240,253))};hint.Child=new TextBlock{Text="普通右键：完整菜单　 ·　 按住右键拖出死区：轮盘　 ·　 松开执行，回到中心取消",Foreground=new SolidColorBrush(Color.FromRgb(77,96,141)),TextWrapping=TextWrapping.Wrap};content.Children.Add(hint);
    }
    void RefreshRecent(){
        recentRows.Children.Clear();string filter=recentSearch.Text.Trim();var items=recent.Items.Where(i=>filter==""||i.Path.Contains(filter,StringComparison.OrdinalIgnoreCase)).ToList();
        if(items.Count==0){recentRows.Children.Add(new TextBlock{Text=recent.Items.Count==0?"还没有最近打开的文件。选择文件或拖入 PDF，即可开始。":"没有找到匹配的记录。",Foreground=Brushes.SlateGray,Margin=new(4,18,4,18)});return;}
        foreach(var entry in items){bool exists=File.Exists(entry.Path);var row=new Border{Background=Brushes.White,CornerRadius=new(13),Padding=new(18,13,15,13),Margin=new(0,0,0,9),BorderBrush=new SolidColorBrush(Color.FromRgb(231,235,244)),BorderThickness=new(1),Cursor=Cursors.Hand};var grid=new DockPanel();row.Child=grid;var remove=ActionButton("×",()=>{recent.Remove(entry.Path);RefreshRecent();return Task.CompletedTask;});remove.ToolTip="移除记录，保留文件";DockPanel.SetDock(remove,Dock.Right);grid.Children.Add(remove);var date=new TextBlock{Text=entry.Opened.ToOffset(TimeSpan.FromHours(8)).ToString("MM-dd HH:mm"),Foreground=Brushes.SlateGray,VerticalAlignment=VerticalAlignment.Center,Margin=new(18,0,8,0)};DockPanel.SetDock(date,Dock.Right);grid.Children.Add(date);var badge=new Border{Width=43,Height=46,CornerRadius=new(10),Background=new SolidColorBrush(Color.FromRgb(235,240,255)),Margin=new(0,0,16,0),Child=new TextBlock{Text="PDF",FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Color.FromRgb(67,99,224)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};DockPanel.SetDock(badge,Dock.Left);grid.Children.Add(badge);var labels=new StackPanel{VerticalAlignment=VerticalAlignment.Center};labels.Children.Add(new TextBlock{Text=Path.GetFileNameWithoutExtension(entry.Path),FontSize=15,FontWeight=FontWeights.SemiBold,TextTrimming=TextTrimming.CharacterEllipsis,Foreground=exists?Foreground:Brushes.SlateGray});labels.Children.Add(new TextBlock{Text=exists?$"第 {entry.Page+1} 页  ·  {Path.GetDirectoryName(entry.Path)}":"文件已移动或删除",Foreground=Brushes.SlateGray,TextTrimming=TextTrimming.CharacterEllipsis,Margin=new(0,5,0,0),ToolTip=entry.Path});grid.Children.Add(labels);recentRows.Children.Add(row);row.MouseLeftButtonUp+=async(_,e)=>{if(e.OriginalSource is Button)return;await OpenDocument(entry.Path);};var menu=new ContextMenu();var forget=new MenuItem{Header="移除记录，保留文件"};forget.Click+=(_,_)=>{recent.Remove(entry.Path);RefreshRecent();};menu.Items.Add(forget);row.ContextMenu=menu;
        }
    }
}
