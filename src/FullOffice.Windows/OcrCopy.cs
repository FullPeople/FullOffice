using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FlowOfficeLab;

public static class OcrCopy
{
    public static bool LooksBroken(string text){if(string.IsNullOrWhiteSpace(text))return true;int suspect=text.Count(c=>c=='\uFFFD'||c>='\u0370'&&c<='\u052F'||c<32&&c!='\n'&&c!='\r'&&c!='\t');return suspect>Math.Max(6,text.Length*.06);}
    public static Task<string> RecognizeAsync(BitmapSource source,CancellationToken token)=>Task.Run(async()=>{
        token.ThrowIfCancellationRequested();var language=OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l=>l.LanguageTag.StartsWith("zh",StringComparison.OrdinalIgnoreCase));var engine=language==null?OcrEngine.TryCreateFromUserProfileLanguages():OcrEngine.TryCreateFromLanguage(language);if(engine==null)throw new IOException("系统没有可用的文字识别语言。请在 Windows 语言设置中安装中文识别组件。");
        int maximum=(int)OcrEngine.MaxImageDimension;if(source.PixelWidth>maximum||source.PixelHeight>maximum){double scale=(double)maximum/Math.Max(source.PixelWidth,source.PixelHeight);var resized=new TransformedBitmap(source,new ScaleTransform(scale,scale));resized.Freeze();source=resized;}
        var pixels=new byte[source.PixelWidth*source.PixelHeight*4];source.CopyPixels(pixels,source.PixelWidth*4,0);using var bitmap=new SoftwareBitmap(BitmapPixelFormat.Bgra8,source.PixelWidth,source.PixelHeight,BitmapAlphaMode.Premultiplied);using var writer=new DataWriter();writer.WriteBytes(pixels);bitmap.CopyFromBuffer(writer.DetachBuffer());token.ThrowIfCancellationRequested();var result=await engine.RecognizeAsync(bitmap);token.ThrowIfCancellationRequested();var glyphs=new List<PdfGlyph>();
        foreach(var line in result.Lines){if(line.Words.Count==0)continue;var heights=line.Words.Select(w=>w.BoundingRect.Height).OrderBy(h=>h).ToArray();double lineHeight=heights[heights.Length/2];double lineTop=line.Words.Where(w=>w.BoundingRect.Height>=lineHeight*.8).Average(w=>w.BoundingRect.Y);string? previous=null;foreach(var word in line.Words){var rect=word.BoundingRect;string text=word.Text;if(previous!=null&&previous.Length>0&&text.Length>0&&previous[^1]<128&&text[0]<128)text=" "+text;glyphs.Add(new(text,rect.X/source.PixelWidth,lineTop/source.PixelHeight,rect.Width/source.PixelWidth,lineHeight/source.PixelHeight));previous=word.Text;}}
        return TextLayout.Reflow(glyphs);
    },token);
}
