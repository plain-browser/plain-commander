# Draws the app icon (two panels on an accent square) at every size MSIX and the exe need.
# Runs on Windows (System.Drawing). Usage: make-icons.ps1 -Out <Assets folder>
param([string]$Out = "$PSScriptRoot\..\src\PlainCommander.App\Assets")
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $Out | Out-Null

# Mode: plated (accent square, white panels), dark (taskbar on a dark bar: bright panels alone),
# light (taskbar on a light bar: accent panels alone).
function Draw([int]$w, [int]$h, [string]$Mode = 'plated') {
  $Plain = $Mode -ne 'plated'
  $bmp = New-Object System.Drawing.Bitmap $w, $h, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
  [double]$s = [Math]::Min($w, $h); [double]$ox = ($w - $s) / 2; [double]$oy = ($h - $s) / 2
  if (-not $Plain) {
    # accent square with rounded corners (Windows 11 blue)
    $r = $s * 0.22; $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rect = New-Object System.Drawing.RectangleF ($ox, $oy, $s, $s)
    $path.AddArc($rect.X, $rect.Y, $r, $r, 180, 90); $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 90)
    $path.AddArc($rect.Right - $r, $rect.Bottom - $r, $r, $r, 0, 90); $path.AddArc($rect.X, $rect.Bottom - $r, $r, $r, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 15, 108, 189))), $path)
    $panel = [System.Drawing.Color]::FromArgb(255, 255, 255, 255); $panel2 = [System.Drawing.Color]::FromArgb(160, 255, 255, 255)
  } else {
    # taskbar/unplated variants: the panels alone, no square. A dark bar needs bright panels (the accent blue vanishes there).
    if ($Mode -eq 'dark') { $panel = [System.Drawing.Color]::FromArgb(255, 76, 194, 255); $panel2 = [System.Drawing.Color]::FromArgb(170, 76, 194, 255) }
    else { $panel = [System.Drawing.Color]::FromArgb(255, 15, 108, 189); $panel2 = [System.Drawing.Color]::FromArgb(150, 15, 108, 189) }
  }
  # two panels: left solid, right translucent; gap between them
  [double]$m = $s * $(if ($Plain) { 0.05 } else { 0.15 }); [double]$gap = $s * 0.08; [double]$pw = ($s - 2 * $m - $gap) / 2; [double]$ph = $s - 2 * $m; [double]$pr = [Math]::Max(1, $s * 0.07)
  foreach ($i in 0, 1) {
    [double]$x = $ox + $m + $i * ($pw + $gap); [double]$y = $oy + $m
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $pr, $pr, 180, 90); $p.AddArc($x + $pw - $pr, $y, $pr, $pr, 270, 90)
    $p.AddArc($x + $pw - $pr, $y + $ph - $pr, $pr, $pr, 0, 90); $p.AddArc($x, $y + $ph - $pr, $pr, $pr, 90, 90); $p.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ($(if ($i -eq 0) { $panel } else { $panel2 }))), $p)
    # a "cursor" bar on the left panel
    if ($i -eq 0 -and $s -ge 32) {
      [float]$bx = $x + $pw * 0.18; [float]$by = $y + $ph * 0.30; [float]$bw = $pw * 0.64; [float]$bh = [Math]::Max(1.0, $ph * 0.09)
      $barColor = if ($Mode -eq 'dark') { [System.Drawing.Color]::FromArgb(255, 11, 60, 110) } elseif ($Plain) { [System.Drawing.Color]::White } else { [System.Drawing.Color]::FromArgb(255, 15, 108, 189) }
      $g.FillRectangle((New-Object System.Drawing.SolidBrush $barColor), $bx, $by, $bw, $bh)
    }
  }
  $g.Dispose(); return $bmp
}
function Save($bmp, $name) { $bmp.Save((Join-Path $Out $name), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose() }

Save (Draw 88 88) 'Square44x44Logo.scale-200.png'
Save (Draw 300 300) 'Square150x150Logo.scale-200.png'
Save (Draw 620 300) 'Wide310x150Logo.scale-200.png'
Save (Draw 620 620) 'LargeTile.scale-200.png'
Save (Draw 142 142) 'SmallTile.scale-200.png'
Save (Draw 100 100) 'StoreLogo.scale-200.png'
Save (Draw 1240 600) 'SplashScreen.scale-200.png'
foreach ($t in 16, 24, 32, 48, 256) {
  Save (Draw $t $t) "Square44x44Logo.targetsize-$t.png"
  Save (Draw $t $t 'dark') "Square44x44Logo.targetsize-${t}_altform-unplated.png"
  Save (Draw $t $t 'light') "Square44x44Logo.targetsize-${t}_altform-lightunplated.png"
}

# .ico for the exe: PNG-compressed entries (Vista+), sizes 16..256
$sizes = 16, 24, 32, 48, 64, 128, 256
$entries = foreach ($z in $sizes) { $ms = New-Object IO.MemoryStream; (Draw $z $z).Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); ,@($z, $ms.ToArray()) }
$ico = New-Object IO.MemoryStream; $bw = New-Object IO.BinaryWriter $ico
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($e in $entries) { $z = $e[0]; $d = $e[1]; $bw.Write([byte]$(if ($z -ge 256) { 0 } else { $z })); $bw.Write([byte]$(if ($z -ge 256) { 0 } else { $z })); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$d.Length); $bw.Write([uint32]$offset); $offset += $d.Length }
foreach ($e in $entries) { $bw.Write($e[1]) }
[IO.File]::WriteAllBytes((Join-Path $Out 'PlainCommander.ico'), $ico.ToArray())
Get-ChildItem $Out | ForEach-Object { "$($_.Name) $($_.Length)" }
