using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Collections.ObjectModel;

namespace FlowOfficeLab;

public sealed class SmoothList : Grid
{
    public ListBox List { get; } = new() { Background=Brushes.Transparent, BorderThickness=new(0), HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly Canvas overlay=new(){IsHitTestVisible=false};
    readonly Border follower=new(){CornerRadius=new(7),Background=new SolidColorBrush(Color.FromArgb(24,63,100,216)),Opacity=0};
    readonly ObservableCollection<object> visibleItems=new();
    public event Action? ExpansionChanged;
    object[] allItems=Array.Empty<object>();
    ListBoxItem? hovered;
    public const double RowHeight=28;
    public bool IsUpdating {get;private set;}
    public SmoothList(){
        ClipToBounds=true;overlay.Children.Add(follower);Children.Add(overlay);Children.Add(List);List.ItemsSource=visibleItems;
        ScrollViewer.SetHorizontalScrollBarVisibility(List,ScrollBarVisibility.Disabled);
        ScrollViewer.SetCanContentScroll(List,true);
        VirtualizingStackPanel.SetIsVirtualizing(List,true);VirtualizingStackPanel.SetVirtualizationMode(List,VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(List,ScrollUnit.Pixel);
        var textTemplate=new DataTemplate();var row=new FrameworkElementFactory(typeof(NavigationRow));textTemplate.VisualTree=row;List.ItemTemplate=textTemplate;
        var style=new Style(typeof(ListBoxItem));style.Setters.Add(new Setter(HeightProperty,RowHeight));style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        var template=new ControlTemplate(typeof(ListBoxItem));var surface=new FrameworkElementFactory(typeof(Border));surface.Name="selectedSurface";surface.SetValue(Border.CornerRadiusProperty,new CornerRadius(7));surface.SetValue(FrameworkElement.MarginProperty,new Thickness(3,1,3,1));surface.SetValue(Border.BackgroundProperty,Brushes.Transparent);
        var presenter=new FrameworkElementFactory(typeof(ContentPresenter));presenter.SetValue(FrameworkElement.MarginProperty,new Thickness(5,0,6,0));presenter.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);surface.AppendChild(presenter);template.VisualTree=surface;
        var selected=new Trigger{Property=ListBoxItem.IsSelectedProperty,Value=true};selected.Setters.Add(new Setter(Border.BackgroundProperty,new SolidColorBrush(Color.FromArgb(38,65,105,225)),"selectedSurface"));selected.Setters.Add(new Setter(Control.ForegroundProperty,new SolidColorBrush(Color.FromRgb(42,78,178))));template.Triggers.Add(selected);style.Setters.Add(new Setter(Control.TemplateProperty,template));List.ItemContainerStyle=style;
        List.SelectionChanged+=(_,_)=>{MoveTo(List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem);Dispatcher.BeginInvoke(()=>MoveTo(List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem),DispatcherPriority.Render);};
        List.PreviewMouseMove+=(_,e)=>{DependencyObject? node=e.OriginalSource as DependencyObject;while(node!=null&&node is not ListBoxItem)node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);if(node is ListBoxItem item&&item!=hovered){hovered=item;MoveTo(item);}};
        List.MouseLeave+=(_,_)=>{hovered=null;MoveTo(List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem);};
        List.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>{hovered=null;MoveTo(List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem);}));
        SizeChanged+=(_,_)=>MoveTo(List.ItemContainerGenerator.ContainerFromItem(List.SelectedItem) as ListBoxItem);
    }
    public void SetItems(IEnumerable<object> items){allItems=items.ToArray();Rebuild();}
    void Rebuild(){
        IsUpdating=true;try{object? selected=List.SelectedItem;var visible=new List<object>();int hiddenDepth=int.MaxValue;
        foreach(var item in allItems){if(item is Bookmark b){if(b.Depth>hiddenDepth)continue;hiddenDepth=int.MaxValue;visible.Add(b);if(!b.Expanded)hiddenDepth=b.Depth;}else visible.Add(item);}
        var wanted=visible.ToHashSet();for(int i=visibleItems.Count-1;i>=0;i--)if(!wanted.Contains(visibleItems[i]))visibleItems.RemoveAt(i);
        for(int i=0;i<visible.Count;i++){if(i<visibleItems.Count&&ReferenceEquals(visibleItems[i],visible[i]))continue;int old=visibleItems.IndexOf(visible[i]);if(old>=0)visibleItems.Move(old,i);else visibleItems.Insert(i,visible[i]);}
        if(selected is Bookmark bookmark){var same=visible.OfType<Bookmark>().FirstOrDefault(b=>b.Id==bookmark.Id);if(same==null){int index=Array.IndexOf(allItems,bookmark),depth=bookmark.Depth;for(int i=index-1;i>=0;i--)if(allItems[i] is Bookmark ancestor&&ancestor.Depth<depth){if(visible.Contains(ancestor)){same=ancestor;break;}depth=ancestor.Depth;}}List.SelectedItem=same;}
        else if(selected!=null&&visible.Contains(selected))List.SelectedItem=selected;
        foreach(var row in RealizedRows(List))row.Refresh();
        }finally{IsUpdating=false;}
    }
    static IEnumerable<NavigationRow> RealizedRows(DependencyObject root){for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is NavigationRow row)yield return row;else foreach(var descendant in RealizedRows(child))yield return descendant;}}
    public bool IsExpanded(Bookmark item)=>item.Expanded;
    public void Toggle(Bookmark item){if(!item.HasChildren)return;item.Expanded=!item.Expanded;Rebuild();ExpansionChanged?.Invoke();}
    public void ExpandAll(bool expanded){foreach(var b in allItems.OfType<Bookmark>())b.Expanded=expanded;Rebuild();ExpansionChanged?.Invoke();}
    public void ExpandToLevel(int level){foreach(var b in allItems.OfType<Bookmark>())b.Expanded=b.Depth<level-1;Rebuild();ExpansionChanged?.Invoke();}
    public void ExpandBranch(Bookmark item,bool expanded){int index=Array.IndexOf(allItems,item);if(index<0)return;item.Expanded=expanded;for(int i=index+1;i<allItems.Length&&allItems[i] is Bookmark b&&b.Depth>item.Depth;i++)b.Expanded=expanded;Rebuild();ExpansionChanged?.Invoke();}
    public void Reveal(Bookmark item){
        int index=Array.IndexOf(allItems,item),depth=item.Depth;bool changed=false;
        for(int i=index-1;i>=0;i--)if(allItems[i] is Bookmark parent&&parent.Depth<depth){changed|=!parent.Expanded;parent.Expanded=true;depth=parent.Depth;}
        if(changed)Rebuild();
    }
    void MoveTo(ListBoxItem? item){
        if(item==null||!item.IsVisible||item.ActualWidth<=0){follower.Opacity=0;return;}
        var point=item.TranslatePoint(new Point(3,1),this);follower.Opacity=1;
        Animate(follower,Canvas.LeftProperty,point.X);Animate(follower,Canvas.TopProperty,point.Y);Animate(follower,WidthProperty,Math.Max(0,item.ActualWidth-6));Animate(follower,HeightProperty,item.ActualHeight-2);
    }
    public static void Animate(DependencyObject target,DependencyProperty property,double value,int milliseconds=130)=>SmoothMotion.To(target,property,value,milliseconds);
}

