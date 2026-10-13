using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace FlowOfficeLab;

public static class SmoothScroll
{
    static readonly DependencyProperty OffsetProperty=DependencyProperty.RegisterAttached("Offset",typeof(double),typeof(SmoothScroll),new PropertyMetadata(0.0,(d,e)=>((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    static readonly DependencyProperty PositionRevisionProperty=DependencyProperty.RegisterAttached("PositionRevision",typeof(long),typeof(SmoothScroll),new PropertyMetadata(0L));
    static readonly DependencyProperty PendingOffsetProperty=DependencyProperty.RegisterAttached("PendingOffset",typeof(double),typeof(SmoothScroll),new PropertyMetadata(double.NaN));
    static readonly DependencyProperty RequestSerialProperty=DependencyProperty.RegisterAttached("RequestSerial",typeof(long),typeof(SmoothScroll),new PropertyMetadata(0L));
    public static long PositionRevision(ScrollViewer viewer)=>(long)viewer.GetValue(PositionRevisionProperty);
    static void RecordPositionChange(ScrollViewer viewer,bool fromWheel){if(!fromWheel)viewer.SetValue(PositionRevisionProperty,unchecked(PositionRevision(viewer)+1));}
    public static double Goal(ScrollViewer viewer){double pending=(double)viewer.GetValue(PendingOffsetProperty);return SmoothMotion.Goal(viewer,OffsetProperty,double.IsNaN(pending)?viewer.VerticalOffset:pending);}
    public static void Set(ScrollViewer viewer,double value,bool fromWheel=false){
        RecordPositionChange(viewer,fromWheel);
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        long serial=unchecked((long)viewer.GetValue(RequestSerialProperty)+1);viewer.SetValue(RequestSerialProperty,serial);
        bool pending=Math.Abs(value-viewer.VerticalOffset)>.01;viewer.SetValue(PendingOffsetProperty,pending?value:double.NaN);
        SmoothMotion.Cancel(viewer,OffsetProperty);viewer.SetValue(OffsetProperty,value);viewer.ScrollToVerticalOffset(value);
        // WPF publishes offsets after its queued scroll commands complete during layout.
        if(pending)viewer.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>{if((long)viewer.GetValue(RequestSerialProperty)==serial)viewer.SetValue(PendingOffsetProperty,double.NaN);}));
    }
    public static void To(ScrollViewer viewer,double value,bool fromWheel=false){
        RecordPositionChange(viewer,fromWheel);
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        double pending=(double)viewer.GetValue(PendingOffsetProperty);viewer.SetValue(PendingOffsetProperty,double.NaN);
        if(!SmoothMotion.Running(viewer,OffsetProperty))viewer.SetValue(OffsetProperty,double.IsNaN(pending)?viewer.VerticalOffset:pending);
        SmoothMotion.To(viewer,OffsetProperty,value,180);
    }
}
