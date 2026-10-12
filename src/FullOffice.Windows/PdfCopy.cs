using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static FlowOfficeLab.PdfNative;

namespace FlowOfficeLab;

public sealed partial class PdfEngine
{
    public Task<string> ExtractTextAsync(int index,Rect? selected,bool raw,CancellationToken token)=>PdfScheduler.Run(()=>{
        lock(Gate){token.ThrowIfCancellationRequested();var page=LoadPage(index);IntPtr text=IntPtr.Zero;
            try{
                text=FPDFText_LoadPage(page);if(text==IntPtr.Zero)throw new IOException("无法提取这页的文字。");var area=selected??new Rect(0,0,1,1);
                if(raw){
                    FPDF_DeviceToPage(page,0,0,100000,100000,0,(int)(area.Left*100000),(int)(area.Top*100000),out double x1,out double y1);FPDF_DeviceToPage(page,0,0,100000,100000,0,(int)(area.Right*100000),(int)(area.Bottom*100000),out double x2,out double y2);
                    int required=FPDFText_GetBoundedText(text,Math.Min(x1,x2),Math.Max(y1,y2),Math.Max(x1,x2),Math.Min(y1,y2),null,0);if(required<=0)return "";var buffer=new char[required+1];int count=FPDFText_GetBoundedText(text,Math.Min(x1,x2),Math.Max(y1,y2),Math.Max(x1,x2),Math.Min(y1,y2),buffer,buffer.Length);return new string(buffer,0,Math.Min(count,buffer.Length)).TrimEnd('\0');
                }
                int total=FPDFText_CountChars(text);var glyphs=new List<PdfGlyph>();bool space=false;
                for(int i=0;i<total;i++){
                    if(i%128==0)token.ThrowIfCancellationRequested();uint unicode=FPDFText_GetUnicode(text,i);if(unicode==10||unicode==13){space=false;continue;}if(unicode==32||unicode==9||unicode==0xA0){space=true;continue;}if(unicode<32||unicode>0x10FFFF||unicode>=0xD800&&unicode<=0xDFFF)continue;
                    if(FPDFText_GetCharBox(text,i,out double left,out double right,out double bottom,out double top)==0||right<=left||top<=bottom)continue;
                    FPDF_PageToDevice(page,0,0,100000,100000,0,left,top,out int ax,out int ay);FPDF_PageToDevice(page,0,0,100000,100000,0,right,bottom,out int bx,out int by);
                    double x=Math.Min(ax,bx)/100000.0,y=Math.Min(ay,by)/100000.0,w=Math.Abs(bx-ax)/100000.0,h=Math.Abs(by-ay)/100000.0;
                    if(!area.Contains(new Point(x+w/2,y+h/2))){space=false;continue;}
                    if(FPDFText_GetCharOrigin(text,i,out double ox,out double oy)!=0){double font=FPDFText_GetFontSize(text,i);double scale=FPDFText_GetMatrix(text,i,out var matrix)!=0?Math.Sqrt(matrix.c*matrix.c+matrix.d*matrix.d):1;FPDF_PageToDevice(page,0,0,100000,100000,0,ox,oy,out _,out int baseline);FPDF_PageToDevice(page,0,0,100000,100000,0,ox,oy+font*scale,out _,out int cap);double fontHeight=Math.Abs(baseline-cap)/100000.0;if(fontHeight>.0001){h=fontHeight;y=baseline/100000.0-h;}}
                    string character=(space?" ":"")+char.ConvertFromUtf32((int)unicode);space=false;glyphs.Add(new(character,x,y,w,h));
                }
                return TextLayout.Reflow(glyphs);
            }finally{if(text!=IntPtr.Zero)FPDFText_ClosePage(text);FPDF_ClosePage(page);}
        }
    },token);
    public Task<BitmapSource?> ExtractImageAsync(int index,Point position,CancellationToken token)=>PdfScheduler.Run(()=>{
        lock(Gate){token.ThrowIfCancellationRequested();var page=LoadPage(index);IntPtr bitmap=IntPtr.Zero;
            try{
                FPDF_DeviceToPage(page,0,0,100000,100000,0,(int)(position.X*100000),(int)(position.Y*100000),out double x,out double y);
                IntPtr Find(IntPtr container,int count,Point point,bool form,int depth){if(depth>16)return IntPtr.Zero;for(int i=count-1;i>=0;i--){token.ThrowIfCancellationRequested();var obj=form?FPDFFormObj_GetObject(container,i):FPDFPage_GetObject(container,i);if(FPDFPageObj_GetBounds(obj,out float l,out float b,out float r,out float t)==0||point.X<l||point.X>r||point.Y<b||point.Y>t)continue;int type=FPDFPageObj_GetType(obj);if(type==3)return obj;if(type==5&&FPDFPageObj_GetMatrix(obj,out var m)!=0){double determinant=m.a*m.d-m.b*m.c;if(Math.Abs(determinant)<1e-12)continue;var local=new Point((m.d*(point.X-m.e)-m.c*(point.Y-m.f))/determinant,(-m.b*(point.X-m.e)+m.a*(point.Y-m.f))/determinant);var found=Find(obj,FPDFFormObj_CountObjects(obj),local,true,depth+1);if(found!=IntPtr.Zero)return found;}}return IntPtr.Zero;}
                var image=Find(page,FPDFPage_CountObjects(page),new(x,y),false,0);if(image==IntPtr.Zero)return null;bitmap=FPDFImageObj_GetBitmap(image);if(bitmap==IntPtr.Zero)return null;
                int width=FPDFBitmap_GetWidth(bitmap),height=FPDFBitmap_GetHeight(bitmap),stride=FPDFBitmap_GetStride(bitmap),format=FPDFBitmap_GetFormat(bitmap);if((long)stride*height>120L*1024*1024)throw new IOException("原图过大，请框选所需区域后复制。");
                var pixels=new byte[stride*height];Marshal.Copy(FPDFBitmap_GetBuffer(bitmap),pixels,0,pixels.Length);PixelFormat pixelFormat=format switch{1=>PixelFormats.Gray8,2=>PixelFormats.Bgr24,3=>PixelFormats.Bgr32,_=>PixelFormats.Bgra32};var result=BitmapSource.Create(width,height,96,96,pixelFormat,null,pixels,stride);result.Freeze();return result;
            }finally{if(bitmap!=IntPtr.Zero)FPDFBitmap_Destroy(bitmap);FPDF_ClosePage(page);}
        }
    },token);
}