public sealed class NavigationRow : Grid
{
    public NavigationRow(){DataContextChanged+=(_,_)=>Build();Loaded+=(_,_)=>Build();}
    public void Refresh()=>Build();
    SmoothList? Owner(){DependencyObject? node=this;while(node!=null&&node is not SmoothList)node=VisualTreeHelper.GetParent(node);return node as SmoothList;}
    void Build(){
        Children.Clear();ColumnDefinitions.Clear();var bookmark=DataContext as Bookmark;var annotation=DataContext as Annotation;if(bookmark==null&&annotation==null)return;
        int depth=bookmark?.Depth??0;double indentation=Math.Min(depth,9)*15;
        ColumnDefinitions.Add(new(){Width=new GridLength(indentation+20)});ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        var guides=new Canvas{Height=26,IsHitTestVisible=false};for(int level=0;level<Math.Min(depth,9);level++){var line=new Border{Width=1,Height=28,Background=new SolidColorBrush(Color.FromRgb(207,215,229))};Canvas.SetLeft(line,level*15+7);guides.Children.Add(line);}Children.Add(guides);
        if(bookmark?.HasChildren==true){
            bool expanded=bookmark.Expanded;var toggle=new Button{Width=20,Height=25,Padding=new(0),Margin=new(0),Background=Brushes.Transparent,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,ToolTip=$"{(expanded?"折叠":"展开")} · 包含 {bookmark.DescendantCount} 个子书签",Focusable=false};
            var icon=new System.Windows.Shapes.Path{Data=Geometry.Parse(expanded?"M 3,5 L 7,9 L 11,5":"M 5,3 L 9,7 L 5,11"),Stroke=new SolidColorBrush(Color.FromRgb(85,105,141)),StrokeThickness=1.8,StrokeLineJoin=PenLineJoin.Round,Width=14,Height=14,Stretch=Stretch.None,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};toggle.Content=icon;
            System.Windows.Automation.AutomationProperties.SetName(toggle,expanded?"折叠书签":"展开书签");toggle.PreviewMouseLeftButtonDown+=(_,e)=>{e.Handled=true;toggle.CaptureMouse();};toggle.PreviewMouseLeftButtonUp+=(_,e)=>{e.Handled=true;bool inside=toggle.IsMouseOver;toggle.ReleaseMouseCapture();if(inside)Owner()?.Toggle(bookmark);};toggle.Click+=(_,e)=>{e.Handled=true;Owner()?.Toggle(bookmark);};Children.Add(toggle);
        }
        else if(depth>0){var branch=new Border{Height=1,Width=10,Background=new SolidColorBrush(Color.FromRgb(193,204,222)),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,0,4,0)};Children.Add(branch);}
        var text=new TextBlock{Text=bookmark?.Title??annotation!.Title,TextTrimming=TextTrimming.CharacterEllipsis,FontSize=12,FontWeight=bookmark?.Bold==true||depth==0?FontWeights.SemiBold:FontWeights.Normal,FontStyle=bookmark?.Italic==true?FontStyles.Italic:FontStyles.Normal,Foreground=bookmark?.TextBrush??Brushes.SlateGray,VerticalAlignment=VerticalAlignment.Center,ToolTip=bookmark==null?annotation!.Title:$"{bookmark.Title}\n{(bookmark.CanNavigate?$"第 {bookmark.Page+1} 页":"目录分组／非页内目标")}{(bookmark.HasChildren?$" · {bookmark.DescendantCount} 个子书签":"")}"};Grid.SetColumn(text,1);Children.Add(text);
        var trailing=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        if(bookmark?.HasChildren==true&&!bookmark.Expanded){var badge=new Border{CornerRadius=new(4),Background=new SolidColorBrush(Color.FromRgb(225,233,247)),Padding=new(4,1,4,1),Margin=new(5,0,0,0),Child=new TextBlock{Text=$"+{bookmark.DescendantCount}",FontSize=9,Foreground=new SolidColorBrush(Color.FromRgb(72,96,143))},ToolTip="已折叠的子书签数量"};trailing.Children.Add(badge);}
        trailing.Children.Add(new TextBlock{Text=bookmark?.CanNavigate==false?"—":((bookmark?.Page??annotation!.Page)+1).ToString(),FontSize=10,Foreground=new SolidColorBrush(Color.FromRgb(124,139,161)),Margin=new(7,0,0,0),VerticalAlignment=VerticalAlignment.Center});Grid.SetColumn(trailing,2);Children.Add(trailing);
    }
}
