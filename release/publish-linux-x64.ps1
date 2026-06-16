$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root "artifacts\linux-x64-self-contained"

dotnet publish (Join-Path $root "src\FrpQuickStart.Server\FrpQuickStart.Server.csproj") `
  -c Release `
  -r linux-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o (Join-Path $outRoot "server")
if ($LASTEXITCODE -ne 0) {
  exit $LASTEXITCODE
}

Get-ChildItem -Path (Join-Path $outRoot "server") -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "Linux server published to $outRoot\server"
Write-Host "Put frps in the same folder before deploying it to Ubuntu."
Write-Host "The server binary is self-contained; .NET Runtime is not required on the target Ubuntu machine."