public sealed partial class PdfReaderView
{
    Rect? copyRegion;
    bool copyDrag;
    void ClearCopySelection(){copyRegion=null;selection.Visibility=Visibility.Collapsed;}
    void SetCopySelection(Point start,Point end){copyRegion=new Rect(new Point(Math.Min(start.X,end.X),Math.Min(start.Y,end.Y)),new Point(Math.Max(start.X,end.X),Math.Max(start.Y,end.Y)));RenderCopySelection();status.Text="已选中区域：Ctrl+C 复制文字，Ctrl+Shift+C 复制图片";}
    void RenderCopySelection(){if(copyRegion is not Rect r)return;DrawSelection(new(r.Left*pageImage.Width,r.Top*pageImage.Height),new(r.Right*pageImage.Width,r.Bottom*pageImage.Height));selection.Visibility=Visibility.Visible;}
    async Task CopyText(bool raw){if(currentFile==""||displayedPage!=currentPage||copying)return;int doc=generation,page=currentPage;var region=copyRegion;copying=true;status.Text="正在提取文字…";try{var text=await engine.ExtractTextAsync(page,region,raw,documentCancellation.Token);bool recognized=false;if(!raw&&OcrCopy.LooksBroken(text)){status.Text="文字层无法正常读取，正在识别选区…";text=await RecognizeText(page,region,documentCancellation.Token);recognized=true;}if(doc!=generation)return;if(string.IsNullOrWhiteSpace(text)){status.Text="这个区域没有可复制的文字，可以复制图片";return;}Clipboard.SetText(text);status.Text=recognized?"文字已识别并复制，已按段落合并换行":raw?"文字已复制，保留原始换行":"文字已复制，已按段落合并换行";}finally{copying=false;}}
    async Task<string> RecognizeText(int page,Rect? region,CancellationToken token){var rendered=await engine.RenderAsync(page,2200,token,allowLarger:true);var cropped=CropImage(rendered.Image,region??new Rect(0,0,1,1));return await OcrCopy.RecognizeAsync(cropped,token);}
    async Task CopyRecognizedText(){if(copying)return;copying=true;int doc=generation,page=currentPage;status.Text="正在识别文字…";try{var text=await RecognizeText(page,copyRegion,documentCancellation.Token);if(doc!=generation)return;if(string.IsNullOrWhiteSpace(text)){status.Text="没有识别到文字，可以复制图片";return;}Clipboard.SetText(text);status.Text="识别文字已复制";}finally{copying=false;}}
    void CopyImage(){if(displayedPage!=currentPage||pageImage.Source is not BitmapSource image)return;if(engine.TryGetPreview(currentPage,out var rendered)&&rendered!=null)image=rendered.Image;if(copyRegion is Rect r){int x=Math.Clamp((int)Math.Floor(r.X*image.PixelWidth),0,image.PixelWidth-1),y=Math.Clamp((int)Math.Floor(r.Y*image.PixelHeight),0,image.PixelHeight-1),w=Math.Clamp((int)Math.Ceiling(r.Width*image.PixelWidth),1,image.PixelWidth-x),h=Math.Clamp((int)Math.Ceiling(r.Height*image.PixelHeight),1,image.PixelHeight-y);image=new CroppedBitmap(image,new Int32Rect(x,y,w,h));image.Freeze();}Clipboard.SetImage(image);status.Text=copyRegion==null?"页面图片已复制":"选区图片已复制";}
    async Task CopyEmbeddedImage(){int doc=generation;var image=await engine.ExtractImageAsync(currentPage,contextPosition,documentCancellation.Token);if(doc!=generation)return;if(image==null){status.Text="此处没有独立图片，可以框选区域后复制";return;}Clipboard.SetImage(image);status.Text=$"原图已复制 · {image.PixelWidth} × {image.PixelHeight}";}
}
