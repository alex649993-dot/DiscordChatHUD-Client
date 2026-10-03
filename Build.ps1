param([string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=$PSScriptRoot
Push-Location $root
try {
 & "$root/tools/Generate-Assets.ps1"
 $client="$root/client/src/DiscordChatHUD/DiscordChatHUD.csproj"
 $output=Join-Path $root 'artifacts'
 [IO.Directory]::CreateDirectory($output) | Out-Null
 function Publish-Project([string]$project,[string]$destination,[string[]]$extra=@()) {
  & dotnet publish $project -c $Configuration -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:PublishReadyToRun=false' '-p:DebugType=None' '-p:DebugSymbols=false' '-p:BetaRelease=false' -o $destination @extra
  if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
 }
 Publish-Project $client "$output/watcher" @('-p:ClientRole=Watcher','-p:AssemblyTitle=DiscordChatHUD Watcher')
 $inputStream=[IO.File]::OpenRead("$output/watcher/DiscordChatHUD.exe")
 $outputStream=[IO.File]::Create("$root/client/src/DiscordChatHUD/Assets/Watcher.exe.gz")
 $gzip=New-Object IO.Compression.GZipStream $outputStream,([IO.Compression.CompressionMode]::Compress)
 try{$inputStream.CopyTo($gzip)}finally{$gzip.Dispose();$outputStream.Dispose();$inputStream.Dispose()}
 Publish-Project $client "$output/client"
 Copy-Item -LiteralPath "$output/client/DiscordChatHUD.exe" -Destination "$output/client/DiscordChatHUD_Config.exe"
 '{"serverUrl":"https://relay.example.invalid"}' | Set-Content -LiteralPath "$output/client/relay-client.json" -Encoding UTF8
 foreach($name in @('watcher','client')){
  $licenseDir=Join-Path "$output/$name" 'licenses'
  [IO.Directory]::CreateDirectory($licenseDir)|Out-Null
  Copy-Item -LiteralPath "$root/LICENSE","$root/THIRD_PARTY_NOTICES.md" -Destination $licenseDir
  Copy-Item -Path "$root/licenses/*" -Destination $licenseDir
  Copy-Item -LiteralPath "$root/client/FONT_LICENSE_OFL.txt","$root/client/LZ4_LICENSE.txt","$root/client/ASSET_LICENSE.txt" -Destination $licenseDir
 }
 Write-Host 'Build complete. Configure your own server before running. Binaries are unsigned.'
}finally{Pop-Location}
