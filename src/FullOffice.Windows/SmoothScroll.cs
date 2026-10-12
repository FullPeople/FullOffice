using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace FlowOfficeLab;

public static class SmoothScroll
{
    static readonly DependencyProperty OffsetProperty=DependencyProperty.RegisterAttached("Offset",typeof(double),typeof(SmoothScroll),new PropertyMetadata(0.0,(d,e)=>((ScrollViewer)d).ScrollToVerticalOffset((double)e.NewValue)));
    public static double Goal(ScrollViewer viewer)=>SmoothMotion.Goal(viewer,OffsetProperty,viewer.VerticalOffset);
    public static void Set(ScrollViewer viewer,double value){
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        SmoothMotion.Cancel(viewer,OffsetProperty);viewer.SetValue(OffsetProperty,value);viewer.ScrollToVerticalOffset(value);
    }
    public static void To(ScrollViewer viewer,double value){
        value=Math.Clamp(value,0,viewer.ScrollableHeight);
        if(!SmoothMotion.Running(viewer,OffsetProperty))viewer.SetValue(OffsetProperty,viewer.VerticalOffset);
        SmoothMotion.To(viewer,OffsetProperty,value,180);
    }
}
