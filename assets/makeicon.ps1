# Genera assets/icon.ico (varias resoluciones) con System.Drawing. Icono: cuadrado redondeado
# oscuro con barras horizontales de colores, guino al propio panel.
Add-Type -AssemblyName System.Drawing
$out = "C:\Users\WRC\source\repos\SidebarMonitor\assets\icon.ico"
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$bg     = [System.Drawing.Color]::FromArgb(255, 26, 26, 25)
$panel  = [System.Drawing.Color]::FromArgb(255, 34, 34, 32)
$bars = @(
  [System.Drawing.Color]::FromArgb(255, 57, 135, 229),   # azul
  [System.Drawing.Color]::FromArgb(255, 25, 158, 112),   # aqua
  [System.Drawing.Color]::FromArgb(255, 217, 89, 38),    # naranja
  [System.Drawing.Color]::FromArgb(255, 144, 133, 233)   # violeta
)

function New-Frame([int]$s) {
  $bmp = New-Object System.Drawing.Bitmap($s, $s, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)

  $pad = [math]::Max(1, [int]($s * 0.06))
  $r   = [int]($s * 0.18)
  $rect = New-Object System.Drawing.Rectangle($pad, $pad, ($s - 2*$pad), ($s - 2*$pad))

  # cuadrado redondeado de fondo
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()
  $br = New-Object System.Drawing.SolidBrush($panel)
  $g.FillPath($br, $path)

  # barras horizontales de colores (longitudes variadas, como usos distintos)
  $inner = [int]($s * 0.16)
  $bx = $rect.X + $inner
  $bw = $rect.Width - 2*$inner
  $n = $bars.Count
  $gap = [int]($rect.Height * 0.07)
  $bh = [int](($rect.Height - 2*$inner - ($n-1)*$gap) / $n)
  if ($bh -lt 1) { $bh = 1 }
  $by = $rect.Y + $inner
  $fracs = @(0.95, 0.55, 0.8, 0.4)
  for ($i = 0; $i -lt $n; $i++) {
    $w = [int]($bw * $fracs[$i])
    $rad = [math]::Max(1, [int]($bh * 0.4))
    $brc = New-Object System.Drawing.SolidBrush($bars[$i])
    if ($s -ge 24) {
      $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
      $dd = $rad * 2
      $bp.AddArc($bx, $by, $dd, $dd, 180, 90)
      $bp.AddArc($bx + $w - $dd, $by, $dd, $dd, 270, 90)
      $bp.AddArc($bx + $w - $dd, $by + $bh - $dd, $dd, $dd, 0, 90)
      $bp.AddArc($bx, $by + $bh - $dd, $dd, $dd, 90, 90)
      $bp.CloseFigure()
      $g.FillPath($brc, $bp)
    } else {
      $g.FillRectangle($brc, $bx, $by, $w, $bh)
    }
    $by += $bh + $gap
  }
  $g.Dispose()
  return $bmp
}

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngs = @()
foreach ($sz in $sizes) {
  $bmp = New-Frame $sz
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,($ms.ToArray())
  $bmp.Dispose()
}

# Ensamblar el contenedor ICO (cada frame es un PNG incrustado; Vista+ lo soporta).
$fs = New-Object System.IO.MemoryStream
$bw2 = New-Object System.IO.BinaryWriter($fs)
$bw2.Write([UInt16]0)      # reservado
$bw2.Write([UInt16]1)      # tipo: icono
$bw2.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $sz = $sizes[$i]; $data = $pngs[$i]
  $bw2.Write([Byte]($(if ($sz -ge 256) {0} else {$sz})))   # ancho (0 = 256)
  $bw2.Write([Byte]($(if ($sz -ge 256) {0} else {$sz})))   # alto
  $bw2.Write([Byte]0)      # paleta
  $bw2.Write([Byte]0)      # reservado
  $bw2.Write([UInt16]1)    # planos
  $bw2.Write([UInt16]32)   # bpp
  $bw2.Write([UInt32]$data.Length)
  $bw2.Write([UInt32]$offset)
  $offset += $data.Length
}
foreach ($data in $pngs) { $bw2.Write($data) }
$bw2.Flush()
[System.IO.File]::WriteAllBytes($out, $fs.ToArray())
$bw2.Dispose(); $fs.Dispose()
"icono: $out ($((Get-Item $out).Length) bytes, $($sizes.Count) resoluciones)"
