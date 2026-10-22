$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = $PSScriptRoot
New-Item -ItemType Directory -Path (Join-Path $root 'assets') -Force | Out-Null
$archivePath = Join-Path $root 'intro-convergence.zip'
$output = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = New-Object IO.Compression.ZipArchive($output, [IO.Compression.ZipArchiveMode]::Create)
try {
    for ($i = 0; $i -lt 210; $i++) {
        $bitmap = New-Object Drawing.Bitmap(960,540)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $rect = New-Object Drawing.Rectangle(0,0,960,540)
        $gradient = New-Object Drawing.Drawing2D.LinearGradientBrush($rect, [Drawing.Color]::FromArgb(16,9,28), [Drawing.Color]::FromArgb(42,67,84), 30)
        $graphics.FillRectangle($gradient,$rect)
        $pen = New-Object Drawing.Pen([Drawing.Color]::FromArgb(100,115,220,225),2)
        $progress = [Math]::Min(1,$i/155.0)
        for ($ring=0; $ring -lt 9; $ring++) {
            $radius = [int](24+$ring*19+(1-$progress)*100)
            $graphics.DrawEllipse($pen, [int](740-$radius), [int](265-$radius), $radius*2, $radius*2)
        }
        $entry = $archive.CreateEntry(('frame-{0:D3}.png' -f $i))
        $stream = $entry.Open()
        try { $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png) }
        finally { $stream.Dispose(); $pen.Dispose(); $gradient.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $archive.Dispose(); $output.Dispose() }
$motion = New-Object Drawing.Bitmap(1280,720)
$g = [Drawing.Graphics]::FromImage($motion)
try { $g.Clear([Drawing.Color]::FromArgb(42,67,84)); $motion.Save((Join-Path $root 'assets/reze-motion-keyframe.png'),[Drawing.Imaging.ImageFormat]::Png) }
finally { $g.Dispose(); $motion.Dispose() }
# Encode a single PNG image as an ICO without relying on third-party artwork.
$iconBitmap = New-Object Drawing.Bitmap(64,64)
$g = [Drawing.Graphics]::FromImage($iconBitmap)
$brush = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(115,220,225))
$png = New-Object IO.MemoryStream
try {
    $g.Clear([Drawing.Color]::FromArgb(16,9,28))
    $g.FillEllipse($brush,12,12,40,40)
    $iconBitmap.Save($png,[Drawing.Imaging.ImageFormat]::Png)
    $file = [IO.File]::Create((Join-Path $root 'assets/codex-black-transparent.ico'))
    $writer = New-Object IO.BinaryWriter($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
        $writer.Write([byte]64); $writer.Write([byte]64); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
        $writer.Write($png.ToArray())
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $png.Dispose(); $brush.Dispose(); $g.Dispose(); $iconBitmap.Dispose() }
Write-Output 'Original abstract demo assets generated (210 frames).'
