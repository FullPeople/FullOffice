using System.Windows;
using System.Windows.Controls;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    Grid? documentBody,bookmarkSidebar;
    readonly Border collapsedSidebar=new(){Visibility=Visibility.Collapsed};
    bool sidebarCollapsed;
    void InitializeBookmarkSidebar(Grid body,Grid sidebar){documentBody=body;bookmarkSidebar=sidebar;var compact=new StackPanel{Margin=new(0,8,0,0)};var reveal=Button("›",()=>{SetSidebarCollapsed(false);return Task.CompletedTask;});reveal.Padding=new(6,5,6,5);reveal.Margin=new(2);reveal.ToolTip="展开书签栏";compact.Children.Add(reveal);collapsedSidebar.Child=compact;body.Children.Add(collapsedSidebar);
        var tools=new StackPanel{Orientation=Orientation.Horizontal};Grid.SetRow(tools,2);sidebar.Children.Add(tools);
        Button Small(string title,Action action){var b=Button(title,()=>{action();return Task.CompletedTask;});b.Padding=new(6,4,6,4);b.Margin=new(1);tools.Children.Add(b);return b;}
        Small("折叠",()=>{tagTab=false;annotationTab=false;RefreshNavigation();navigation.ExpandAll(false);});
        var levels=Small("展开 ▾",()=>{});levels.Click+=(_,_)=>{var menu=new ContextMenu();int maximum=bookmarks.Count==0?1:bookmarks.Max(b=>b.Depth)+1;for(int level=1;level<=maximum;level++){int target=level;menu.Items.Add(ActionMenu($"展开到第 {level} 层",()=>{tagTab=false;annotationTab=false;RefreshNavigation();navigation.ExpandToLevel(target);}));}menu.Items.Add(new Separator());menu.Items.Add(ActionMenu("展开全部",()=>{tagTab=false;annotationTab=false;RefreshNavigation();navigation.ExpandAll(true);}));menu.PlacementTarget=levels;menu.IsOpen=true;};
        Small("收起",()=>SetSidebarCollapsed(true));var tag=Button("打标",TagTopLevelBookmarks);tag.Padding=new(6,4,6,4);tag.Margin=new(1);tag.ToolTip="按最高层书签添加彩色标签";tools.Children.Add(tag);
    }
    void SetSidebarCollapsed(bool collapsed){sidebarCollapsed=collapsed;if(documentBody==null||bookmarkSidebar==null)return;documentBody.ColumnDefinitions[0].Width=new GridLength(collapsed?36:270);bookmarkSidebar.Visibility=collapsed?Visibility.Collapsed:Visibility.Visible;collapsedSidebar.Visibility=collapsed?Visibility.Visible:Visibility.Collapsed;}
}
