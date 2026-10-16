#requires -Version 7.4
# Render the project's simple vector mark into the PNG and multi-size Windows icon.
param([string]$AssetsDirectory=(Join-Path $PSScriptRoot '../src/MabiSync.App/Assets'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$mabiAssets=(Resolve-Path -LiteralPath $AssetsDirectory).Path
[xml]$mabiSvg=Get-Content -LiteralPath (Join-Path $mabiAssets 'MabiSync.svg') -Raw
$mabiDrawing=[Windows.Media.DrawingVisual]::new()
$mabiContext=$mabiDrawing.RenderOpen()
try {
    foreach($mabiShape in $mabiSvg.DocumentElement.ChildNodes){
        if($mabiShape.NodeType -ne [Xml.XmlNodeType]::Element){continue}
        $mabiFill=if($mabiShape.fill -and $mabiShape.fill -ne 'none'){[Windows.Media.BrushConverter]::new().ConvertFromInvariantString($mabiShape.fill)}else{$null}
        $mabiPen=if($mabiShape.stroke){
            $mabiStroke=[Windows.Media.BrushConverter]::new().ConvertFromInvariantString($mabiShape.stroke)
            $mabiOutline=[Windows.Media.Pen]::new($mabiStroke,[double]$mabiShape.'stroke-width')
            $mabiOutline.StartLineCap=$mabiOutline.EndLineCap=[Windows.Media.PenLineCap]::Round
            $mabiOutline.LineJoin=[Windows.Media.PenLineJoin]::Round
            $mabiOutline
        }else{$null}
        switch($mabiShape.LocalName){
            'rect' {$mabiContext.DrawRoundedRectangle($mabiFill,$mabiPen,[Windows.Rect]::new([double]$mabiShape.x,[double]$mabiShape.y,[double]$mabiShape.width,[double]$mabiShape.height),[double]$mabiShape.rx,[double]$mabiShape.rx)}
            'path' {$mabiContext.DrawGeometry($mabiFill,$mabiPen,[Windows.Media.Geometry]::Parse($mabiShape.d))}
            'ellipse' {$mabiContext.DrawEllipse($mabiFill,$mabiPen,[Windows.Point]::new([double]$mabiShape.cx,[double]$mabiShape.cy),[double]$mabiShape.rx,[double]$mabiShape.ry)}
            default {throw ('Unsupported icon element: '+$mabiShape.LocalName)}
        }
    }
}finally{$mabiContext.Close()}
$mabiFrames=@()
foreach($mabiSize in @(16,20,24,32,40,48,64,128,256)){
    $mabiScaled=[Windows.Media.DrawingVisual]::new();$mabiRender=$mabiScaled.RenderOpen()
    try {
        $mabiRender.PushTransform([Windows.Media.ScaleTransform]::new($mabiSize/256.0,$mabiSize/256.0))
        $mabiRender.DrawDrawing($mabiDrawing.Drawing);$mabiRender.Pop()
    }finally{$mabiRender.Close()}
    $mabiBitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($mabiSize,$mabiSize,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $mabiBitmap.Render($mabiScaled)
    $mabiEncoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $mabiEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($mabiBitmap))
    $mabiStream=[IO.MemoryStream]::new()
    try{$mabiEncoder.Save($mabiStream);$mabiFrames+=@{size=$mabiSize;data=$mabiStream.ToArray()}}finally{$mabiStream.Dispose()}
}
[IO.File]::WriteAllBytes((Join-Path $mabiAssets 'MabiSync.png'),$mabiFrames[-1].data)
$mabiIcon=[IO.File]::Create((Join-Path $mabiAssets 'MabiSync.ico'))
$mabiWriter=[IO.BinaryWriter]::new($mabiIcon)
try{
    $mabiWriter.Write([uint16]0);$mabiWriter.Write([uint16]1);$mabiWriter.Write([uint16]$mabiFrames.Count)
    $mabiOffset=6+16*$mabiFrames.Count
    foreach($mabiFrame in $mabiFrames){
        $mabiDimension=if($mabiFrame.size -eq 256){0}else{$mabiFrame.size}
        $mabiWriter.Write([byte]$mabiDimension);$mabiWriter.Write([byte]$mabiDimension)
        $mabiWriter.Write([byte]0);$mabiWriter.Write([byte]0);$mabiWriter.Write([uint16]1);$mabiWriter.Write([uint16]32)
        $mabiWriter.Write([uint32]$mabiFrame.data.Length);$mabiWriter.Write([uint32]$mabiOffset)
        $mabiOffset+=$mabiFrame.data.Length
    }
    foreach($mabiFrame in $mabiFrames){$mabiWriter.Write([byte[]]$mabiFrame.data)}
}finally{$mabiWriter.Dispose();$mabiIcon.Dispose()}
Write-Output ('Generated multi-size icon in '+$mabiAssets)
