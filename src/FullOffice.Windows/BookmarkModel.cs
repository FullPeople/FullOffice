using System.Windows.Media;

namespace FlowOfficeLab;

public sealed class Bookmark
{
    public Bookmark(string title,int page,int depth,double? x=null,double? y=null){Title=title;Page=page;Depth=depth;X=x;Y=y;}
    public Guid Id {get;set;}=Guid.NewGuid();
    public int SourceIndex {get;set;}=-1;
    public string Title {get;set;}
    public int Page {get;set;}
    public int Depth {get;set;}
    public double? X {get;set;}
    public double? Y {get;set;}
    public string? ColorHex {get;set;}
    public bool Bold {get;set;}
    public bool Italic {get;set;}
    public bool Expanded {get;set;}=true;
    public bool DestinationEdited {get;set;}
    public bool PresentationLoaded {get;set;}
    public bool ColorEdited {get;set;}
    public bool StyleEdited {get;set;}
    public double VisualY {get;set;}
    public bool HasChildren {get;set;}
    public int DescendantCount {get;set;}
    public bool CanNavigate=>Page>=0;
    public string Label=>Title;
    public Bookmark Clone(bool newIdentity=false)=>new(Title,Page,Depth,X,Y){Id=newIdentity?Guid.NewGuid():Id,SourceIndex=SourceIndex,ColorHex=ColorHex,Bold=Bold,Italic=Italic,Expanded=Expanded,DestinationEdited=DestinationEdited,PresentationLoaded=PresentationLoaded,ColorEdited=ColorEdited,StyleEdited=StyleEdited,VisualY=VisualY,HasChildren=HasChildren,DescendantCount=DescendantCount};
    public Brush TextBrush=>ColorHex==null?new SolidColorBrush(Color.FromRgb(36,46,64)):new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
}

public static class BookmarkTree
{
    public static int End(IReadOnlyList<Bookmark> items,int index){int end=index+1;while(end<items.Count&&items[end].Depth>items[index].Depth)end++;return end;}
    public static int Parent(IReadOnlyList<Bookmark> items,int index){for(int p=index-1;p>=0;p--)if(items[p].Depth<items[index].Depth)return p;return -1;}
    public static int PreviousSibling(IReadOnlyList<Bookmark> items,int index){for(int p=index-1;p>=0;p--){if(items[p].Depth<items[index].Depth)return -1;if(items[p].Depth==items[index].Depth)return p;}return -1;}
    public static int NextSibling(IReadOnlyList<Bookmark> items,int index){int next=End(items,index);return next<items.Count&&items[next].Depth==items[index].Depth?next:-1;}
    public static void Normalize(List<Bookmark> items){
        var stack=new Stack<int>();
        for(int i=0;i<items.Count;i++){
            items[i].Depth=Math.Clamp(items[i].Depth,0,i==0?0:items[i-1].Depth+1);
            items[i].HasChildren=i+1<items.Count&&items[i+1].Depth>items[i].Depth;
            items[i].DescendantCount=0;
            while(stack.Count>0&&items[stack.Peek()].Depth>=items[i].Depth){int parent=stack.Pop();items[parent].DescendantCount=i-parent-1;}
            stack.Push(i);
        }
        while(stack.Count>0){int parent=stack.Pop();items[parent].DescendantCount=items.Count-parent-1;}
    }
}
