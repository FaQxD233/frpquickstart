$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root "artifacts\win-x64-self-contained"

$projects = @(
  @{ Name = "Server"; Output = "server" },
  @{ Name = "Client"; Output = "client" }
)

foreach ($project in $projects) {
  dotnet publish (Join-Path $root "src\FrpQuickStart.$($project.Name)\FrpQuickStart.$($project.Name).csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o (Join-Path $outRoot $project.Output)
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  Get-ChildItem -Path (Join-Path $outRoot $project.Output) -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force
}

Write-Host "Windows server published to $outRoot\server"
Write-Host "Windows client published to $outRoot\client"
Write-Host "Both executables are self-contained and embed the required frp component."
