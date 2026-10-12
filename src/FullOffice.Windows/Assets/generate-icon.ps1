$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$drawing=[System.Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'FlowOffice.xaml')))
$sizes=@(16,24,32,48,64,128,256)
$images=@()
foreach($size in $sizes){
    $visual=[System.Windows.Media.DrawingVisual]::new()
    $context=$visual.RenderOpen()
    $context.DrawImage($drawing,[System.Windows.Rect]::new(0,0,$size,$size))
    $context.Close()
    $bitmap=[System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder=[System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $memory=[IO.MemoryStream]::new();$encoder.Save($memory);$images+=,@($size,$memory.ToArray());$memory.Dispose()
    if($size -eq 64 -or $size -eq 256){[IO.File]::WriteAllBytes((Join-Path $PSScriptRoot "icon-$size.png"),$images[-1][1])}
}
$stream=[IO.File]::Create((Join-Path $PSScriptRoot 'FlowOffice.ico'));$writer=[IO.BinaryWriter]::new($stream)
try{
    $writer.Write([UInt16]0);$writer.Write([UInt16]1);$writer.Write([UInt16]$sizes.Count);$offset=6+16*$sizes.Count
    foreach($image in $images){$dimension=if($image[0] -eq 256){0}else{$image[0]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([UInt16]1);$writer.Write([UInt16]32);$writer.Write([UInt32]$image[1].Length);$writer.Write([UInt32]$offset);$offset+=$image[1].Length}
    foreach($image in $images){$writer.Write([byte[]]$image[1])}
}finally{$writer.Dispose();$stream.Dispose()}
