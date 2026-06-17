$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root "artifacts\win-x64-self-contained"

dotnet publish (Join-Path $root "src\FrpQuickStart.Client\FrpQuickStart.Client.csproj") `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o (Join-Path $outRoot "client")
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

Get-ChildItem -Path (Join-Path $outRoot "client") -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Windows client published to $outRoot\client"
Write-Host "frpc.exe is embedded in frpquick-client.exe and will be extracted automatically on first run."
Write-Host "The client exe is self-contained; .NET Runtime is not required on the target Windows machine."
