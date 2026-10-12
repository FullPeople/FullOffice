using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowOfficeLab;

public sealed class RegionPinWindow : Window
{
    readonly ScrollViewer viewer=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Background=new SolidColorBrush(Color.FromRgb(233,237,244)),CanContentScroll=false};
    readonly Image image=new(){Stretch=Stretch.Fill};
    double zoom=1;bool moving;Point last;readonly TextBlock scale=new(){VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,8,0)};
    public BitmapSource Snapshot {get;}
    public RegionPinWindow(Window owner,BitmapSource source,string title){
        Title=title+" · 固定区域";Width=490;Height=460;MinWidth=220;MinHeight=180;Topmost=true;Icon=owner.Icon;FontFamily=owner.FontFamily;WindowStartupLocation=WindowStartupLocation.Manual;Background=Brushes.White;
        // Copy only the chosen rectangle, so pins do not keep whole-page caches alive.
        var pixels=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(pixels,source.PixelWidth*4,0);Snapshot=BitmapSource.Create(source.PixelWidth,source.PixelHeight,96,96,PixelFormats.Bgra32,null,pixels,source.PixelWidth*4);Snapshot.Freeze();image.Source=Snapshot;RenderOptions.SetBitmapScalingMode(image,BitmapScalingMode.HighQuality);
        var root=new DockPanel{Background=Brushes.White};Content=root;var tools=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(6,4,6,4)};DockPanel.SetDock(tools,Dock.Top);root.Children.Add(tools);
        Button Tool(string text,Action action){var button=new Button{Content=text,Padding=new(8,5,8,5),Margin=new(2)};button.Click+=(_,_)=>action();return button;}
        tools.Children.Add(Tool("适应",Fit));tools.Children.Add(Tool("－",()=>ZoomAt(zoom/1.2,new(viewer.ViewportWidth/2,viewer.ViewportHeight/2))));tools.Children.Add(scale);tools.Children.Add(Tool("＋",()=>ZoomAt(zoom*1.2,new(viewer.ViewportWidth/2,viewer.ViewportHeight/2))));var pin=Tool("取消置顶",()=>{});pin.Click+=(_,_)=>{Topmost=!Topmost;pin.Content=Topmost?"取消置顶":"置顶";};tools.Children.Add(pin);viewer.Content=image;root.Children.Add(viewer);image.HorizontalAlignment=HorizontalAlignment.Center;image.VerticalAlignment=VerticalAlignment.Center;
        viewer.PreviewMouseWheel+=(_,e)=>{e.Handled=true;ZoomAt(zoom*Math.Pow(1.16,e.Delta/120.0),e.GetPosition(viewer));};image.MouseLeftButtonDown+=(_,e)=>{moving=true;last=e.GetPosition(viewer);image.CaptureMouse();image.Cursor=Cursors.SizeAll;e.Handled=true;};image.MouseMove+=(_,e)=>{if(!moving)return;var now=e.GetPosition(viewer);PanBy(new(last.X-now.X,last.Y-now.Y));last=now;};image.MouseLeftButtonUp+=(_,e)=>{moving=false;image.ReleaseMouseCapture();image.Cursor=Cursors.Hand;e.Handled=true;};image.LostMouseCapture+=(_,_)=>moving=false;image.Cursor=Cursors.Hand;
        Loaded+=(_,_)=>Fit();
    }
    public void Fit(){viewer.UpdateLayout();zoom=Math.Clamp(Math.Min(Math.Max(80,viewer.ViewportWidth-18)/Snapshot.PixelWidth,Math.Max(80,viewer.ViewportHeight-18)/Snapshot.PixelHeight),.05,12);ApplyZoom();viewer.ScrollToHorizontalOffset(0);viewer.ScrollToVerticalOffset(0);}
    public void PanBy(Point delta){viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset+delta.X);viewer.ScrollToVerticalOffset(viewer.VerticalOffset+delta.Y);}
    public void ZoomAt(double value,Point point){double old=zoom;var origin=image.TranslatePoint(new Point(0,0),viewer);zoom=Math.Clamp(value,.05,12);double x=(point.X-origin.X)*zoom/old-point.X,y=(point.Y-origin.Y)*zoom/old-point.Y;ApplyZoom();viewer.UpdateLayout();viewer.ScrollToHorizontalOffset(Math.Max(0,x));viewer.ScrollToVerticalOffset(Math.Max(0,y));}
    void ApplyZoom(){image.Width=Snapshot.PixelWidth*zoom;image.Height=Snapshot.PixelHeight*zoom;scale.Text=$"{zoom:P0}";}
}

public sealed partial class PdfReaderView
{
    readonly List<RegionPinWindow> pins=new();
    async Task PinRegion(int page,Rect region,string? title=null){int doc=generation;status.Text="正在创建固定区域小窗…";int width=Math.Clamp((int)Math.Ceiling(850/Math.Max(.08,region.Width)),1000,2200);var rendered=await engine.RenderAsync(page,width,documentCancellation.Token,true,true);if(doc!=generation)return;var cropped=CropImage(rendered.Image,region);var pin=new RegionPinWindow(Host,cropped,title??$"{Path.GetFileNameWithoutExtension(currentFile)} · 第 {page+1} 页");pins.Add(pin);pin.Closed+=(_,_)=>pins.Remove(pin);pin.Left=Host.Left+160+(pins.Count-1)%4*30;pin.Top=Host.Top+120+(pins.Count-1)%4*30;pin.Show();status.Text="区域已固定：滚轮缩放，拖动查看细节";}
}
