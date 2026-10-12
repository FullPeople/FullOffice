using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    HoverReference? pendingHoverSource;
    readonly List<FrameworkElement> hoverIcons=new();
    readonly Popup hoverPopup=new(){AllowsTransparency=true,Placement=PlacementMode.Right,StaysOpen=true};
    readonly DispatcherTimer hoverCloseTimer=new(){Interval=TimeSpan.FromMilliseconds(220)};
    CancellationTokenSource hoverCancellation=new();
    List<HoverMark> currentHoverMarks=new();
    int hoverRequest;
    void InitializeHoverPreview(){
        hoverCloseTimer.Tick+=(_,_)=>CloseHoverPopup();hoverPopup.MouseEnter+=(_,_)=>hoverCloseTimer.Stop();hoverPopup.MouseLeave+=(_,_)=>hoverCloseTimer.Start();
    }
    void CloseHoverPopup(){hoverCloseTimer.Stop();hoverCancellation.Cancel();hoverPopup.IsOpen=false;}
    void ClearHoverIcons(){CloseHoverPopup();foreach(var icon in hoverIcons)canvas.Children.Remove(icon);hoverIcons.Clear();currentHoverMarks.Clear();}
    async Task RefreshHoverIcons(int page,int doc,int revision,CancellationToken token){
        int request=++hoverRequest;try{var marks=await engine.HoverMarksAsync(page,token);if(token.IsCancellationRequested||doc!=generation||page!=displayedPage||revision!=renderRevision||request!=hoverRequest)return;ClearHoverIcons();currentHoverMarks=marks;
            foreach(var mark in marks){var box=await engine.LocateAsync(new Annotation(mark.Reference.Title,mark.Page,1,mark.Bounds));if(token.IsCancellationRequested||doc!=generation||revision!=renderRevision||request!=hoverRequest)return;var icon=new Border{Width=23,Height=23,CornerRadius=new(7),Background=new SolidColorBrush(Color.FromRgb(59,104,214)),BorderBrush=Brushes.White,BorderThickness=new(1.5),Cursor=System.Windows.Input.Cursors.Hand,Child=new TextBlock{Text="↗",FontSize=15,Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center},ToolTip="悬停预览正文；点击跳到来源"};Canvas.SetLeft(icon,Math.Clamp(box.Left*pageImage.Width,0,Math.Max(0,pageImage.Width-23)));Canvas.SetTop(icon,Math.Clamp(box.Top*pageImage.Height,0,Math.Max(0,pageImage.Height-23)));canvas.Children.Add(icon);hoverIcons.Add(icon);icon.ToolTip=null;
                icon.MouseEnter+=async(_,_)=>await ShowHoverPreview(mark.Reference,icon);icon.MouseLeave+=(_,_)=>hoverCloseTimer.Start();icon.MouseLeftButtonUp+=async(_,e)=>{e.Handled=true;CloseHoverPopup();await GoToRegion(mark.Reference);};
                var menu=new ContextMenu();menu.Items.Add(Menu("打开来源",()=>GoToRegion(mark.Reference)));menu.Items.Add(Menu("将来源区域固定为小窗",()=>PinRegion(mark.Reference.SourcePage,mark.Reference.Region,mark.Reference.Title)));menu.Items.Add(Menu("修改悬停标标题…",async()=>{if(!CanEditDocument)return;var title=InputText("修改悬停标标题",mark.Reference.Title);if(title!=null){await engine.EditHoverAsync(mark.Page,mark.Reference.Id,title);await ShowPage(currentPage,false,true,true);}},CanEditDocument));menu.Items.Add(Menu("删除悬停标",async()=>{if(!CanEditDocument)return;await engine.EditHoverAsync(mark.Page,mark.Reference.Id,null);await ShowPage(currentPage,false,true,true);},CanEditDocument));icon.ContextMenu=menu;
            }
        }catch(OperationCanceledException){}catch(Exception ex){if(doc==generation&&!token.IsCancellationRequested)status.Text="悬停标读取失败："+ex.Message;}
    }
    async Task ShowHoverPreview(HoverReference reference,FrameworkElement target){
        CloseHoverPopup();hoverCancellation.Dispose();hoverCancellation=CancellationTokenSource.CreateLinkedTokenSource(documentCancellation.Token);var token=hoverCancellation.Token;int doc=generation;hoverCloseTimer.Stop();
        var panel=new StackPanel{Margin=new(12),MaxWidth=440};panel.Children.Add(new TextBlock{Text=reference.Title,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new(0,0,0,7)});var loading=new TextBlock{Text=$"第 {reference.SourcePage+1} 页 · 正在生成预览…",Foreground=Brushes.SlateGray};panel.Children.Add(loading);var image=new Image{MaxWidth=410,MaxHeight=400,Stretch=Stretch.Uniform};panel.Children.Add(image);var actions=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,8,0,0)};actions.Children.Add(Button("打开来源",async()=>{CloseHoverPopup();await GoToRegion(reference);}));actions.Children.Add(Button("固定小窗",async()=>await PinRegion(reference.SourcePage,reference.Region,reference.Title)));panel.Children.Add(actions);
        hoverPopup.Child=new Border{CornerRadius=new(10),Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(185,201,232)),BorderThickness=new(1),Child=panel};hoverPopup.PlacementTarget=target;hoverPopup.IsOpen=true;
        try{await Task.Delay(130,token);int width=Math.Clamp((int)Math.Ceiling(420/Math.Max(.08,reference.Width)),700,2200);RenderedPage? rendered=null;if(engine.TryGetPreview(reference.SourcePage,out var cached)&&cached!=null&&cached.Width>=700)rendered=cached;else rendered=await engine.RenderAsync(reference.SourcePage,width,token,true,true);if(token.IsCancellationRequested||doc!=generation)return;image.Source=CropImage(rendered!.Image,reference.Region);loading.Text=$"第 {reference.SourcePage+1} 页";}catch(OperationCanceledException){}catch(Exception){if(!token.IsCancellationRequested)loading.Text="预览暂时无法生成，可以点击打开来源";}
    }
    static BitmapSource CropImage(BitmapSource source,Rect region){int x=Math.Clamp((int)Math.Floor(region.Left*source.PixelWidth),0,source.PixelWidth-1),y=Math.Clamp((int)Math.Floor(region.Top*source.PixelHeight),0,source.PixelHeight-1),w=Math.Clamp((int)Math.Ceiling(region.Width*source.PixelWidth),1,source.PixelWidth-x),h=Math.Clamp((int)Math.Ceiling(region.Height*source.PixelHeight),1,source.PixelHeight-y);var cropped=new CroppedBitmap(source,new Int32Rect(x,y,w,h));cropped.Freeze();return cropped;}
    async Task PlaceHoverMark(){if(!CanEditDocument||pendingHoverSource==null)return;var source=pendingHoverSource;var title=InputText("悬停标标题",source.Title);if(title==null)return;int doc=generation,page=currentPage;renderCancellation.Cancel();await engine.AddHoverAsync(page,contextPosition,source with {Id=Guid.NewGuid(),Title=title});if(doc==generation&&currentPage==page){await ShowPage(page,false,true,true);status.Text="悬停标已添加，记得保存";}}
    void SetHoverSource(Point start,Point end){var rect=new Rect(new Point(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y)),new Point(Math.Max(start.X,end.X),Math.Max(start.Y,end.Y)));pendingHoverSource=new(Guid.NewGuid(),$"第 {currentPage+1} 页的内容",currentPage,rect.X,rect.Y,rect.Width,rect.Height);SetMode(0);status.Text="来源区域已选好：到目标位置右键，选择“放置悬停标”";}
    async Task GoToRegion(HoverReference reference){await ShowPage(reference.SourcePage,false,true,true);if(displayedPage!=reference.SourcePage)return;selection.BeginAnimation(OpacityProperty,null);selection.Opacity=1;DrawSelection(new(reference.X*pageImage.Width,reference.Y*pageImage.Height),new((reference.X+reference.Width)*pageImage.Width,(reference.Y+reference.Height)*pageImage.Height));selection.Visibility=Visibility.Visible;SmoothScroll.To(scroll,Math.Max(0,reference.Y*pageImage.Height+24-40));}
}
