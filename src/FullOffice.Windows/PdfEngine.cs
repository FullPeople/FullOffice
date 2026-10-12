using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FlowOfficeLab.PdfNative;

namespace FlowOfficeLab;

public record Annotation(string Title,int Page,int Subtype,RectFData Bounds) { public string Label => $"{Title}  ·  {Page+1}"; }
public record RectFData(double Left,double Top,double Right,double Bottom);
public record RenderedPage(BitmapSource Image,int Page,int Width,int Height,double Milliseconds,bool Cached);
public record PdfInfo(int Pages,List<Bookmark> Bookmarks,double OpenMilliseconds);

public sealed partial class PdfEngine : IDisposable
{
    static readonly object Gate=new();
    static bool initialized;
    double[] aspects=Array.Empty<double>();
    public double PageAspect(int page)=>page>=0&&page<aspects.Length?aspects[page]:1.414;
    IntPtr document;
    string sourcePath="";
    readonly Dictionary<(int,int),RenderedPage> cache=new();
    readonly LinkedList<(int,int)> lru=new();
    readonly object cacheGate=new();
    readonly Dictionary<Bookmark,Point?> bookmarkPositions=new();
    Dictionary<int,Bookmark[]> bookmarksByPage=new();
    long cacheBytes;
    const long CacheLimit=64L*1024*1024;
    public int PageCount {get;private set;}
    bool annotationModified,bookmarkModified,writeOutlines;
    IReadOnlyList<Bookmark> currentBookmarks=Array.Empty<Bookmark>();
    public bool Modified=>annotationModified||bookmarkModified||tagModified;
    public void SetBookmarks(IReadOnlyList<Bookmark> items,bool modified=true){currentBookmarks=items;writeOutlines=true;if(modified)bookmarkModified=true;lock(cacheGate){bookmarkPositions.Clear();bookmarksByPage=items.Where(b=>b.Page>=0&&b.Y!=null).GroupBy(b=>b.Page).ToDictionary(g=>g.Key,g=>g.ToArray());}}

    public Task<PdfInfo> OpenAsync(string path,CancellationToken token=default) => PdfScheduler.Run(()=>{
        lock(Gate){
            token.ThrowIfCancellationRequested();
            if(!initialized){FPDF_InitLibrary();initialized=true;}
            // Keep the existing document usable if opening the replacement fails.
            var timer=Stopwatch.StartNew();
            var replacement=FPDF_LoadDocument(path,null);
            if(replacement==IntPtr.Zero) throw new IOException(FPDF_GetLastError()==4?"PDF 需要密码，当前研究版暂未提供密码输入。":"无法打开 PDF，请确认文件完整且可读取。");
            if(token.IsCancellationRequested){FPDF_CloseDocument(replacement);token.ThrowIfCancellationRequested();}
            ReleaseDocument();
            document=replacement;sourcePath=Path.GetFullPath(path);sourceStamp=SourceStamp.Read(sourcePath);LastBackupPath=null;annotationModified=false;bookmarkModified=false;writeOutlines=false;tagModified=false;writeTags=false;currentPageTags=Array.Empty<PageTag>();PageCount=FPDF_GetPageCount(document);ClearCache();
            aspects=new double[PageCount];for(int i=0;i<PageCount;i++){if(FPDF_GetPageSizeByIndex(document,i,out double w,out double h)!=0&&w>0&&h>0)aspects[i]=h/w;else aspects[i]=1.414;}
            var bookmarks=new List<Bookmark>();var seen=new HashSet<IntPtr>();
            void Walk(IntPtr item,int depth){
                while(item!=IntPtr.Zero&&depth<64&&bookmarks.Count<20000&&seen.Add(item)){
                    var title=ReadString((b,n)=>FPDFBookmark_GetTitle(item,b,n));
                    var dest=FPDFBookmark_GetDest(document,item);
                    if(dest==IntPtr.Zero){var action=FPDFBookmark_GetAction(item);if(action!=IntPtr.Zero&&FPDFAction_GetType(action)==1)dest=FPDFAction_GetDest(document,action);}
                    int page=dest==IntPtr.Zero?-1:FPDFDest_GetDestPageIndex(document,dest);
                    {
                        if(page<0||page>=PageCount)page=-1;
                        double? x=null,y=null;
                        if(dest!=IntPtr.Zero&&FPDFDest_GetLocationInPage(dest,out int hasX,out int hasY,out _,out float dx,out float dy,out _)!=0){if(hasX!=0&&float.IsFinite(dx))x=dx;if(hasY!=0&&float.IsFinite(dy))y=dy;}
                        else if(dest!=IntPtr.Zero){var parameters=new float[4];uint type=FPDFDest_GetView(dest,out uint count,parameters);if((type==3||type==7)&&count>0&&float.IsFinite(parameters[0]))y=parameters[0];else if(type==5&&count>=4&&float.IsFinite(parameters[3]))y=parameters[3];}
                        bookmarks.Add(new(title,page,depth,x,y){SourceIndex=bookmarks.Count,Expanded=FPDFBookmark_GetCount(item)>=0});
                    }
                    Walk(FPDFBookmark_GetFirstChild(document,item),depth+1);item=FPDFBookmark_GetNextSibling(document,item);
                }
            }
            Walk(FPDFBookmark_GetFirstChild(document,IntPtr.Zero),0);
            BookmarkTree.Normalize(bookmarks);currentBookmarks=bookmarks;
            bookmarksByPage=bookmarks.Where(b=>b.Y!=null).GroupBy(b=>b.Page).ToDictionary(g=>g.Key,g=>g.ToArray());
            return new PdfInfo(PageCount,bookmarks,timer.Elapsed.TotalMilliseconds);
        }
    },token);

