using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace FlowOfficeLab;

public sealed record BookmarkEdit(string Title,int Page,double Horizontal,double Vertical,string? Color,bool Bold,bool Italic,bool ChangeDestination);

public sealed class BookmarkDialog : Window
{
    readonly TextBox name=new(),page=new(),horizontal=new(),vertical=new(),color=new();
    readonly CheckBox bold=new(){Content="加粗"},italic=new(){Content="斜体"},changeDestination=new(){Content="修改跳转位置"};
    public BookmarkEdit? Result {get;private set;}
    public BookmarkDialog(Window owner,Bookmark bookmark,int pageCount,System.Windows.Point position,bool creating){
        Owner=owner;Icon=owner.Icon;Title=creating?"添加书签":"书签属性";Width=450;Height=450;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=owner.FontFamily;FontSize=13;
        var panel=new StackPanel{Margin=new(22)};Content=panel;
        void Field(string label,TextBox box,string value){panel.Children.Add(new TextBlock{Text=label,Margin=new(0,8,0,4)});box.Text=value;box.Padding=new(8,5,8,5);panel.Children.Add(box);}
        Field("名称",name,bookmark.Title);Field($"页码（1～{pageCount}）",page,Math.Max(1,bookmark.Page+1).ToString());
        var offsets=new Grid{Margin=new(0,10,0,0)};offsets.ColumnDefinitions.Add(new());offsets.ColumnDefinitions.Add(new());
        var left=new StackPanel{Margin=new(0,0,8,0)};var right=new StackPanel{Margin=new(8,0,0,0)};offsets.Children.Add(left);Grid.SetColumn(right,1);offsets.Children.Add(right);
        left.Children.Add(new TextBlock{Text="距页面左侧（%）"});left.Children.Add(horizontal);right.Children.Add(new TextBlock{Text="距页面顶部（%）"});right.Children.Add(vertical);horizontal.Text=(position.X*100).ToString("0.##",CultureInfo.CurrentCulture);vertical.Text=(position.Y*100).ToString("0.##",CultureInfo.CurrentCulture);horizontal.Padding=vertical.Padding=new(8,5,8,5);panel.Children.Add(offsets);
        changeDestination.IsChecked=creating;changeDestination.Visibility=creating?Visibility.Collapsed:Visibility.Visible;changeDestination.Margin=new(0,10,0,0);panel.Children.Add(changeDestination);
        page.IsEnabled=horizontal.IsEnabled=vertical.IsEnabled=creating;changeDestination.Checked+=(_,_)=>page.IsEnabled=horizontal.IsEnabled=vertical.IsEnabled=true;changeDestination.Unchecked+=(_,_)=>page.IsEnabled=horizontal.IsEnabled=vertical.IsEnabled=false;
        Field("颜色（#RRGGBB，留空使用默认颜色）",color,bookmark.ColorHex??"");var styles=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,12,0,0)};bold.IsChecked=bookmark.Bold;italic.IsChecked=bookmark.Italic;italic.Margin=new(20,0,0,0);styles.Children.Add(bold);styles.Children.Add(italic);panel.Children.Add(styles);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,17,0,0)};panel.Children.Add(buttons);var cancel=new Button{Content="取消",IsCancel=true,Padding=new(14,7,14,7)};var ok=new Button{Content=creating?"添加":"保存",IsDefault=true,Padding=new(14,7,14,7),Margin=new(8,0,0,0)};buttons.Children.Add(cancel);buttons.Children.Add(ok);
        System.Windows.Automation.AutomationProperties.SetAutomationId(name,"BookmarkTitle");System.Windows.Automation.AutomationProperties.SetAutomationId(page,"BookmarkPage");System.Windows.Automation.AutomationProperties.SetAutomationId(color,"BookmarkColor");
        ok.Click+=(_,_)=>{
            bool changed=changeDestination.IsChecked==true;
            if(string.IsNullOrWhiteSpace(name.Text)){MessageBox.Show(this,"请输入书签名称。");return;}
            if(!int.TryParse(page.Text,out int pageValue)||pageValue<1||pageValue>pageCount||!double.TryParse(horizontal.Text,out double x)||!double.IsFinite(x)||x<0||x>100||!double.TryParse(vertical.Text,out double y)||!double.IsFinite(y)||y<0||y>100){MessageBox.Show(this,"请填写有效页码，以及 0～100 的位置百分比。");return;}
            string? hex=string.IsNullOrWhiteSpace(color.Text)?null:color.Text.Trim().ToUpperInvariant();if(hex!=null&&!System.Text.RegularExpressions.Regex.IsMatch(hex,"^#[0-9A-F]{6}$")){MessageBox.Show(this,"颜色格式应为 #RRGGBB，例如 #E85D75。");return;}
            Result=new(name.Text.Trim(),changed?pageValue-1:bookmark.Page,x/100,y/100,hex,bold.IsChecked==true,italic.IsChecked==true,changed);DialogResult=true;
        };
        Loaded+=(_,_)=>{name.Focus();name.SelectAll();};
    }
}
