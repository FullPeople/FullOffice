using System.Diagnostics;
using System.Text.Json;
using System.Windows;

namespace FlowOfficeLab;

public static class InPlaceNativeProbe
{
    public static int Run(string input,string directory){
        Directory.CreateDirectory(directory);var checks=new Dictionary<string,bool>();var inputHash=Program.HashFile(input);string current=Path.Combine(directory,"large-current.pdf");File.Copy(input,current,true);var timer=Stopwatch.StartNew();
        try{
            using(var pdf=new PdfEngine()){
                var info=pdf.OpenAsync(current).GetAwaiter().GetResult();var appearance=PdfOutlineStore.ReadMetadataAsync(current,CancellationToken.None).GetAwaiter().GetResult();foreach(var style in appearance.Outlines){var b=info.Bookmarks.FirstOrDefault(b=>b.SourceIndex==style.Index);if(b==null)continue;b.ColorHex=style.Color;b.Bold=style.Bold;b.Italic=style.Italic;b.PresentationLoaded=true;}
                var tags=appearance.Tags.ToList();var tag=new PageTag(Guid.NewGuid(),"写回验证",Math.Max(0,info.Pages-1),TagColors.Next(tags.Select(t=>t.Color)),.32);tags.Add(tag);pdf.SetPageTags(tags);pdf.SetBookmarks(info.Bookmarks,false);
                pdf.AnnotateAsync(0,new(.15,.18),new(.15,.18),false,"写回保存验证批注").GetAwaiter().GetResult();var save=Stopwatch.StartNew();pdf.SaveInPlaceAsync().GetAwaiter().GetResult();double savedMs=save.Elapsed.TotalMilliseconds;
                checks["InPlaceSaveCompletesAndClearsModified"]=!pdf.Modified;checks["RecoveryBackupMatchesOriginal"]=inputHash.SequenceEqual(Program.HashFile(pdf.LastBackupPath!));var restored=PdfOutlineStore.ReadMetadataAsync(current,CancellationToken.None).GetAwaiter().GetResult();checks["ExactTagMetadataReopened"]=tags.SequenceEqual(restored.Tags);
                using var verify=new PdfEngine();var reopened=verify.OpenAsync(current).GetAwaiter().GetResult();checks["EveryBookmarkTitlePageDepthAndDestinationPreserved"]=reopened.Pages==info.Pages&&reopened.Bookmarks.Select(b=>(b.Title,b.Page,b.Depth,b.X,b.Y)).SequenceEqual(info.Bookmarks.Select(b=>(b.Title,b.Page,b.Depth,b.X,b.Y)));checks["EveryBookmarkColorAndStylePreserved"]=appearance.Outlines.SequenceEqual(restored.Outlines);checks["AnnotationReopenedFromSameFile"]=verify.AnnotationsAsync(0,CancellationToken.None).GetAwaiter().GetResult().Any(a=>a.Title=="写回保存验证批注");checks["ProtectedUserInputUnchanged"]=inputHash.SequenceEqual(Program.HashFile(input));
                File.WriteAllText(Path.Combine(directory,"inplace-probe.json"),JsonSerializer.Serialize(new{Passed=checks.Values.All(v=>v),Checks=checks,File=Path.GetFileName(input),Pages=info.Pages,Bookmarks=info.Bookmarks.Count,InPlaceSaveMs=savedMs,ElapsedMs=timer.Elapsed.TotalMilliseconds,Scope="Real user PDF cloned into the probe directory; only this clone is edited and replaced. Native save/reopen and all outline metadata checked. Every page body and image stream checked separately."},new JsonSerializerOptions{WriteIndented=true}));
                return checks.Values.All(v=>v)?0:1;
            }
        }catch(Exception ex){File.WriteAllText(Path.Combine(directory,"inplace-probe.json"),JsonSerializer.Serialize(new{Error=ex.ToString(),Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}
