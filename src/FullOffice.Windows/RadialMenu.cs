using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlowOfficeLab;

public sealed record RadialAction(string Title,string Glyph,bool Enabled,Func<Task> Execute);
public sealed class RadialMenuSurface : FrameworkElement
{
    public const double Diameter=296,DeadZone=28;
    public IReadOnlyList<RadialAction> Actions {get;set;}=Array.Empty<RadialAction>();
    public int Selected {get;private set;}=-1;
    public event Action<int>? Chosen;
    public RadialMenuSurface(){Width=Height=Diameter;Cursor=Cursors.Hand;Focusable=true;MouseMove+=(_,e)=>Select(Hit(e.GetPosition(this)));MouseLeftButtonUp+=(_,e)=>{e.Handled=true;Chosen?.Invoke(Hit(e.GetPosition(this)));};}
    public static int Direction(Vector vector)=>vector.Length<DeadZone?-1:vector.X<0?0:1;
    int Hit(Point point){var vector=point-new Point(Diameter/2,Diameter/2);return vector.Length<43||vector.Length>139?-1:Direction(vector);}
    public void Select(int index){if(index==Selected)return;Selected=index;InvalidateVisual();}
    static Point Polar(double radius,double angle){double radians=angle*Math.PI/180;return new(Diameter/2+radius*Math.Cos(radians),Diameter/2+radius*Math.Sin(radians));}
    static Geometry Slice(int index){double a=index==0?92:-88,b=a+176;var shape=new StreamGeometry();using(var g=shape.Open()){g.BeginFigure(Polar(136,a),true,true);g.ArcTo(Polar(136,b),new(136,136),0,false,SweepDirection.Clockwise,true,false);g.LineTo(Polar(45,b),true,false);g.ArcTo(Polar(45,a),new(45,45),0,false,SweepDirection.Counterclockwise,true,false);}shape.Freeze();return shape;}
    protected override void OnRender(DrawingContext dc){base.OnRender(dc);var center=new Point(Diameter/2,Diameter/2);dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(30,38,55,97)),null,new(center.X,center.Y+4),141,141);dc.DrawEllipse(Brushes.White,new Pen(new SolidColorBrush(Color.FromRgb(218,226,242)),1),center,139,139);for(int i=0;i<Actions.Count;i++){var item=Actions[i];bool selected=Selected==i&&item.Enabled;var color=selected?Color.FromRgb(66,100,225):Color.FromRgb(248,250,255);dc.DrawGeometry(new SolidColorBrush(color),new Pen(Brushes.White,4){LineJoin=PenLineJoin.Round},Slice(i));var brush=item.Enabled?(selected?Brushes.White:new SolidColorBrush(Color.FromRgb(43,56,87))):Brushes.LightSlateGray;var point=Polar(91,i==0?180:0);Text(dc,item.Glyph,"Segoe MDL2 Assets",25,brush,new(point.X,point.Y-15));Text(dc,item.Title,"Microsoft YaHei UI",14,brush,new(point.X,point.Y+18));}dc.DrawEllipse(Brushes.White,new Pen(new SolidColorBrush(Color.FromRgb(222,230,245)),1),center,41,41);Text(dc,"取消","Microsoft YaHei UI",12,Brushes.SlateGray,center);}
    void Text(DrawingContext dc,string text,string font,double size,Brush brush,Point center){var layout=new FormattedText(text,CultureInfo.GetCultureInfo("zh-CN"),FlowDirection.LeftToRight,new Typeface(font),size,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);dc.DrawText(layout,new(center.X-layout.Width/2,center.Y-layout.Height/2));}
}
public sealed partial class PdfReaderView
{
    readonly Canvas radialOverlay=new(){Visibility=Visibility.Collapsed,Background=new SolidColorBrush(Color.FromArgb(8,30,50,95))};
    readonly RadialMenuSurface radialSurface=new();
    Point radialStart;
    bool radialHolding,radialGestureMoved;
    FrameworkElement? radialRoot;
    bool HasOwnContextMenu(DependencyObject? node){while(node!=null&&node!=scroll){if(node is FrameworkElement element&&element.ContextMenu!=null&&element.ContextMenu!=mainMenu)return true;node=node is Visual?VisualTreeHelper.GetParent(node):LogicalTreeHelper.GetParent(node);}return false;}
    void InitializeRadialMenu(Grid root,Grid pageHost){radialRoot=root;Grid.SetRowSpan(radialOverlay,4);Panel.SetZIndex(radialOverlay,100);root.Children.Add(radialOverlay);radialOverlay.Children.Add(radialSurface);
        scroll.ContextMenuOpening+=(_,e)=>{if(Host.RadialEnabled&&!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)&&!HasOwnContextMenu(e.OriginalSource as DependencyObject))e.Handled=true;};
        scroll.PreviewMouseRightButtonDown+=(_,e)=>{if(!Host.RadialEnabled||Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)||HasOwnContextMenu(e.OriginalSource as DependencyObject)||currentFile==""||displayedPage!=currentPage)return;e.Handled=true;StartRightGesture(e.GetPosition(root));Mouse.Capture(scroll);};
        scroll.PreviewMouseMove+=(_,e)=>{if(!radialHolding||radialGestureMoved)return;e.Handled=true;MoveRightGesture(e.GetPosition(root));if(radialGestureMoved)Mouse.Capture(radialOverlay,CaptureMode.SubTree);};
        scroll.PreviewMouseRightButtonUp+=async(_,e)=>{if(!radialHolding)return;e.Handled=true;await FinishRightGesture(e.GetPosition(root));};
        radialOverlay.PreviewMouseMove+=(_,e)=>{if(!radialHolding)return;e.Handled=true;MoveRightGesture(e.GetPosition(root));};
        radialOverlay.PreviewMouseRightButtonUp+=async(_,e)=>{if(!radialHolding)return;e.Handled=true;await FinishRightGesture(e.GetPosition(root));};
        radialOverlay.PreviewMouseRightButtonDown+=(_,e)=>{e.Handled=true;CloseRadial();};radialSurface.Chosen+=async i=>await ExecuteRadial(i);radialOverlay.MouseLeftButtonUp+=(_,e)=>{if(e.OriginalSource==radialOverlay){e.Handled=true;CloseRadial();}};
        PreviewKeyDown+=(_,e)=>{if(radialHolding||radialOverlay.Visibility==Visibility.Visible){if(e.Key==Key.Escape){e.Handled=true;CloseRadial();}}};Host.Deactivated+=HostDeactivated;
        radialOverlay.LostMouseCapture+=(_,_)=>{if(radialHolding)CloseRadial();};scroll.LostMouseCapture+=(_,_)=>{if(radialHolding&&!radialGestureMoved)CloseRadial();};
    }
    void HostDeactivated(object? sender,EventArgs e)=>CloseRadial();
    void StartRightGesture(Point point){CloseHoverPopup();mainMenu.IsOpen=false;radialStart=point;radialHolding=true;radialGestureMoved=false;radialOverlay.Visibility=Visibility.Collapsed;}
    void MoveRightGesture(Point point){if(!radialHolding||radialRoot==null)return;var vector=point-radialStart;if(!radialGestureMoved&&vector.Length>=RadialMenuSurface.DeadZone){radialGestureMoved=true;BeginRadial(radialStart,radialRoot);}if(radialGestureMoved)radialSurface.Select(RadialMenuSurface.Direction(vector));}
    async Task FinishRightGesture(Point point){bool dragged=radialGestureMoved;int selected=RadialMenuSurface.Direction(point-radialStart);radialHolding=false;CloseRadial();if(dragged)await ExecuteRadial(selected);else OpenOrdinaryMenu();}
    void OpenOrdinaryMenu(){BuildMainMenu();mainMenu.PlacementTarget=scroll;mainMenu.Placement=PlacementMode.RelativePoint;mainMenu.HorizontalOffset=radialRoot!=null?radialRoot.TranslatePoint(radialStart,scroll).X:0;mainMenu.VerticalOffset=radialRoot!=null?radialRoot.TranslatePoint(radialStart,scroll).Y:0;mainMenu.IsOpen=true;}
    IReadOnlyList<RadialAction> RadialActions()=>new RadialAction[]{new("添加注释","\uE90A",CanEditDocument,async()=>{var text=InputNote();if(text!=null)await AddAnnotation(contextPosition,contextPosition,false,text);}),new("添加标签","\uE8EC",CanEditDocument,()=>AddPageTag(contextPosition))};
    void BeginRadial(Point point,FrameworkElement root){CloseHoverPopup();mainMenu.IsOpen=false;radialSurface.Actions=RadialActions();radialSurface.Select(-1);radialSurface.InvalidateVisual();double half=RadialMenuSurface.Diameter/2;Canvas.SetLeft(radialSurface,Math.Clamp(point.X-half,8,Math.Max(8,root.ActualWidth-RadialMenuSurface.Diameter-8)));Canvas.SetTop(radialSurface,Math.Clamp(point.Y-half,8,Math.Max(8,root.ActualHeight-RadialMenuSurface.Diameter-8)));radialOverlay.Visibility=Visibility.Visible;radialSurface.RenderTransformOrigin=new(.5,.5);var scale=new ScaleTransform(.94,.94);radialSurface.RenderTransform=scale;scale.BeginAnimation(ScaleTransform.ScaleXProperty,new DoubleAnimation(.94,1,TimeSpan.FromMilliseconds(90)));scale.BeginAnimation(ScaleTransform.ScaleYProperty,new DoubleAnimation(.94,1,TimeSpan.FromMilliseconds(90)));}
    async Task ExecuteRadial(int index){var actions=radialSurface.Actions;CloseRadial();if(index<0||index>=actions.Count||!actions[index].Enabled)return;try{await actions[index].Execute();Host.ReaderChanged(this);}catch(OperationCanceledException){}catch(Exception ex){status.Text=ex.Message;}}
    void CloseRadial(){radialHolding=false;if(Mouse.Captured==radialOverlay||Mouse.Captured==radialSurface||Mouse.Captured==scroll)Mouse.Capture(null);radialOverlay.Visibility=Visibility.Collapsed;}
}
