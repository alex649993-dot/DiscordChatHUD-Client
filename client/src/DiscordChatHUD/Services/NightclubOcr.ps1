$startup = [System.Diagnostics.Stopwatch]::StartNew()
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null = [Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($operation, $type) {
 $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation))
 $task.GetAwaiter().GetResult()
}
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]
  $null = [Windows.Globalization.Language,Windows.Globalization,ContentType=WindowsRuntime]
  $korean = New-Object Windows.Globalization.Language('ko-KR')
  $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($korean)
  if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
  if ($null -eq $engine) { throw 'Windows OCR language pack is unavailable.' }
$startup.Stop()
[pscustomobject]@{ready=$true;startupMs=$startup.Elapsed.TotalMilliseconds} | ConvertTo-Json -Compress | Write-Output
# Sequential requests share only their timing state; every input line resets it.
function Set-ReadPhase([string]$phase) {
 $script:readClock.Stop()
 $script:readTiming[$script:readPhase] = $script:readClock.Elapsed.TotalMilliseconds
 $script:readPhase = $phase
 $script:readClock.Restart()
}
function Get-ReadTiming {
 if ($script:readClock.IsRunning) {
  $script:readClock.Stop()
  $script:readTiming[$script:readPhase] = $script:readClock.Elapsed.TotalMilliseconds
 }
 [pscustomobject]$script:readTiming
}
# One process per F7 operation; reused only for its confirmation/retries.
function Read-Frame([string]$encoded) {
$stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
$writer = New-Object Windows.Storage.Streams.DataWriter($stream)
$writer.WriteBytes([Convert]::FromBase64String($encoded))
$null = Await ($writer.StoreAsync()) ([uint32])
$null = $writer.DetachStream()
$writer.Dispose()
$stream.Seek(0)
try {
 $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
 $transform = New-Object Windows.Graphics.Imaging.BitmapTransform
 $limit = [Math]::Min(2600.0, [Windows.Media.Ocr.OcrEngine]::MaxImageDimension)
 $scale = [Math]::Min(1.0, $limit / [Math]::Max($decoder.PixelWidth,$decoder.PixelHeight))
 $transform.ScaledWidth = [uint32][Math]::Round($decoder.PixelWidth * $scale)
 $transform.ScaledHeight = [uint32][Math]::Round($decoder.PixelHeight * $scale)
 $bitmap = Await ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,[Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied,$transform,[Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation,[Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
 try {
  Set-ReadPhase 'recognizeMs'
  $result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
  Set-ReadPhase 'parseMs'
  # Match complete Korean product labels and a quantity to their right on the
  # same row. Require the seven-card 4+3 grid above Special Orders.
  $products = @(
   [pscustomobject]@{key='cargo';label='패키지와화물';name='패키지와 화물';column=0;row=0},
   [pscustomobject]@{key='sporting';label='사냥용품';name='사냥 용품';column=1;row=0},
   [pscustomobject]@{key='imports';label='남미산수입품';name='남미산 수입품';column=0;row=1},
   [pscustomobject]@{key='pharma';label='연구용약품';name='연구용 약품';column=1;row=1},
   [pscustomobject]@{key='organic';label='유기농작물';name='유기농 작물';column=0;row=2},
   [pscustomobject]@{key='printing';label='인쇄복사';name='인쇄 & 복사';column=1;row=2},
   [pscustomobject]@{key='cash';label='현금창출';alias='현금장출';name='현금 창출';column=0;row=3}
  )
  $labelCount = @($result.Lines | Where-Object { ($_.Text -replace '[^가-힣]','') -in $products.label }).Count
  $nightclubText = ($result.Text -replace '[^가-힣]','') -match '나이트클럽'
  if ($labelCount -eq 0 -and -not $nightclubText) {
   $response = [pscustomobject]@{notNightclub=$true;screenText=$result.Text}
   $response | Add-Member -NotePropertyName timing -NotePropertyValue (Get-ReadTiming)
   $response | ConvertTo-Json -Depth 6 -Compress | Write-Output
   return
  }
  # Other businesses need text only. Materialize word geometry only when the
  # same label/name checks identify a possible nightclub inventory screen.
  $lines = @($result.Lines | ForEach-Object { [pscustomobject]@{ text=$_.Text; words=@($_.Words | ForEach-Object { [pscustomobject]@{ text=$_.Text; x=$_.BoundingRect.X; y=$_.BoundingRect.Y; width=$_.BoundingRect.Width; height=$_.BoundingRect.Height } }) } })
  $boundary = @($lines | Where-Object { ($_.text -replace '[^가-힣]','') -match '특별주문$' })
  # The seven labels, quantities and 4+3 grid identify warehouse inventory.
  $bottom = $bitmap.PixelHeight
  if ($boundary.Count -gt 0) { $bottom = ($boundary | ForEach-Object {$_.words} | Measure-Object y -Minimum).Minimum }
  $words = @($lines | ForEach-Object {$_.words})
  # OCR may tokenize 25/50 as 25, /, 50 at a different display scale.
  # Join only adjacent words on the same baseline; never span product columns.
  $quantities = @()
  foreach ($first in $words) {
   if ($first.text -notmatch '^\d') { continue }
   $parts = @($words | Where-Object {
    $_.x -ge $first.x -and $_.x -le $first.x+$first.height*6 -and
    [Math]::Abs(($_.y+$_.height/2)-($first.y+$first.height/2)) -lt $first.height*.45
   } | Sort-Object x)
   $joined = ''; $edge=$first.x
   foreach ($part in $parts) {
    if ($part.x-$edge -gt $first.height*.8) { break }
    $joined += ($part.text -replace '\s','' -replace '[／⁄]','/')
    $edge=$part.x+$part.width
    if ($joined -match '^\d{1,3}/\d{1,3}$') {
     $quantities += [pscustomobject]@{text=$joined;x=$first.x;y=$first.y;width=$edge-$first.x;height=$first.height}
     break
    }
    if ($joined -notmatch '^\d{1,3}/?$') { break }
   }
  }
  # Windows OCR can read the clear cash label as 현금 장출. Accept only this
  # observed label alias; all seven quantities, capacities and grid checks remain.
  $rows = @()
  foreach ($product in $products) {
   $labelLines = @($lines | Where-Object { ($_.text -replace '[^가-힣]','') -in @($product.label,$product.alias) -and (($_.words | Measure-Object y -Minimum).Minimum -lt $bottom) })
   if ($labelLines.Count -eq 0) { throw ('Missing product label: ' + $product.key) }
   # Names repeat inside special orders. Their topmost occurrences must form
   # the complete warehouse grid validated below.
   $labelWords = ($labelLines | Sort-Object {($_.words | Measure-Object y -Minimum).Minimum} | Select-Object -First 1).words
   $left = ($labelWords | Measure-Object x -Minimum).Minimum
   $right = ($labelWords | ForEach-Object {$_.x+$_.width} | Measure-Object -Maximum).Maximum
   $center = ($labelWords | ForEach-Object {$_.y+$_.height/2} | Measure-Object -Average).Average
   $h = ($labelWords | Measure-Object height -Maximum).Maximum
   $candidates = @($quantities | Where-Object {
    $_.text -match '^\d{1,3}/\d{1,3}$' -and $_.x -gt $right -and $_.x-$right -lt $bitmap.PixelWidth*.19 -and [Math]::Abs(($_.y+$_.height/2)-$center) -lt $h*.65
   })
   if ($candidates.Count -ne 1) { throw ('Ambiguous/missing quantity: ' + $product.name) }
   $parts = $candidates[0].text.Split('/')
   $stock=[int]$parts[0];$capacity=[int]$parts[1]
   if ($capacity -le 0 -or $stock -gt $capacity) { throw 'Invalid quantity; no result written.' }
   $rows += [pscustomobject]@{key=$product.key;name=$product.name;stock=$stock;capacity=$capacity;x=$left;y=$center;column=$product.column;row=$product.row}
  }
  $leftRows = @($rows | Where-Object {$_.column -eq 0} | Sort-Object row)
  $rightRows = @($rows | Where-Object {$_.column -eq 1} | Sort-Object row)
  $gap=$leftRows[1].y-$leftRows[0].y
  if ($rightRows[0].x-$leftRows[0].x -lt $bitmap.PixelWidth*.12) {throw 'Inventory columns overlap.'}
  if ($gap -lt $bitmap.PixelHeight*.05 -or $gap -gt $bitmap.PixelHeight*.18) {throw 'Unexpected inventory grid spacing.'}
  foreach ($row in $rows) {
   $anchor=if($row.column -eq 0){$leftRows[0]}else{$rightRows[0]}
   if ([Math]::Abs($row.x-$anchor.x) -gt $bitmap.PixelWidth*.015 -or [Math]::Abs($row.y-($leftRows[0].y+$row.row*$gap)) -gt $bitmap.PixelHeight*.015) {throw 'Inventory grid did not match; no result written.'}
  }
  $response = [pscustomobject]@{experimental=$true;source='local Windows OCR';items=@($rows | ForEach-Object {[pscustomobject]@{key=$_.key;name=$_.name;stock=$_.stock;capacity=$_.capacity}})}
  $response | Add-Member -NotePropertyName timing -NotePropertyValue (Get-ReadTiming)
  $response | ConvertTo-Json -Depth 6 -Compress | Write-Output
 } finally { if ($bitmap) {$bitmap.Dispose()} }
} finally {$stream.Dispose()}

}
while ($null -ne ($encoded = [Console]::In.ReadLine())) {
 $script:readTiming = [ordered]@{decodeMs=0.0;recognizeMs=0.0;parseMs=0.0}
 $script:readPhase = 'decodeMs'
 $script:readClock = [System.Diagnostics.Stopwatch]::StartNew()
 try { Read-Frame $encoded }
 catch { [pscustomobject]@{error=$_.Exception.Message;timing=(Get-ReadTiming)} | ConvertTo-Json -Depth 6 -Compress | Write-Output }
}
