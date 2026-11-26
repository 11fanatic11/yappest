# Generates the Stream Deck plugin icons (key images, action list icons, plugin icon) into ../com.yappest.sdPlugin/icons.
# Run from anywhere:  powershell -File make-icons.ps1
Add-Type -AssemblyName System.Drawing

$out = Join-Path $PSScriptRoot '..\com.yappest.sdPlugin\icons'
New-Item -ItemType Directory -Force $out | Out-Null
$logo = Join-Path $PSScriptRoot '..\..\yappestlogo.png'

$font = 'Segoe Fluent Icons'
if (-not (New-Object System.Drawing.Text.InstalledFontCollection).Families.Name.Contains($font)) { $font = 'Segoe MDL2 Assets' }

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

function Resize($src, $size) {
  $b = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'HighQuality'; $g.PixelOffsetMode = 'HighQuality'
  $g.DrawImage($src, 0, 0, $size, $size); $g.Dispose(); return $b
}

# Key image: dark tile + glyph. $ring draws a thin accent border (active state).
function Key-Icon($name, $glyph, $fg, $ring) {
  $s = 144
  $b = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($b)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $g.Clear([System.Drawing.Color]::FromArgb(255, 18, 18, 21))
  if ($ring) {
    $pen = New-Object System.Drawing.Pen ([System.Drawing.ColorTranslator]::FromHtml($ring)), 8
    $g.DrawRectangle($pen, 4, 4, $s - 8, $s - 8)
  }
  $f = New-Object System.Drawing.Font $font, 64, ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
  $brush = New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($fg))
  $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
  $g.DrawString([string][char][Convert]::ToInt32($glyph, 16), $f, $brush, (New-Object System.Drawing.RectangleF 0, 0, $s, $s), $sf)
  $g.Dispose()
  Save-Png (Resize $b 72) (Join-Path $out "$name.png")
  Save-Png $b (Join-Path $out "$name@2x.png")
}

# Action list icon: white glyph on transparent, 20px / 40px
function List-Icon($name, $glyph) {
  foreach ($pair in @(@(20, ''), @(40, '@2x'))) {
    $s = $pair[0]
    $b = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $f = New-Object System.Drawing.Font $font, ($s * 0.8), ([System.Drawing.FontStyle]::Regular), ([System.Drawing.GraphicsUnit]::Pixel)
    $sf = New-Object System.Drawing.StringFormat; $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString([string][char][Convert]::ToInt32($glyph, 16), $f, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF 0, 0, $s, $s), $sf)
    $g.Dispose()
    Save-Png $b (Join-Path $out ("$name$($pair[1]).png"))
  }
}

$Y = '#FCC603'; $W = '#F1F1F3'; $G = '#6B6B75'; $R = '#FF5C54'

# key images
Key-Icon 'sound'        'E767' $W $null          # real icon replaced per key by the sound's own icon
Key-Icon 'clip'         'E8C6' $Y $null
Key-Icon 'mic-live'     'E720' $W $null
Key-Icon 'mic-muted'    'F781' $R $R
Key-Icon 'stop'         'E71A' $W $null
Key-Icon 'vc-off'       'E8D6' $G $null
Key-Icon 'vc-on'        'E8D6' $Y $Y
Key-Icon 'effect'       'E9E9' $G $null
Key-Icon 'effect-active' 'E9E9' $Y $Y
Key-Icon 'hear-off'     'E7F6' $G $null
Key-Icon 'hear-on'      'E7F6' $Y $Y

# list icons
List-Icon 'sound-list'  'E767'
List-Icon 'clip-list'   'E8C6'
List-Icon 'mute-list'   'E720'
List-Icon 'stop-list'   'E71A'
List-Icon 'vc-list'     'E8D6'
List-Icon 'effect-list' 'E9E9'
List-Icon 'hear-list'   'E7F6'

# plugin + category icons come from the Yappest logo
$src = [System.Drawing.Image]::FromFile((Resolve-Path $logo))
Save-Png (Resize $src 256) (Join-Path $out 'plugin.png')
Save-Png (Resize $src 512) (Join-Path $out 'plugin@2x.png')
Save-Png (Resize $src 28)  (Join-Path $out 'category.png')
Save-Png (Resize $src 56)  (Join-Path $out 'category@2x.png')
$src.Dispose()
Write-Host "icons written to $out"
