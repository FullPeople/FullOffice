using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FlowOfficeLab;

internal sealed class ColorTagButton : Button
{
    readonly ScaleTransform scale=new(1,1);
    readonly TranslateTransform shift=new();
    bool hovered,pressed,selected;
    double goalWidth=double.NaN,goalLeft=double.NaN;
    internal bool Selected=>selected;
    internal ScaleTransform InteractiveScale=>scale;
    public ColorTagButton(){
        Cursor=Cursors.Hand;AllowDrop=false;RenderTransformOrigin=new(1,.5);var group=new TransformGroup();group.Children.Add(scale);group.Children.Add(shift);RenderTransform=group;
        MouseEnter+=(_,_)=>{hovered=true;RefreshState();};MouseLeave+=(_,_)=>{hovered=false;pressed=false;RefreshState();};
        PreviewMouseLeftButtonDown+=(_,_)=>{pressed=true;RefreshState();};PreviewMouseLeftButtonUp+=(_,_)=>{pressed=false;RefreshState();};LostMouseCapture+=(_,_)=>{pressed=false;RefreshState();};
        RefreshState();
    }
    internal void SetSelected(bool value){if(selected==value)return;selected=value;RefreshState();}
    internal void RefreshState(){
        double size=pressed?.955:hovered?1.015:1;
        Animate(scale,ScaleTransform.ScaleXProperty,size,pressed?80:170);Animate(scale,ScaleTransform.ScaleYProperty,size,pressed?80:170);
        Animate(shift,TranslateTransform.XProperty,hovered?-2:0,170);Animate(this,OpacityProperty,pressed?.78:hovered||selected?1:.91,130);
        FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal;
        BorderBrush=new SolidColorBrush(Color.FromArgb(selected?(byte)240:hovered?(byte)160:(byte)50,255,255,255));BorderThickness=new(selected?1.6:1);
    }
    internal void Pulse(){
        if(!SystemParameters.ClientAreaAnimation)return;double finish=hovered?1.015:1;
        foreach(var property in new[]{ScaleTransform.ScaleXProperty,ScaleTransform.ScaleYProperty}){var frames=new DoubleAnimationUsingKeyFrames{Duration=TimeSpan.FromMilliseconds(180),FillBehavior=FillBehavior.Stop};frames.KeyFrames.Add(new EasingDoubleKeyFrame(.94,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65)),new CubicEase{EasingMode=EasingMode.EaseOut}));frames.KeyFrames.Add(new EasingDoubleKeyFrame(finish,KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(180)),new CubicEase{EasingMode=EasingMode.EaseOut}));scale.SetValue(property,finish);scale.BeginAnimation(property,frames,HandoffBehavior.SnapshotAndReplace);}
    }
    internal void Place(double width,double left,double top,bool animated){
        if(double.IsNaN(goalWidth)||Math.Abs(goalWidth-width)>.01){goalWidth=width;Animate(this,WidthProperty,width,animated?170:0);}
        if(double.IsNaN(goalLeft)||Math.Abs(goalLeft-left)>.01){goalLeft=left;Animate(this,Canvas.LeftProperty,left,animated?170:0);}
        Canvas.SetTop(this,top);
    }
    static void Animate(Animatable target,DependencyProperty property,double goal,int milliseconds){double current=(double)target.GetValue(property);target.SetValue(property,goal);if(!SystemParameters.ClientAreaAnimation||milliseconds<=0||!double.IsFinite(current)){target.BeginAnimation(property,null);return;}target.BeginAnimation(property,new DoubleAnimation(current,goal,TimeSpan.FromMilliseconds(milliseconds)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);}
    static void Animate(UIElement target,DependencyProperty property,double goal,int milliseconds){double current=(double)target.GetValue(property);target.SetValue(property,goal);if(!SystemParameters.ClientAreaAnimation||milliseconds<=0||!double.IsFinite(current)){target.BeginAnimation(property,null);return;}target.BeginAnimation(property,new DoubleAnimation(current,goal,TimeSpan.FromMilliseconds(milliseconds)){EasingFunction=new CubicEase{EasingMode=EasingMode.EaseOut},FillBehavior=FillBehavior.Stop},HandoffBehavior.SnapshotAndReplace);}
}
