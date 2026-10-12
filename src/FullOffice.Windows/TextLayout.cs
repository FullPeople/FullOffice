using System.Text;
using System.Text.RegularExpressions;

namespace FlowOfficeLab;

public record PdfGlyph(string Text,double X,double Y,double Width,double Height);

public static class TextLayout
{
    sealed record Line(List<PdfGlyph> Glyphs,double X,double Y,double Right,double Height,string Text);
    public static string Reflow(IReadOnlyList<PdfGlyph> glyphs){
        if(glyphs.Count==0)return "";
        var bands=new List<List<PdfGlyph>>();
        foreach(var glyph in glyphs.OrderBy(g=>g.Y+g.Height/2).ThenBy(g=>g.X)){
            var band=bands.LastOrDefault();if(band==null||Math.Abs((band.Average(g=>g.Y+g.Height/2))-(glyph.Y+glyph.Height/2))>Math.Max(glyph.Height,band.Average(g=>g.Height))*.45)bands.Add(new(){glyph});else band.Add(glyph);
        }
        var lines=new List<Line>();
        void Add(List<PdfGlyph> run){if(run.Count==0)return;var text=new StringBuilder();foreach(var g in run)text.Append(g.Text);string value=text.ToString().Trim();if(value!="")lines.Add(new(run,run.Min(g=>g.X),run.Min(g=>g.Y),run.Max(g=>g.X+g.Width),run.Average(g=>g.Height),value));}
        foreach(var band in bands){var run=new List<PdfGlyph>();foreach(var glyph in band.OrderBy(g=>g.X)){if(run.Count>0&&glyph.X-(run[^1].X+run[^1].Width)>Math.Max(glyph.Height,run[^1].Height)*2.8){Add(run);run=new();}run.Add(glyph);}Add(run);}
        if(lines.Count==0)return "";
        double leftEdge=lines.Min(l=>l.X),rightEdge=lines.Max(l=>l.Right),middle=(leftEdge+rightEdge)/2;
        var candidates=new List<double>();
        foreach(var group in lines.GroupBy(l=>Math.Round(l.Y/.006))){var sorted=group.OrderBy(l=>l.X).ToArray();for(int i=1;i<sorted.Length;i++){double gap=sorted[i].X-sorted[i-1].Right;if(gap>.018&&sorted[i-1].Right<middle+.1&&sorted[i].X>middle-.1)candidates.Add((sorted[i].X+sorted[i-1].Right)/2);}}
        double? divider=candidates.Count>=2?candidates.OrderBy(x=>x).ElementAt(candidates.Count/2):null;
        var blocks=new List<List<Line>>();var continuations=new HashSet<List<Line>>();
        if(divider==null)blocks.Add(lines.OrderBy(l=>l.Y).ThenBy(l=>l.X).ToList());
        else{
            var separators=lines.Where(l=>l.X<divider-.01&&l.Right>divider+.01).OrderBy(l=>l.Y).ToArray();double from=double.NegativeInfinity;
            foreach(var separator in separators){AddColumns(from,separator.Y-.002);blocks.Add(new(){separator});from=separator.Y+.002;}AddColumns(from,double.PositiveInfinity);
            void AddColumns(double low,double high){var subset=lines.Where(l=>l.Y>=low&&l.Y<high&&!separators.Contains(l)).ToArray();var left=subset.Where(l=>(l.X+l.Right)/2<divider).OrderBy(l=>l.Y).ToList();var right=subset.Where(l=>(l.X+l.Right)/2>=divider).OrderBy(l=>l.Y).ToList();if(left.Count>0)blocks.Add(left);if(right.Count>0){blocks.Add(right);if(left.Count>0)continuations.Add(right);}}
        }
        var output=new StringBuilder();
        List<Line>? previousBlock=null;
        foreach(var block in blocks){if(block.Count==0)continue;if(output.Length>0){var last=previousBlock?[^1];var first=block[0];bool continuation=continuations.Contains(block)&&last!=null&&!Regex.IsMatch(last.Text,@"[。！？.!?：:；;]$")&&last.Text.Length>8&&first.X<=block.Min(l=>l.X)+first.Height*.8&&Math.Max(last.Height,first.Height)<Math.Min(last.Height,first.Height)*1.2;if(continuation){if(!IsCjkEnd(last!.Text)&&!IsCjkStart(first.Text))output.Append(' ');}else output.Append("\n\n");}double columnRight=block.Max(l=>l.Right),columnLeft=block.Min(l=>l.X),columnWidth=columnRight-columnLeft;Line? previous=null;
            foreach(var line in block){
                if(previous!=null){double gap=line.Y-previous.Y;bool list=Regex.IsMatch(line.Text,@"^(?:[•·●◆■▶]|\d+[.、)]|[一二三四五六七八九十]+、)");bool paragraph=gap>Math.Max(line.Height,previous.Height)*1.9||line.X>columnLeft+line.Height*.85||line.Height>previous.Height*1.23||previous.Height>line.Height*1.23||list||(previous.Right<columnRight-columnWidth*.18&&Regex.IsMatch(previous.Text,@"[。！？.!?:：]$"));
                    if(paragraph)output.Append(list?'\n':"\n\n");else if(previous.Text.EndsWith('\u00ad'))output.Length--;else if(!IsCjkEnd(previous.Text)&&!IsCjkStart(line.Text)&&!char.IsWhiteSpace(output[^1]))output.Append(' ');
                }
                output.Append(line.Text);previous=line;
            }
            previousBlock=block;
        }
        return output.ToString().Trim();
    }
    static bool Cjk(char c)=>c>='\u2E80'&&c<='\u9FFF'||c>='\uF900'&&c<='\uFAFF'||"，。！？：；、（）《》“”".Contains(c);
    static bool IsCjkStart(string text)=>text.Length>0&&Cjk(text[0]);
    static bool IsCjkEnd(string text)=>text.Length>0&&Cjk(text[^1]);
}
