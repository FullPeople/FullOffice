using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace FlowOfficeLab;

public static class SmoothScroll
{
    static readonly DependencyProperty OffsetProperty=DependencyProperty.RegisterAttached("Offset",typeof(double),typeof(SmoothScroll),new PropertyMetadata(0.0,(d,e)=>((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    static readonly DependencyProperty PositionRevisionProperty=DependencyProperty.RegisterAttached("PositionRevision",typeof(long),typeof(SmoothScroll),new PropertyMetadata(0L));
    public static long PositionRevision(ScrollViewer viewer)=>(long)viewer.GetValue(PositionRevisionProperty);
    static void RecordPositionChange(ScrollViewer viewer,bool fromWheel){if(!fromWheel)viewer.SetValue(PositionRevisionProperty,unchecked(PositionRevision(viewer)+1));}
    public static double Goal(ScrollViewer viewer)=>SmoothMotion.Goal(viewer,OffsetProperty,viewer.VerticalOffset);
    public static void Set(ScrollViewer viewer,double value,bool fromWheel=false){
        RecordPositionChange(viewer,fromWheel);
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        SmoothMotion.Cancel(viewer,OffsetProperty);viewer.SetValue(OffsetProperty,value);viewer.ScrollToVerticalOffset(value);
    }
    public static void To(ScrollViewer viewer,double value,bool fromWheel=false){
        RecordPositionChange(viewer,fromWheel);
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        if(!SmoothMotion.Running(viewer,OffsetProperty))viewer.SetValue(OffsetProperty,viewer.VerticalOffset);
        SmoothMotion.To(viewer,OffsetProperty,value,180);
    }
}
