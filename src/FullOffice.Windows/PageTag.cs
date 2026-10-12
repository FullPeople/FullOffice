using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FlowOfficeLab;

public sealed record PageTag(Guid Id,string Title,int Page,string Color,double Y);
public sealed record DocumentMetadata(List<OutlineAppearance> Outlines,List<PageTag> Tags);
public static class TagColors
{
    static readonly string[] choices={"#D94262","#C76D18","#27855D","#326DD1","#8151C8","#975132","#297E8F","#8F397A","#557934","#4D5CAD","#A3503F","#735898"};
    public static string Next(IEnumerable<string> used){var taken=used.ToHashSet(StringComparer.OrdinalIgnoreCase);var available=choices.Where(c=>!taken.Contains(c)).ToArray();if(available.Length>0)return available[Random.Shared.Next(available.Length)];
        for(int attempt=0;attempt<20000;attempt++){double h=Random.Shared.NextDouble()*6,s=.6+Random.Shared.NextDouble()*.25,v=.48+Random.Shared.NextDouble()*.22;int sector=(int)h;double f=h-sector,p=v*(1-s),q=v*(1-f*s),t=v*(1-(1-f)*s);var (r,g,b)=sector switch{0=>(v,t,p),1=>(q,v,p),2=>(p,v,t),3=>(p,q,v),4=>(t,p,v),_=>(v,p,q)};string hex=$"#{(int)(r*255):X2}{(int)(g*255):X2}{(int)(b*255):X2}";if(!taken.Contains(hex))return hex;}throw new IOException("暂时无法生成新颜色，请删除一个不再使用的标签。");
    }
    public static List<PageTag> Read(string json,int pages){if(string.IsNullOrWhiteSpace(json))return new();var items=JsonSerializer.Deserialize<List<PageTag>>(json)??new();return items.Where(t=>t.Page>=0&&t.Page<pages&&!string.IsNullOrWhiteSpace(t.Title)&&Regex.IsMatch(t.Color??"","^#[0-9a-fA-F]{6}$")&&double.IsFinite(t.Y)).Take(20000).DistinctBy(t=>t.Id).Select(t=>t with{Y=Math.Clamp(t.Y,0,1),Color=t.Color.ToUpperInvariant()}).ToList();}
}
public sealed class PageTagDialog : Window
{
    public PageTag? Result {get;private set;}
    public PageTagDialog(Window owner,PageTag draft,IEnumerable<string> used){Owner=owner;Icon=owner.Icon;Title="彩色标签";Width=390;Height=230;ResizeMode=ResizeMode.NoResize;WindowStartupLocation=WindowStartupLocation.CenterOwner;FontFamily=owner.FontFamily;FontSize=13;var usedColors=used.ToArray();var selected=draft.Color;
        var root=new StackPanel{Margin=new(22)};Content=root;root.Children.Add(new TextBlock{Text="标签文字",Margin=new(0,0,0,6)});var title=new TextBox{Text=draft.Title,Padding=new(9,6,9,6),MaxLength=200};System.Windows.Automation.AutomationProperties.SetAutomationId(title,"PageTagTitle");root.Children.Add(title);
        var colors=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,13,0,0)};var preview=new Border{Width=25,Height=25,CornerRadius=new(5),Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected)),Margin=new(0,0,10,0)};colors.Children.Add(preview);var change=new Button{Content="换个未使用的颜色",Padding=new(9,5,9,5)};change.Click+=(_,_)=>{selected=TagColors.Next(usedColors.Append(selected));preview.Background=new SolidColorBrush((Color)ColorConverter.ConvertFromString(selected));};colors.Children.Add(change);root.Children.Add(colors);
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new(0,15,0,0)};var cancel=new Button{Content="取消",IsCancel=true,Padding=new(14,5,14,5)};var save=new Button{Content="保存",IsDefault=true,Padding=new(14,5,14,5),Margin=new(8,0,0,0)};save.Click+=(_,_)=>{if(string.IsNullOrWhiteSpace(title.Text)){title.Focus();return;}Result=draft with{Title=title.Text.Trim(),Color=selected};DialogResult=true;};buttons.Children.Add(cancel);buttons.Children.Add(save);root.Children.Add(buttons);Loaded+=(_,_)=>{title.Focus();title.SelectAll();};
    }
}
