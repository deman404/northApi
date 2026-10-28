$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
[xml]$svg=Get-Content (Join-Path $PSScriptRoot '..\assets\compass.svg') -Raw
$geometry=[Windows.Media.Geometry]::Parse($svg.svg.path.d)
$sizes=@(16,24,32,48,64,128,256);$frames=@()
foreach($size in $sizes){
 $visual=New-Object Windows.Media.DrawingVisual;$drawing=$visual.RenderOpen()
 $drawing.DrawEllipse([Windows.Media.BrushConverter]::new().ConvertFromString('#FF6C37'),$null,[Windows.Point]::new($size/2,$size/2),$size/2,$size/2)
 $scale=$size*0.60/24.052;$group=New-Object Windows.Media.TransformGroup
 $group.Children.Add([Windows.Media.TranslateTransform]::new(0.026,0.026));$group.Children.Add([Windows.Media.ScaleTransform]::new($scale,$scale));$group.Children.Add([Windows.Media.TranslateTransform]::new($size*0.20,$size*0.20))
 $drawing.PushTransform($group);$drawing.DrawGeometry([Windows.Media.Brushes]::White,$null,$geometry);$drawing.Pop();$drawing.Close()
 $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($visual)
 $encoder=New-Object Windows.Media.Imaging.PngBitmapEncoder;$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$stream=New-Object IO.MemoryStream;$encoder.Save($stream);$frames+=,($stream.ToArray());$stream.Dispose()
}
$output=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\assets\app.ico'))
$writer=[IO.BinaryWriter]::new([IO.File]::Create($output))
try{
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count);$offset=6+16*$sizes.Count
 for($i=0;$i -lt $sizes.Count;$i++){$dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]};$writer.Write([byte]$dimension);$writer.Write([byte]$dimension);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset);$offset+=$frames[$i].Length}
 foreach($frame in $frames){$writer.Write([byte[]]$frame)}
}finally{$writer.Dispose()}
