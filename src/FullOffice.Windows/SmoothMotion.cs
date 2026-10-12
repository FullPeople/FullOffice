using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace FlowOfficeLab;

public static class SmoothMotion
{
    sealed class Movement { public double Current;public double Goal;public double Last;public double TimeConstant; }
    static readonly Dictionary<(DependencyObject,DependencyProperty),Movement> active=new();
    static readonly Stopwatch clock=Stopwatch.StartNew();
    static bool subscribed;
    public static bool Running(DependencyObject target,DependencyProperty property)=>active.ContainsKey((target,property));
    public static double Goal(DependencyObject target,DependencyProperty property,double fallback)=>active.TryGetValue((target,property),out var movement)?movement.Goal:fallback;
    public static void Cancel(DependencyObject target,DependencyProperty property){active.Remove((target,property));UnsubscribeIfIdle();}
    public static void To(DependencyObject target,DependencyProperty property,double value,int milliseconds=190){
        double current=(double)target.GetValue(property);
        if(!SystemParameters.ClientAreaAnimation||double.IsNaN(current)||milliseconds<=0){Cancel(target,property);target.SetValue(property,value);return;}
        if(active.TryGetValue((target,property),out var movement)){movement.Goal=value;return;}
        active[(target,property)]=new(){Current=current,Goal=value,Last=clock.Elapsed.TotalSeconds,TimeConstant=milliseconds/4200.0};
        if(!subscribed){CompositionTarget.Rendering+=Tick;subscribed=true;}
    }
    static void Tick(object? sender,EventArgs args){
        double now=clock.Elapsed.TotalSeconds;
        foreach(var item in active.ToArray()){
            var (target,property)=item.Key;var movement=item.Value;
            if(target is FrameworkElement element&&!element.IsLoaded){active.Remove(item.Key);continue;}
            double dt=Math.Clamp(now-movement.Last,0,.1);movement.Last=now;
            movement.Current+=(movement.Goal-movement.Current)*(1-Math.Exp(-dt/movement.TimeConstant));
            if(Math.Abs(movement.Goal-movement.Current)<.4){movement.Current=movement.Goal;active.Remove(item.Key);}
            target.SetValue(property,movement.Current);
        }
        UnsubscribeIfIdle();
    }
    static void UnsubscribeIfIdle(){if(active.Count==0&&subscribed){CompositionTarget.Rendering-=Tick;subscribed=false;}}
}
