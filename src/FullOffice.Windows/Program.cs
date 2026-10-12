using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace FlowOfficeLab;

public static class Program
{
    public static byte[] HashFile(string path){using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);return SHA256.HashData(stream);}
    [STAThread]
    public static int Main(string[] args){
        var jsonOptions=new JsonSerializerOptions{WriteIndented=true};
        if(args.Length>=3&&args[0]=="--inplace-probe")return InPlaceNativeProbe.Run(args[1],args[2]);
        if(args.Length>=3&&args[0]=="--edit-probe")return EditingProbe.Run(args[1],args[2]);
        if(args.Length>=3&&args[0]=="--inspect-xlsx"){
            try{File.WriteAllText(args[2],JsonSerializer.Serialize(XlsxInspector.Inspect(args[1]),jsonOptions));return 0;}catch(Exception ex){File.WriteAllText(args[2],JsonSerializer.Serialize(new{error=ex.ToString()},jsonOptions));return 1;}
        }
        if(args.Length>=3&&(args[0]=="--benchmark"||args[0]=="--selftest")){
            try{
                string path=args[1];var before=HashFile(path);var measurements=new List<object>();
                using var pdf=new PdfEngine();PdfInfo? info=null;
                for(int repeat=0;repeat<5;repeat++){
                    var display=Stopwatch.StartNew();info=pdf.OpenAsync(path).GetAwaiter().GetResult();var first=pdf.RenderAsync(0,1200,CancellationToken.None).GetAwaiter().GetResult()!;
                    measurements.Add(new{Repeat=repeat,OpenMilliseconds=info.OpenMilliseconds,FirstRenderMilliseconds=first.Milliseconds,OpenAndBitmapReadyMilliseconds=display.Elapsed.TotalMilliseconds,Kind=repeat==0?"first library/document load; OS cache uncontrolled":"same-process reopen; OS cache uncontrolled"});
                }
                var jumps=new List<object>();foreach(int page in new[]{info!.Pages-1,info.Pages/2,0,info.Pages-1,info.Pages/2}){var timer=Stopwatch.StartNew();var rendered=pdf.RenderAsync(page,1200,CancellationToken.None).GetAwaiter().GetResult()!;jumps.Add(new{Page=page+1,Milliseconds=timer.Elapsed.TotalMilliseconds,RenderedMilliseconds=rendered.Milliseconds,rendered.Cached});}
                bool? persisted=null,visible=null;string? savedPath=null;
                if(args[0]=="--selftest"){
                    var first=pdf.RenderAsync(0,1200,CancellationToken.None).GetAwaiter().GetResult()!;var pixelsBefore=new byte[first.Width*first.Height*4];first.Image.CopyPixels(pixelsBefore,first.Width*4,0);
                    int originalCount=pdf.AnnotationsAsync(0,CancellationToken.None).GetAwaiter().GetResult().Count;
                    pdf.AnnotateAsync(0,new(.15,.20),new(.72,.24),true,"Verification highlight").GetAwaiter().GetResult();pdf.AnnotateAsync(0,new(.82,.30),new(.82,.30),false,"验证批注").GetAwaiter().GetResult();
                    var updated=pdf.RenderAsync(0,1200,CancellationToken.None).GetAwaiter().GetResult()!;var pixelsAfter=new byte[updated.Width*updated.Height*4];updated.Image.CopyPixels(pixelsAfter,updated.Width*4,0);visible=!pixelsBefore.SequenceEqual(pixelsAfter);
                    savedPath=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[2]))!,"selftest-annotated-copy.pdf");pdf.SaveCopyAsync(savedPath).GetAwaiter().GetResult();using var verify=new PdfEngine();verify.OpenAsync(savedPath).GetAwaiter().GetResult();var annotations=verify.AnnotationsAsync(0,CancellationToken.None).GetAwaiter().GetResult();persisted=annotations.Count==originalCount+2&&annotations.Any(a=>a.Title=="验证批注")&&annotations.Any(a=>a.Title=="Verification highlight");
                    if(persisted!=true||visible!=true)throw new Exception("Annotation rendering or persistence verification failed.");
                }
                using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool cancellationPassed=false;try{pdf.RenderAsync(0,1200,cancelled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){cancellationPassed=true;}
                var originalUnchanged=before.SequenceEqual(HashFile(path));if(!originalUnchanged||!cancellationPassed)throw new Exception("Source integrity or cancellation check failed.");
                using var process=Process.GetCurrentProcess();File.WriteAllText(args[2],JsonSerializer.Serialize(new{Engine="PDFium 7999 + .NET 8 / WPF",Pages=info!.Pages,Bookmarks=info.Bookmarks.Count,FileBytes=new FileInfo(path).Length,BitmapWidth=1200,Measurements=measurements,Jumps=jumps,AnnotationPersistence= persisted,AnnotationPixelsChanged=visible,AnnotationCopy=savedPath,OriginalUnchanged=originalUnchanged,CancellationPassed=cancellationPassed,ProcessWorkingSetBytes=process.WorkingSet64,Scope="Bitmap-ready timings exclude desktop launch, WPF first paint and storage cold-cache control. Synthetic workload; real-file acceptance pending."},jsonOptions));return 0;
            }catch(Exception ex){File.WriteAllText(args[2],JsonSerializer.Serialize(new{error=ex.ToString()},jsonOptions));return 1;}
        }
        string? file=args.FirstOrDefault(DocumentFormats.Supported);string? smoke=args.Length>=3&&(args[0]=="--ui-smoke"||args[0]=="--ui-probe")?args[2]:null;
        string? features=args.Length>=3&&args[0]=="--feature-probe"?args[2]:null;
        var application=new Application();application.DispatcherUnhandledException+=(_,e)=>{MessageBox.Show(e.Exception.Message,"FlowOffice",MessageBoxButton.OK,MessageBoxImage.Information);e.Handled=true;};application.Run(new WorkspaceWindow(file,smoke,args.Length>0&&args[0]=="--ui-probe",features,args));return 0;
    }
}
