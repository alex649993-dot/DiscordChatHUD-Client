$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetRoot = Join-Path (Split-Path $PSScriptRoot) 'client/src/DiscordChatHUD/Assets'
$items = @{
 'radar_property_bunker.png'='B'; 'radar_acid_lab.png'='A'; 'radar_hangar.png'='H'; 'radar_warehouse.png'='W';
 'BusinessIcons/cash.png'='$'; 'BusinessIcons/cocaine.png'='C'; 'BusinessIcons/documents.png'='D';
 'BusinessIcons/meth.png'='M'; 'BusinessIcons/nightclub.png'='N'; 'BusinessIcons/weed.png'='L'; 'app.png'='HUD'
}
foreach ($item in $items.GetEnumerator()) {
 $path = Join-Path $assetRoot $item.Key
 [IO.Directory]::CreateDirectory((Split-Path $path)) | Out-Null
 $bitmap = New-Object Drawing.Bitmap 64,64
 $graphics = [Drawing.Graphics]::FromImage($bitmap)
 $brush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(250,45,85))
 $font = New-Object Drawing.Font 'Segoe UI',$(if($item.Value -eq 'HUD'){16}else{30}),([Drawing.FontStyle]::Bold)
 $format = New-Object Drawing.StringFormat
 try {
  $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $graphics.Clear([Drawing.Color]::Transparent)
  $graphics.FillEllipse($brush,3,3,58,58)
  $format.Alignment=[Drawing.StringAlignment]::Center; $format.LineAlignment=[Drawing.StringAlignment]::Center
  $graphics.DrawString($item.Value,$font,[Drawing.Brushes]::White,(New-Object Drawing.RectangleF 0,0,64,64),$format)
  $bitmap.Save($path,[Drawing.Imaging.ImageFormat]::Png)
 } finally { $format.Dispose();$font.Dispose();$brush.Dispose();$graphics.Dispose();$bitmap.Dispose() }
}
$png=[IO.File]::ReadAllBytes((Join-Path $assetRoot 'app.png'))
$stream=[IO.File]::Create((Join-Path $assetRoot 'gtao_hud_icon.ico'))
$writer=New-Object IO.BinaryWriter $stream
try {
 $writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]1)
 $writer.Write([byte]64);$writer.Write([byte]64);$writer.Write([byte]0);$writer.Write([byte]0)
 $writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$png.Length);$writer.Write([uint32]22);$writer.Write($png)
} finally {$writer.Dispose()}
