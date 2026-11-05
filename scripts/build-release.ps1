# Builds the Windows release: self-contained app, installer, portable zip and checksums, in artifacts/.
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$version = ([xml](Get-Content shft.csproj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in shft.csproj' }

$iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

if (-not $SkipTests) {
    dotnet test tests/shft.Tests -c Release --nologo
    if ($LASTEXITCODE) { throw 'Tests failed' }
}

$out = Join-Path $root 'artifacts'
$publish = Join-Path $out 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish shft.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o $publish --nologo
if ($LASTEXITCODE) { throw 'Publish failed' }

& $iscc "/DAppVersion=$version" "/DPublishDir=$publish" "/DOutputDir=$out" installer\shft.iss
if ($LASTEXITCODE) { throw 'Installer build failed' }

$zip = Join-Path $out "shft.-$version-portable-win-x64.zip"
Compress-Archive -Path "$publish\*" -DestinationPath $zip -CompressionLevel Optimal

Get-ChildItem $out -File | Where-Object Extension -in '.exe', '.zip' | ForEach-Object {
    '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding ASCII

Get-ChildItem $out -File | ForEach-Object { '{0,8:0.0} MB  {1}' -f ($_.Length / 1MB), $_.Name }
