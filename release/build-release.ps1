$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$releaseDir = Join-Path $root "release"

Get-ChildItem -Path $releaseDir -Filter "frpquick-*.tar.gz" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $releaseDir -Filter "frpquick-*.zip" -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem -Path $releaseDir -Filter "install-*.sh" -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item -LiteralPath (Join-Path $releaseDir "checksums.sha256") -Force -ErrorAction SilentlyContinue

$targets = @(
  @{ Project = "Server"; Rid = "linux-x64"; Output = "server"; Exe = "frpquick-server"; AssetDir = "server-linux-x64"; Asset = "frpquick-server-linux-x64.tar.gz"; Archive = "tar" },
  @{ Project = "Client"; Rid = "linux-x64"; Output = "client"; Exe = "frpquick-client"; AssetDir = "client-linux-x64"; Asset = "frpquick-client-linux-x64.tar.gz"; Archive = "tar" },
  @{ Project = "Server"; Rid = "win-x64"; Output = "server"; Exe = "frpquick-server.exe"; AssetDir = "server-win-x64"; Asset = "frpquick-server-win-x64.zip"; Archive = "zip" },
  @{ Project = "Client"; Rid = "win-x64"; Output = "client"; Exe = "frpquick-client.exe"; AssetDir = "client-win-x64"; Asset = "frpquick-client-win-x64.zip"; Archive = "zip" }
)

foreach ($target in $targets) {
  $projectPath = Join-Path $root "src\FrpQuickStart.$($target.Project)\FrpQuickStart.$($target.Project).csproj"
  $out = Join-Path $artifacts "$($target.Rid)-self-contained\$($target.Output)"

  dotnet publish $projectPath `
    -c Release `
    -r $target.Rid `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }

  Get-ChildItem -Path $out -Filter *.pdb -ErrorAction SilentlyContinue | Remove-Item -Force

  $assetDir = Join-Path $releaseDir $target.AssetDir
  if (Test-Path -LiteralPath $assetDir) {
    Remove-Item -LiteralPath $assetDir -Recurse -Force
  }
  New-Item -ItemType Directory -Path $assetDir | Out-Null

  Copy-Item -LiteralPath (Join-Path $out $target.Exe) -Destination (Join-Path $assetDir $target.Exe) -Force

  $assetPath = Join-Path $releaseDir $target.Asset
  if (Test-Path -LiteralPath $assetPath) {
    Remove-Item -LiteralPath $assetPath -Force
  }

  if ($target.Archive -eq "tar") {
    Push-Location $assetDir
    try {
      tar -czf $assetPath $target.Exe
    }
    finally {
      Pop-Location
    }
  }
  else {
    Compress-Archive -LiteralPath (Join-Path $assetDir $target.Exe) -DestinationPath $assetPath -Force
  }

  Write-Host "Created $assetPath"
}

$releaseAssets = @(
  "frpquick-server-linux-x64.tar.gz",
  "frpquick-client-linux-x64.tar.gz",
  "frpquick-server-win-x64.zip",
  "frpquick-client-win-x64.zip",
  "install-server.sh",
  "install-client.sh"
)

foreach ($script in @("install-server.sh", "install-client.sh")) {
  $source = Join-Path $root "scripts\$script"
  $destination = Join-Path $releaseDir $script
  $content = [IO.File]::ReadAllText($source)
  $content = $content.Replace("`r`n", "`n").Replace("`r", "`n")
  [IO.File]::WriteAllText($destination, $content, [Text.UTF8Encoding]::new($false))
}

$checksumPath = Join-Path $releaseDir "checksums.sha256"
if (Test-Path -LiteralPath $checksumPath) {
  Remove-Item -LiteralPath $checksumPath -Force
}

foreach ($asset in $releaseAssets) {
  $path = Join-Path $releaseDir $asset
  if (Test-Path -LiteralPath $path) {
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $asset" | Add-Content -LiteralPath $checksumPath -Encoding ascii
  }
}

Write-Host "Checksums written to $checksumPath"
