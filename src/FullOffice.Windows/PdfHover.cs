using System.Text.Json;
using System.Windows;
using static FlowOfficeLab.PdfNative;

namespace FlowOfficeLab;

public sealed record HoverReference(Guid Id,string Title,int SourcePage,double X,double Y,double Width,double Height){public Rect Region=>new(X,Y,Width,Height);}
public sealed record HoverMark(HoverReference Reference,int Page,RectFData Bounds);

public sealed partial class PdfEngine
{
    public Task<List<HoverMark>> HoverMarksAsync(int index,CancellationToken token)=>PdfScheduler.Run(()=>{
        lock(Gate){token.ThrowIfCancellationRequested();var page=LoadPage(index);try{var marks=new List<HoverMark>();for(int i=0;i<FPDFPage_GetAnnotCount(page);i++){var annotation=FPDFPage_GetAnnot(page,i);if(annotation==IntPtr.Zero)continue;try{var json=ReadString((b,n)=>FPDFAnnot_GetStringValue(annotation,"FlowOfficePeek",b,n));if(string.IsNullOrWhiteSpace(json))continue;HoverReference? reference=null;try{reference=JsonSerializer.Deserialize<HoverReference>(json);}catch(JsonException){}if(reference==null||reference.SourcePage<0||reference.SourcePage>=PageCount||!double.IsFinite(reference.X+reference.Y+reference.Width+reference.Height)||reference.X<0||reference.Y<0||reference.Width<=0||reference.Height<=0||reference.X+reference.Width>1.0001||reference.Y+reference.Height>1.0001)continue;if(FPDFAnnot_GetRect(annotation,out var bounds)!=0)marks.Add(new(reference,index,new(bounds.left,bounds.top,bounds.right,bounds.bottom)));}finally{FPDFPage_CloseAnnot(annotation);}}return marks;}finally{FPDF_ClosePage(page);}}
    },token);
    public Task AddHoverAsync(int page,Point anchor,HoverReference reference)=>AnnotateAsync(page,anchor,anchor,false,$"悬停预览：{reference.Title}",JsonSerializer.Serialize(reference));
    public Task EditHoverAsync(int index,Guid id,string? title)=>PdfScheduler.Run(()=>{
        lock(Gate){var page=LoadPage(index);try{for(int i=FPDFPage_GetAnnotCount(page)-1;i>=0;i--){var annotation=FPDFPage_GetAnnot(page,i);bool remove=false;try{var json=ReadString((b,n)=>FPDFAnnot_GetStringValue(annotation,"FlowOfficePeek",b,n));if(string.IsNullOrWhiteSpace(json))continue;HoverReference? reference;try{reference=JsonSerializer.Deserialize<HoverReference>(json);}catch(JsonException){continue;}if(reference?.Id!=id)continue;if(title==null)remove=true;else{Check(FPDFAnnot_SetStringValue(annotation,"FlowOfficePeek",JsonSerializer.Serialize(reference with {Title=title})));Check(FPDFAnnot_SetStringValue(annotation,"Contents",$"悬停预览：{title}"));}annotationModified=true;ClearCache();}finally{FPDFPage_CloseAnnot(annotation);}if(remove)Check(FPDFPage_RemoveAnnot(page,i));}}finally{FPDF_ClosePage(page);}}
    });
}
