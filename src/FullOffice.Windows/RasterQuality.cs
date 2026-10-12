using System.Numerics;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowOfficeLab;

internal static class RasterQuality
{
    sealed record Tap(int[] Indices,float[] Weights);
    static readonly ConditionalWeakTable<BitmapSource,Dictionary<(int,int),BitmapSource>> displayCache=new();
    static readonly object cacheGate=new();

    public static int TargetWidth(double displayWidth, double dpiScale, bool fitWholePage)
    {
        // Fit-page text is rasterized at twice the physical display resolution.
        // Downsample once on a worker, then display at the physical pixel grid.
        double samples = fitWholePage ? 2 : 1;
        double pixels=displayWidth*dpiScale*samples;
        return Math.Clamp((int)(fitWholePage?Math.Ceiling(pixels):Math.Floor(pixels)),128,2200);
    }

    public static bool TryGetDisplay(BitmapSource source,int width,int height,out BitmapSource? bitmap)
    {
        lock(cacheGate){bitmap=null;return displayCache.TryGetValue(source,out var sizes)&&sizes.TryGetValue((width,height),out bitmap);}
    }

    public static Task<BitmapSource> DownsampleAsync(BitmapSource source,int width,int height,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(source.PixelWidth==width&&source.PixelHeight==height)return Task.FromResult(source);
        if(TryGetDisplay(source,width,height,out var ready))return Task.FromResult(ready!);
        return Task.Run(()=>{
            token.ThrowIfCancellationRequested();
            int sourceWidth=source.PixelWidth,sourceHeight=source.PixelHeight,stride=checked(sourceWidth*4);
            var bytes=new byte[checked(stride*sourceHeight)];source.CopyPixels(bytes,stride,0);
            var horizontal=Taps(sourceWidth,width);var vertical=Taps(sourceHeight,height);
            var output=new byte[checked(width*height*4)];
            var rows=new Dictionary<int,Vector4[]>();var reusable=new Stack<Vector4[]>();
            for(int y=0;y<height;y++){
                token.ThrowIfCancellationRequested();var tap=vertical[y];
                foreach(int old in rows.Keys.Where(i=>i<tap.Indices[0]).ToArray()){reusable.Push(rows[old]);rows.Remove(old);}
                foreach(int rowIndex in tap.Indices){
                    if(rows.ContainsKey(rowIndex))continue;
                    var row=reusable.Count>0?reusable.Pop():new Vector4[width];
                    for(int x=0;x<width;x++){
                        var kernel=horizontal[x];var value=Vector4.Zero;
                        for(int i=0;i<kernel.Indices.Length;i++){
                            int offset=rowIndex*stride+kernel.Indices[i]*4;
                            value+=new Vector4(bytes[offset],bytes[offset+1],bytes[offset+2],bytes[offset+3])*kernel.Weights[i];
                        }
                        row[x]=value;
                    }
                    rows.Add(rowIndex,row);
                }
                for(int x=0;x<width;x++){
                    var value=Vector4.Zero;
                    for(int i=0;i<tap.Indices.Length;i++)value+=rows[tap.Indices[i]][x]*tap.Weights[i];
                    int offset=(y*width+x)*4;
                    output[offset]=Channel(value.X);output[offset+1]=Channel(value.Y);output[offset+2]=Channel(value.Z);output[offset+3]=Channel(value.W);
                }
            }
            token.ThrowIfCancellationRequested();
            var result=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,output,width*4);result.Freeze();
            lock(cacheGate){var sizes=displayCache.GetOrCreateValue(source);if(sizes.Count>=3)sizes.Clear();sizes[(width,height)]=result;}
            return result;
        },token);
    }

    static byte Channel(float value)=>(byte)Math.Clamp((int)MathF.Round(value),0,255);
    static Tap[] Taps(int input,int output)
    {
        double ratio=input/(double)output,scale=Math.Max(1,ratio),support=3*scale;
        var result=new Tap[output];
        for(int pixel=0;pixel<output;pixel++){
            double center=(pixel+.5)*ratio-.5;
            int first=Math.Max(0,(int)Math.Ceiling(center-support)),last=Math.Min(input-1,(int)Math.Floor(center+support));
            var indices=new int[last-first+1];var weights=new float[indices.Length];double sum=0;
            for(int i=0;i<indices.Length;i++){int sample=first+i;indices[i]=sample;double distance=(sample-center)/scale;double weight=Math.Abs(distance)<1e-8?1:Math.Abs(distance)>=3?0:Math.Sin(Math.PI*distance)*Math.Sin(Math.PI*distance/3)/(Math.PI*Math.PI*distance*distance/3);weights[i]=(float)weight;sum+=weight;}
            for(int i=0;i<weights.Length;i++)weights[i]/=(float)sum;
            result[pixel]=new(indices,weights);
        }
        return result;
    }
}
