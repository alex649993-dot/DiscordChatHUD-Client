$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'client/tests/PublicSource/PublicSource.csproj'
$keys=@('HUD_SESSION_PRIMARY_ID','HUD_SESSION_SECONDARY_ID','HUD_SESSION_PRIMARY_NAME','HUD_SESSION_SECONDARY_NAME','HUD_TRACE_USER_ID','HUD_PUBLIC_TEST_CONFIGURED')
$saved=@{}
foreach($key in $keys){$saved[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$null,'Process')}
try{
 & dotnet build $project -c Release
 if($LASTEXITCODE -ne 0){throw 'Test build failed'}
 & dotnet run --project $project -c Release --no-build
 if($LASTEXITCODE -ne 0){throw 'Default state tests failed'}
 $env:HUD_SESSION_PRIMARY_ID='900000000000000090'
 $env:HUD_SESSION_SECONDARY_ID='900000000000000091'
 $env:HUD_SESSION_PRIMARY_NAME='PublicTestHost'
 $env:HUD_PUBLIC_TEST_CONFIGURED='1'
 & dotnet run --project $project -c Release --no-build
 if($LASTEXITCODE -ne 0){throw 'Configured state tests failed'}
}finally{foreach($key in $keys){[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}}
