using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    readonly List<PageTag> pageTags=new();
    readonly Canvas pageTagCanvas=new(){ClipToBounds=true,Visibility=Visibility.Collapsed};
    readonly StackPanel tagRows=new();
    readonly ScrollViewer tagIndex=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};
    readonly Dictionary<Guid,Button> stickyButtons=new();
    PageTag[]? stickyTags;
    PageTag[] orderedStickyTags=Array.Empty<PageTag>();
    Guid? expandedTag;
    int tagScrollStart;
    bool tagTab;
    PageTag[]? indexedTags;
    static Style TagStyle(){var style=new Style(typeof(Button));var template=new ControlTemplate(typeof(Button));var surface=new FrameworkElementFactory(typeof(Border));surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(4,10,10,4));surface.SetBinding(Border.BorderBrushProperty,new System.Windows.Data.Binding("BorderBrush"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});surface.SetBinding(Border.BorderThicknessProperty,new System.Windows.Data.Binding("BorderThickness"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});surface.SetBinding(Border.BackgroundProperty,new System.Windows.Data.Binding("Background"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});var content=new FrameworkElementFactory(typeof(ContentPresenter));content.SetBinding(FrameworkElement.MarginProperty,new System.Windows.Data.Binding("Padding"){RelativeSource=new(System.Windows.Data.RelativeSourceMode.TemplatedParent)});content.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);surface.AppendChild(content);template.VisualTree=surface;style.Setters.Add(new Setter(Control.TemplateProperty,template));return style;}
    double TagWidth(int page)=>0;
    void InitializePageTags(Grid body,Grid sidebar){tagIndex.Content=tagRows;Grid.SetRow(tagIndex,3);sidebar.Children.Add(tagIndex);Grid.SetColumn(pageTagCanvas,1);Panel.SetZIndex(pageTagCanvas,20);body.Children.Add(pageTagCanvas);scroll.ScrollChanged+=(_,_)=>PositionStickyTags();scroll.SizeChanged+=(_,_)=>PositionStickyTags();pageImage.SizeChanged+=(_,_)=>PositionStickyTags();pageTagCanvas.SizeChanged+=(_,_)=>PositionStickyTags();}
    void UpdateTagDisplays(){
        if(stickyTags==null||!stickyTags.SequenceEqual(pageTags)){stickyTags=pageTags.ToArray();orderedStickyTags=pageTags.OrderBy(t=>t.Page).ThenBy(t=>t.Y).ToArray();stickyButtons.Clear();pageTagCanvas.Children.Clear();foreach(var tag in orderedStickyTags){var button=TagButton(tag);button.Height=28;button.MouseEnter+=(_,_)=>{expandedTag=tag.Id;PositionStickyTags(true);};button.MouseLeave+=(_,_)=>{if(expandedTag==tag.Id){expandedTag=null;PositionStickyTags(true);}};button.PreviewMouseWheel+=async(_,e)=>{e.Handled=true;int capacity=StickyCapacity();if(pageTags.Count>capacity){tagScrollStart=Math.Clamp(tagScrollStart+(e.Delta<0?3:-3),0,Math.Max(0,pageTags.Count-capacity));PositionStickyTags();}else await HandleWheel(e.Delta,Keyboard.Modifiers.HasFlag(ModifierKeys.Control));};stickyButtons[tag.Id]=button;pageTagCanvas.Children.Add(button);}}
        PositionStickyTags();
        if(indexedTags==null||!indexedTags.SequenceEqual(pageTags)){indexedTags=pageTags.ToArray();tagRows.Children.Clear();foreach(var tag in pageTags.OrderBy(t=>t.Page).ThenBy(t=>t.Y)){var button=TagButton(tag);button.Margin=new(5,3,5,3);button.HorizontalAlignment=HorizontalAlignment.Stretch;tagRows.Children.Add(button);}if(pageTags.Count==0)tagRows.Children.Add(new TextBlock{Text="还没有彩色标签",Foreground=Brushes.SlateGray,Margin=new(10,16,10,0)});}
        foreach(var button in tagRows.Children.OfType<ColorTagButton>())button.SetSelected(button.Tag is PageTag tag&&tag.Page==currentPage);
        tagIndex.Visibility=tagTab?Visibility.Visible:Visibility.Collapsed;navigation.Visibility=tagTab?Visibility.Collapsed:Visibility.Visible;emptyNavigation.Visibility=tagTab?Visibility.Collapsed:(annotationTab?annotations.Count:bookmarks.Count)==0?Visibility.Visible:Visibility.Collapsed;
    }
    Button TagButton(PageTag tag){var button=new ColorTagButton{Tag=tag,Style=TagStyle(),Padding=new(9,5,9,5),Margin=new(0),HorizontalContentAlignment=HorizontalAlignment.Left,Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(tag.Color)),Foreground=Brushes.White,ToolTip=$"{tag.Title} · 第 {tag.Page+1} 页",Content=new TextBlock{Text=$"{tag.Title} · {tag.Page+1}",TextTrimming=TextTrimming.CharacterEllipsis,FontSize=12}};button.Click+=async(_,_)=>{button.Pulse();await GoToTag(tag);};var menu=new ContextMenu();menu.Items.Add(Menu("跳转到标签",()=>GoToTag(tag)));menu.Items.Add(Menu("修改标签…",()=>EditPageTag(tag),CanEditDocument));menu.Items.Add(ActionMenu("删除标签",()=>{if(!CanEditDocument)return;pageTags.RemoveAll(t=>t.Id==tag.Id);TagsChanged();},CanEditDocument));menu.Opened+=(_,_)=>{foreach(var item in menu.Items.OfType<MenuItem>().Skip(1))item.IsEnabled=CanEditDocument;};button.ContextMenu=menu;button.SetSelected(tag.Page==currentPage);return button;}
    async Task GoToTag(PageTag tag){await ShowPage(tag.Page,false,true,true);if(displayedPage==tag.Page)SmoothScroll.To(scroll,Math.Max(0,tag.Y*pageImage.Height+8));}
    void TagsChanged(){engine.SetPageTags(pageTags);UpdateTagDisplays();Host.ReaderChanged(this);status.Text="标签已更新，记得保存";if(isActiveView)_=ShowPage(currentPage,false,true,true);}
    Task AddPageTag(Point position){if(!CanEditDocument)return Task.CompletedTask;var color=TagColors.Next(pageTags.Select(t=>t.Color));var draft=new PageTag(Guid.NewGuid(),"",currentPage,color,Math.Clamp(position.Y,0,1));var dialog=new PageTagDialog(Host,draft,pageTags.Select(t=>t.Color));if(dialog.ShowDialog()==true&&dialog.Result!=null){var result=dialog.Result;if(pageTags.Any(t=>t.Color==result.Color))result=result with{Color=TagColors.Next(pageTags.Select(t=>t.Color))};pageTags.Add(result);TagsChanged();}return Task.CompletedTask;}
    Task EditPageTag(PageTag tag){if(!CanEditDocument)return Task.CompletedTask;var dialog=new PageTagDialog(Host,tag,pageTags.Where(t=>t.Id!=tag.Id).Select(t=>t.Color));if(dialog.ShowDialog()==true&&dialog.Result!=null){int i=pageTags.FindIndex(t=>t.Id==tag.Id);if(i>=0){pageTags[i]=dialog.Result;TagsChanged();}}return Task.CompletedTask;}
    async Task TagTopLevelBookmarks(){
        if(!CanEditDocument)return;int doc=generation;var pending=new List<(string Title,int Page,double Y)>();
        foreach(var root in bookmarks.Where(b=>b.Depth==0).ToArray()){
            int i=bookmarks.IndexOf(root),end=BookmarkTree.End(bookmarks,i);var destination=root.CanNavigate?root:bookmarks.Skip(i+1).Take(end-i-1).FirstOrDefault(b=>b.CanNavigate);if(destination==null)continue;
            var point=await engine.LocateBookmarkAsync(destination,documentCancellation.Token);if(doc!=generation||!CanEditDocument)return;pending.Add((root.Title,destination.Page,point?.Y??0));
        }
        int added=0;var used=pageTags.Select(t=>t.Color).ToList();foreach(var item in pending){if(pageTags.Any(t=>t.Page==item.Page&&t.Title==item.Title))continue;var color=TagColors.Next(used);used.Add(color);pageTags.Add(new(Guid.NewGuid(),item.Title,item.Page,color,item.Y));added++;}
        if(added>0)TagsChanged();status.Text=added>0?$"已按最高层书签添加 {added} 个标签，记得保存":"没有需要新增的最高层标签";
    }
    int StickyCapacity()=>Math.Max(1,(int)Math.Floor((scroll.ActualHeight-20)/32));
    void PositionStickyTags(bool animate=false){
        if(pageTagCanvas.Parent==null||scroll.ActualWidth<=0)return;pageTagCanvas.Visibility=pageTags.Count>0?Visibility.Visible:Visibility.Collapsed;
        double right=scroll.ActualWidth-4-(scroll.ComputedVerticalScrollBarVisibility==Visibility.Visible?SystemParameters.VerticalScrollBarWidth:0),paperRight=right-20;
        if(pageImage.IsLoaded&&pageImage.ActualWidth>0)paperRight=pageImage.TranslatePoint(new Point(pageImage.ActualWidth,0),scroll).X+2;
        double left=Math.Clamp(paperRight,0,Math.Max(0,right-20)),normalWidth=Math.Clamp(right-left,20,120);
        int capacity=StickyCapacity();tagScrollStart=Math.Clamp(tagScrollStart,0,Math.Max(0,pageTags.Count-capacity));var ordered=orderedStickyTags;
        for(int i=0;i<ordered.Length;i++){var tag=ordered[i];if(!stickyButtons.TryGetValue(tag.Id,out var button))continue;int row=i-tagScrollStart;button.Visibility=row>=0&&row<capacity?Visibility.Visible:Visibility.Collapsed;bool expanded=expandedTag==tag.Id;double width=expanded?120:normalWidth;((ColorTagButton)button).Place(width,Math.Max(0,left+normalWidth-width),10+row*32,animate);if(button.Content is TextBlock text){text.Visibility=width>=72?Visibility.Visible:Visibility.Collapsed;text.FontWeight=tag.Page==currentPage?FontWeights.SemiBold:FontWeights.Normal;}((ColorTagButton)button).SetSelected(tag.Page==currentPage);}
    }
}

public sealed partial class PdfEngine
{
    IReadOnlyList<PageTag> currentPageTags=Array.Empty<PageTag>();
    bool tagModified,writeTags;
    public void SetPageTags(IReadOnlyList<PageTag> tags,bool modified=true){currentPageTags=tags;if(modified){tagModified=true;writeTags=true;}}
}
