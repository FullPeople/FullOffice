using System.Windows;

namespace FlowOfficeLab;

public sealed partial class PdfReaderView
{
    int wheelGoalPage=-1;
    double wheelGoalOffset;
    long wheelSerial;
    void ResetWheelGoal(){wheelGoalPage=-1;wheelSerial++;}
    double PageScrollRange(int page)=>Math.Max(0,PageDisplayWidth(page)*engine.PageAspect(page)+48-scroll.ViewportHeight);
    async Task HandleWheel(int delta,bool control){
        if(disposed||!isActiveView||currentFile==""||opening||delta==0)return;
        if(control){await ChangeZoom(delta/120.0*.12);return;}
        long serial=++wheelSerial;int doc=generation,page=currentPage;
        double maximum=PageScrollRange(page),goal=wheelGoalPage==page?wheelGoalOffset:SmoothScroll.Goal(scroll);
        double step=SystemParameters.WheelScrollLines<0?scroll.ViewportHeight*.85:Math.Max(1,SystemParameters.WheelScrollLines)*22;
        double next=goal-delta/120.0*step;
        if(wheelPageMode){next=Math.Clamp(next,0,maximum);wheelGoalPage=page;wheelGoalOffset=next;SmoothScroll.To(scroll,next);return;}
        if(maximum<=1){page=Math.Clamp(page+(delta<0?1:-1)*Math.Max(1,(int)Math.Floor(Math.Abs(delta)/120.0)),0,engine.PageCount-1);next=0;}
        else{
            while(next>=maximum&&page<engine.PageCount-1){next-=maximum;page++;maximum=PageScrollRange(page);if(maximum<=1){next=0;break;}}
            while(next<0&&page>0){page--;maximum=PageScrollRange(page);next+=maximum;if(maximum<=1){next=0;break;}}
            next=Math.Clamp(next,0,maximum);
        }
        wheelGoalPage=page;wheelGoalOffset=next;
        if(page==currentPage){SmoothScroll.To(scroll,next);return;}
        await ShowPage(page,false,true,false,null,true);
        if(serial!=wheelSerial||doc!=generation||page!=currentPage||!isActiveView)return;
        scroll.UpdateLayout();SmoothScroll.Set(scroll,next);
    }
}
