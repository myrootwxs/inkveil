param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\assets\app.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$output = [System.IO.Path]::GetFullPath($OutputPath)
$directory = [System.IO.Path]::GetDirectoryName($output)
if (-not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory | Out-Null
}

$bitmap = New-Object System.Drawing.Bitmap 256, 256
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 25, 22, 19))
$amber = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 222, 167, 91)), 13
$amber.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
$amber.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
$paper = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 248, 238, 219))

$graphics.FillEllipse($background, 8, 8, 240, 240)
$graphics.DrawArc($amber, 61, 54, 135, 150, 205, 255)
$graphics.DrawLine($amber, 126, 72, 126, 181)
$graphics.DrawLine($amber, 126, 181, 188, 204)

$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddBezier(69, 112, 85, 102, 104, 103, 118, 112)
$path.AddBezier(118, 112, 132, 101, 153, 101, 171, 110)
$path.AddLine(171, 110, 171, 188)
$path.AddBezier(171, 188, 152, 179, 132, 181, 118, 191)
$path.AddBezier(118, 191, 102, 181, 85, 181, 69, 189)
$path.CloseFigure()
$graphics.FillPath($paper, $path)

$graphics.DrawLine($amber, 118, 116, 118, 184)

$stream = New-Object System.IO.MemoryStream
$bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $stream.ToArray()

$file = [System.IO.File]::Create($output)
$writer = New-Object System.IO.BinaryWriter $file
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Flush()
$writer.Close()

$stream.Dispose()
$path.Dispose()
$paper.Dispose()
$amber.Dispose()
$background.Dispose()
$graphics.Dispose()
$bitmap.Dispose()

Write-Host "图标已生成：$output"