    public Task<RenderedPage> RenderAsync(int index,int width,CancellationToken token,bool background=false,bool allowLarger=false){
        token.ThrowIfCancellationRequested();width=Math.Clamp(width,128,2200);
        // Frozen bitmaps are managed objects. A cache hit needs neither native
        // PDFium access nor thread-pool scheduling, and never waits on its lock.
        lock(cacheGate){
            if(allowLarger){
                var sharper=cache.Values.Where(p=>p.Page==index&&p.Width>=width).OrderByDescending(p=>p.Width).FirstOrDefault();
                if(sharper!=null){lru.Remove((index,sharper.Width));lru.AddLast((index,sharper.Width));return Task.FromResult(sharper with {Milliseconds=0,Cached=true});}
            }
            if(cache.TryGetValue((index,width),out var ready)){
                lru.Remove((index,width));lru.AddLast((index,width));
                return Task.FromResult(ready with {Milliseconds=0,Cached=true});
            }
        }
        return PdfScheduler.Run(()=>{
        lock(Gate){
            token.ThrowIfCancellationRequested();EnsureDocument();
            width=Math.Clamp(width,128,2200);
            var key=(index,width);
            lock(cacheGate){if(allowLarger){var sharper=cache.Values.Where(p=>p.Page==index&&p.Width>=width).OrderByDescending(p=>p.Width).FirstOrDefault();if(sharper!=null){lru.Remove((index,sharper.Width));lru.AddLast((index,sharper.Width));return sharper with {Milliseconds=0,Cached=true};}}if(cache.TryGetValue(key,out var cached)){lru.Remove(key);lru.AddLast(key);return cached with {Milliseconds=0,Cached=true};}}
            var page=LoadPage(index);IntPtr bitmap=IntPtr.Zero;var timer=Stopwatch.StartNew();
            try{
                double pw=FPDF_GetPageWidth(page),ph=FPDF_GetPageHeight(page);
                if(pw<=0||ph<=0||!double.IsFinite(pw+ph))throw new IOException("PDF 页面尺寸无效。");
                int height=Math.Clamp((int)Math.Ceiling(width*ph/pw),1,10000);
                if(bookmarksByPage.TryGetValue(index,out var destinations))foreach(var bookmark in destinations){FPDF_PageToDevice(page,0,0,10000,10000,0,bookmark.X??0,bookmark.Y!.Value,out int bx,out int by);lock(cacheGate)bookmarkPositions[bookmark]=new Point(bx/10000.0,by/10000.0);}
                if((long)width*height*4>80L*1024*1024)throw new IOException("页面过长，请降低缩放比例。");
                bitmap=FPDFBitmap_Create(width,height,1);if(bitmap==IntPtr.Zero)throw new IOException("页面位图分配失败。");
                FPDFBitmap_FillRect(bitmap,0,0,width,height,0xffffffff);
                PauseCallback callback=_=>1;var pause=new Pause{version=1,NeedToPauseNow=callback};
                int state=FPDF_RenderPageBitmap_Start(bitmap,page,0,0,width,height,0,1,ref pause);
                try{
                    while(state==1){token.ThrowIfCancellationRequested();state=FPDF_RenderPage_Continue(page,ref pause);}
                    if(state!=2)throw new IOException("页面渲染失败。");
                    token.ThrowIfCancellationRequested();
                }finally{FPDF_RenderPage_Close(page);GC.KeepAlive(callback);}
                int stride=FPDFBitmap_GetStride(bitmap);var bytes=new byte[checked(stride*height)];Marshal.Copy(FPDFBitmap_GetBuffer(bitmap),bytes,0,bytes.Length);
                var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,bytes,stride);image.Freeze();
                var rendered=new RenderedPage(image,index,width,height,timer.Elapsed.TotalMilliseconds,false);
                lock(cacheGate){cache[key]=rendered;lru.AddLast(key);cacheBytes+=(long)stride*height;
                while(cacheBytes>CacheLimit&&lru.Count>1){var old=lru.First!.Value;lru.RemoveFirst();cacheBytes-=(long)cache[old].Width*cache[old].Height*4;cache.Remove(old);}}
                return rendered;
            }finally{if(bitmap!=IntPtr.Zero)FPDFBitmap_Destroy(bitmap);FPDF_ClosePage(page);}
        }
    },token,background);}

    public bool TryGetPreview(int page,out RenderedPage? result){lock(cacheGate){result=cache.Values.Where(p=>p.Page==page).OrderByDescending(p=>p.Width).FirstOrDefault();return result!=null;}}

    public Task<List<Annotation>> AnnotationsAsync(int index,CancellationToken token,bool background=false) => PdfScheduler.Run(()=>{
        lock(Gate){
            token.ThrowIfCancellationRequested();var page=LoadPage(index);
            try{
                var result=new List<Annotation>();int count=FPDFPage_GetAnnotCount(page);
                for(int i=0;i<count;i++){
                    var annotation=FPDFPage_GetAnnot(page,i);if(annotation==IntPtr.Zero)continue;
                    try{
                        int subtype=FPDFAnnot_GetSubtype(annotation);if(subtype==20)continue;
                        var title=ReadString((b,n)=>FPDFAnnot_GetStringValue(annotation,"Contents",b,n));
                        if(string.IsNullOrWhiteSpace(title))title=subtype==9?"文字高亮":subtype==1?"批注":"注释";
                        FPDFAnnot_GetRect(annotation,out var bounds);
                        result.Add(new(title,index,subtype,new(bounds.left,bounds.top,bounds.right,bounds.bottom)));
                    }finally{FPDFPage_CloseAnnot(annotation);}
                }
                return result;
            }finally{FPDF_ClosePage(page);}
        }
    },token,background);

    public Task<int> FindNextAsync(string query,int from,CancellationToken token) => PdfScheduler.Run(()=>{
        for(int step=0;step<PageCount;step++){
            token.ThrowIfCancellationRequested();int index=(from+step)%PageCount;
            lock(Gate){
                token.ThrowIfCancellationRequested();var page=LoadPage(index);IntPtr text=IntPtr.Zero,search=IntPtr.Zero;
                try{text=FPDFText_LoadPage(page);search=FPDFText_FindStart(text,query,0,0);if(search!=IntPtr.Zero&&FPDFText_FindNext(search)!=0)return index;}
                finally{if(search!=IntPtr.Zero)FPDFText_FindClose(search);if(text!=IntPtr.Zero)FPDFText_ClosePage(text);FPDF_ClosePage(page);}
            }
        }
        return -1;
    },token);

    public Task AnnotateAsync(int index,Point a,Point b,bool highlight,string contents,string? hoverData=null) => PdfScheduler.Run(()=>{
        lock(Gate){
            var page=LoadPage(index);IntPtr annotation=IntPtr.Zero,text=IntPtr.Zero;
            try{
                Point Convert(Point p){FPDF_DeviceToPage(page,0,0,10000,10000,0,(int)(p.X*10000),(int)(p.Y*10000),out double x,out double y);return new(x,y);}
                var p1=Convert(a);var p2=Convert(b);
                double left=Math.Min(p1.X,p2.X),right=Math.Max(p1.X,p2.X),bottom=Math.Min(p1.Y,p2.Y),top=Math.Max(p1.Y,p2.Y);
                var rectangles=new List<RectF>();
                if(highlight){
                    text=FPDFText_LoadPage(page);int count=FPDFText_CountChars(text);
                    for(int i=0;i<count;i++){
                        if(FPDFText_GetCharBox(text,i,out double l,out double r,out double bot,out double t)==0||r<=l||t-bot<1)continue;
                        if(l>=right||r<=left||bot>=top||t<=bottom)continue;
                        var current=new RectF{left=(float)l,right=(float)r,bottom=(float)bot,top=(float)t};
                        if(rectangles.Count>0){var prior=rectangles[^1];if(Math.Abs(prior.bottom-current.bottom)<3&&current.left-prior.right<12&&current.left>=prior.left-3){prior.right=Math.Max(prior.right,current.right);prior.top=Math.Max(prior.top,current.top);prior.bottom=Math.Min(prior.bottom,current.bottom);rectangles[^1]=prior;continue;}}
                        rectangles.Add(current);
                    }
                    if(rectangles.Count==0)rectangles.Add(new(){left=(float)left,right=(float)right,bottom=(float)bottom,top=(float)top});
                }
                annotation=FPDFPage_CreateAnnot(page,highlight?9:1);if(annotation==IntPtr.Zero)throw new IOException("无法创建注释。");
                var rect=new RectF{left=(float)left,right=(float)(highlight?right:left+20),bottom=(float)(highlight?bottom:top-20),top=(float)top};
                if(highlight&&rectangles.Count>0)rect=new(){left=rectangles.Min(r=>r.left),right=rectangles.Max(r=>r.right),bottom=rectangles.Min(r=>r.bottom),top=rectangles.Max(r=>r.top)};
                Check(FPDFAnnot_SetRect(annotation,ref rect));Check(FPDFAnnot_SetColor(annotation,0,highlight?255u:75u,highlight?221u:112u,highlight?72u:230u,highlight?110u:255u));
                if(highlight)foreach(var r in rectangles){var quad=new Quad{x1=r.left,y1=r.top,x2=r.right,y2=r.top,x3=r.left,y3=r.bottom,x4=r.right,y4=r.bottom};Check(FPDFAnnot_AppendAttachmentPoints(annotation,ref quad));}
                Check(FPDFAnnot_SetStringValue(annotation,"Contents",contents));Check(FPDFAnnot_SetStringValue(annotation,"T","FullOffice"));if(hoverData!=null){Check(FPDFAnnot_SetStringValue(annotation,"FlowOfficePeek",hoverData));Check(FPDFAnnot_SetFlags(annotation,32));}annotationModified=true;ClearCache();
            }finally{if(text!=IntPtr.Zero)FPDFText_ClosePage(text);if(annotation!=IntPtr.Zero)FPDFPage_CloseAnnot(annotation);FPDF_ClosePage(page);}
        }
    });

    public Task<Point> PagePointAsync(int index,Point normalized,CancellationToken token)=>PdfScheduler.Run(()=>{
        lock(Gate){token.ThrowIfCancellationRequested();var page=LoadPage(index);try{if(FPDF_DeviceToPage(page,0,0,100000,100000,0,(int)Math.Round(Math.Clamp(normalized.X,0,1)*100000),(int)Math.Round(Math.Clamp(normalized.Y,0,1)*100000),out double x,out double y)==0)throw new IOException("无法读取页面位置。");return new Point(x,y);}finally{FPDF_ClosePage(page);}}
    },token);

    public Task<Rect> LocateAsync(Annotation item) => PdfScheduler.Run(()=>{
        lock(Gate){var page=LoadPage(item.Page);try{FPDF_PageToDevice(page,0,0,10000,10000,0,item.Bounds.Left,item.Bounds.Top,out int x1,out int y1);FPDF_PageToDevice(page,0,0,10000,10000,0,item.Bounds.Right,item.Bounds.Bottom,out int x2,out int y2);return new Rect(new Point(Math.Min(x1,x2)/10000.0,Math.Min(y1,y2)/10000.0),new Point(Math.Max(x1,x2)/10000.0,Math.Max(y1,y2)/10000.0));}finally{FPDF_ClosePage(page);}}
    });

    public bool TryGetBookmarkPosition(Bookmark item,out Point? point){lock(cacheGate)return bookmarkPositions.TryGetValue(item,out point);}

    public Task<Point?> LocateBookmarkAsync(Bookmark item,CancellationToken token){
        token.ThrowIfCancellationRequested();if(item.Y==null)return Task.FromResult<Point?>(null);lock(cacheGate){if(bookmarkPositions.TryGetValue(item,out var position))return Task.FromResult(position);}
        return PdfScheduler.Run(()=>{
        lock(Gate){token.ThrowIfCancellationRequested();if(item.Y==null)return (Point?)null;var page=LoadPage(item.Page);try{FPDF_PageToDevice(page,0,0,10000,10000,0,item.X??0,item.Y.Value,out int x,out int y);return (Point?)new Point(x/10000.0,y/10000.0);}finally{FPDF_ClosePage(page);}}
    },token);}

    static void Check(int success){if(success==0)throw new IOException("PDF 注释写入失败。");}
    static string ReadString(Func<IntPtr,uint,uint> read){uint length=read(IntPtr.Zero,0);if(length<2||length>4*1024*1024)return "";var buffer=Marshal.AllocHGlobal((int)length);try{read(buffer,length);return Marshal.PtrToStringUni(buffer)??"";}finally{Marshal.FreeHGlobal(buffer);}}
    IntPtr LoadPage(int index){EnsureDocument();if(index<0||index>=PageCount)throw new ArgumentOutOfRangeException(nameof(index));var page=FPDF_LoadPage(document,index);if(page==IntPtr.Zero)throw new IOException("无法读取该页。");return page;}
    void EnsureDocument(){if(document==IntPtr.Zero)throw new InvalidOperationException("请先打开 PDF。");}
    void ClearCache(){lock(cacheGate){cache.Clear();lru.Clear();bookmarkPositions.Clear();cacheBytes=0;}}
    public void Dispose(){lock(Gate){ReleaseDocument();ClearCache();}}
}
