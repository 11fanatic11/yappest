# Builds a rounded-corner multi-size .ico from yappestlogo.png (the square source stays untouched).
#   powershell -File make-ico.ps1 -Out ..\src\Yappest\Assets\yappest.ico
# Sizes up to 128 are stored as classic 32-bit BMP entries (works everywhere), 256 as PNG (Windows standard).
param([string]$Out = (Join-Path $PSScriptRoot '..\src\Yappest\Assets\yappest.ico'),
      [string]$Src = (Join-Path $PSScriptRoot '..\yappestlogo.png'),
      [double]$Radius = 0.225)   # corner radius as a fraction of the icon size
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 96, 128, 256
$srcPath = (Resolve-Path $Src).Path
$img = [System.Drawing.Image]::FromFile($srcPath)
$entries = @()

foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [Math]::Max(2, $s * $Radius); $d = $r * 2
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d, 0, $d, $d, 270, 90)
  $path.AddArc($s - $d, $s - $d, $d, $d, 0, 90); $path.AddArc(0, $s - $d, $d, $d, 90, 90); $path.CloseFigure()
  $g.SetClip($path)
  $g.DrawImage($img, 0, 0, $s, $s)
  $g.Dispose()

  $ms = New-Object System.IO.MemoryStream
  if ($s -ge 256) {
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  } else {
    $w = New-Object System.IO.BinaryWriter $ms
    $w.Write([uint32]40); $w.Write([int32]$s); $w.Write([int32]($s * 2)); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]0); $w.Write([uint32]($s * $s * 4)); $w.Write([int32]0); $w.Write([int32]0); $w.Write([uint32]0); $w.Write([uint32]0)
    for ($y = $s - 1; $y -ge 0; $y--) {                        # bottom-up BGRA
      for ($x = 0; $x -lt $s; $x++) {
        $c = $bmp.GetPixel($x, $y)
        $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
      }
    }
    $maskRow = [Math]::Ceiling($s / 32.0) * 4                   # AND mask, all opaque (alpha channel does the work)
    $zeros = New-Object 'byte[]' ($maskRow * $s)
    $w.Write($zeros, 0, $zeros.Length)
    $w.Flush()
  }
  $entries += , @($s, $ms.ToArray())
  $bmp.Dispose()
}
$img.Dispose()

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
  $s = $e[0]; $data = [byte[]]$e[1]
  $dim = [byte]($(if ($s -ge 256) { 0 } else { $s }))
  $bw.Write($dim); $bw.Write($dim); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$data.Length); $bw.Write([uint32]$offset)
  $offset += $data.Length
}
foreach ($e in $entries) { $bytes = [byte[]]$e[1]; $bw.Write($bytes, 0, $bytes.Length) }
$bw.Close(); $fs.Close()
Write-Host "wrote $Out ($($entries.Count) sizes)"
