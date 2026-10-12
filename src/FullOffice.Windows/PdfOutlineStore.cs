using System.Text.Json;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace FlowOfficeLab;

public record OutlineAppearance(int Index,string? Color,bool Bold,bool Italic);

public static class PdfOutlineStore
{
    static List<PdfDictionary> ReadNodes(PdfDocument pdf){
        var result=new List<PdfDictionary>();var seen=new HashSet<PdfDictionary>();
        void Walk(PdfDictionary? node,int depth){while(node!=null&&depth<64&&result.Count<20000&&seen.Add(node)){result.Add(node);Walk(node.Elements.GetDictionary("/First"),depth+1);node=node.Elements.GetDictionary("/Next");}}
        Walk(pdf.Internals.Catalog.Elements.GetDictionary("/Outlines")?.Elements.GetDictionary("/First"),0);return result;
    }
    public static async Task<List<OutlineAppearance>> ReadAppearanceAsync(string path,CancellationToken token)=>(await ReadMetadataAsync(path,token)).Outlines;
    public static Task<DocumentMetadata> ReadMetadataAsync(string path,CancellationToken token)=>Task.Run(()=>{
        token.ThrowIfCancellationRequested();using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);using var pdf=PdfReader.Open(input,PdfDocumentOpenMode.Import);var result=new List<OutlineAppearance>();var nodes=ReadNodes(pdf);
        for(int i=0;i<nodes.Count;i++){
            token.ThrowIfCancellationRequested();var color=nodes[i].Elements.GetArray("/C");string? hex=null;
            if(color?.Elements.Count>=3){int Component(int j)=>(int)Math.Round(Math.Clamp(color.Elements.GetReal(j),0,1)*255);hex=$"#{Component(0):X2}{Component(1):X2}{Component(2):X2}";}
            int flags=nodes[i].Elements.GetInteger("/F");result.Add(new(i,hex,(flags&2)!=0,(flags&1)!=0));
        }
        var tags=TagColors.Read(pdf.Internals.Catalog.Elements.GetString("/FlowOfficePageTags"),pdf.PageCount);return new DocumentMetadata(result,tags);
    },token);

    public static void Rewrite(string input,string output,IReadOnlyList<Bookmark> bookmarks,IReadOnlyList<PageTag>? tags=null,bool rewriteOutlines=true){
        using var inputStream=new FileStream(input,FileMode.Open,FileAccess.Read,FileShare.Read);using var pdf=PdfReader.Open(inputStream,PdfDocumentOpenMode.Modify);var source=ReadNodes(pdf);var catalog=pdf.Internals.Catalog;
        if(tags!=null)catalog.Elements["/FlowOfficePageTags"]=new PdfString(JsonSerializer.Serialize(tags),PdfStringEncoding.Unicode);
        if(!rewriteOutlines){pdf.Save(output);return;}
        if(bookmarks.Count==0){catalog.Elements.Remove("/Outlines");pdf.Save(output);return;}
        var root=new PdfDictionary(pdf);root.Elements.SetName("/Type","/Outlines");pdf.Internals.AddObject(root);
        var dictionaries=new PdfDictionary[bookmarks.Count];var children=new Dictionary<int,List<int>>{{-1,new()}};var parents=new int[bookmarks.Count];var stack=new Stack<int>();
        for(int i=0;i<bookmarks.Count;i++){
            var b=bookmarks[i];while(stack.Count>0&&bookmarks[stack.Peek()].Depth>=b.Depth)stack.Pop();int parent=stack.Count>0?stack.Peek():-1;parents[i]=parent;children[parent].Add(i);children[i]=new();stack.Push(i);
            var node=new PdfDictionary(pdf);pdf.Internals.AddObject(node);dictionaries[i]=node;
            // Preserve destinations, actions and unknown metadata from existing entries.
            if(b.SourceIndex>=0&&b.SourceIndex<source.Count)foreach(var pair in source[b.SourceIndex].Elements)if(pair.Key is not ("/Parent" or "/Prev" or "/Next" or "/First" or "/Last" or "/Count"))node.Elements[pair.Key]=pair.Value;
            node.Elements["/Title"]=new PdfString(b.Title,PdfStringEncoding.Unicode);
            if(b.ColorEdited||b.SourceIndex<0){
                if(b.ColorHex!=null){var c=b.ColorHex.TrimStart('#');node.Elements["/C"]=new PdfArray(pdf,new PdfReal(Convert.ToInt32(c[..2],16)/255.0),new PdfReal(Convert.ToInt32(c[2..4],16)/255.0),new PdfReal(Convert.ToInt32(c[4..6],16)/255.0));}else node.Elements.Remove("/C");
            }
            if(b.StyleEdited||b.SourceIndex<0)node.Elements.SetInteger("/F",(node.Elements.GetInteger("/F")&~3)|(b.Bold?2:0)|(b.Italic?1:0));
            if(b.DestinationEdited||b.SourceIndex<0){
                node.Elements.Remove("/A");node.Elements.Remove("/Dest");
                if(b.Page>=0&&b.Page<pdf.PageCount){var destination=new PdfArray(pdf);destination.Elements.Add(pdf.Pages[b.Page].Reference!);destination.Elements.Add(new PdfName("/XYZ"));destination.Elements.Add(b.X.HasValue?new PdfReal(b.X.Value):PdfNull.Value);destination.Elements.Add(b.Y.HasValue?new PdfReal(b.Y.Value):PdfNull.Value);destination.Elements.Add(PdfNull.Value);node.Elements["/Dest"]=destination;}
            }
        }
        int VisibleChildren(int parent){int count=0;foreach(int child in children[parent]){count++;if(bookmarks[child].Expanded)count+=VisibleChildren(child);}return count;}
        foreach(var pair in children){
            var node=pair.Key<0?root:dictionaries[pair.Key];var list=pair.Value;if(list.Count==0)continue;
            node.Elements["/First"]=dictionaries[list[0]].Reference!;node.Elements["/Last"]=dictionaries[list[^1]].Reference!;
            int visible=VisibleChildren(pair.Key);node.Elements.SetInteger("/Count",pair.Key<0||bookmarks[pair.Key].Expanded?visible:-visible);
            for(int j=0;j<list.Count;j++){var child=dictionaries[list[j]];child.Elements["/Parent"]=node.Reference!;if(j>0)child.Elements["/Prev"]=dictionaries[list[j-1]].Reference!;if(j+1<list.Count)child.Elements["/Next"]=dictionaries[list[j+1]].Reference!;}
        }
        catalog.Elements["/Outlines"]=root.Reference!;pdf.Save(output);
    }
}
