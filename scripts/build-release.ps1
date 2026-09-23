param([string]$Version = '1.1.0')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\MiBrightness\MiBrightness.csproj'
$artifacts = Join-Path $root 'artifacts'
$publish = Join-Path $artifacts 'publish'
$stage = Join-Path $artifacts 'stage'

Remove-Item $artifacts -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish, $stage | Out-Null

dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Copy-Item (Join-Path $publish 'MiBrightness.exe') $stage
Copy-Item (Join-Path $root 'installer\Install.ps1') $stage
Copy-Item (Join-Path $root 'installer\Uninstall.ps1') $stage
Copy-Item (Join-Path $root 'installer\config.default.json') $stage
Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md')
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE')

$zip = Join-Path $artifacts 'MiBrightnessPortable-win-x64.zip'
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$candidates = @(
  (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
  'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
  'C:\Program Files\Inno Setup 6\ISCC.exe'
)
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) was not found.' }

$iss = Join-Path $root 'installer\MiBrightness.iss'
& $iscc "/DMySourceDir=$root" "/DMyPublishDir=$publish" "/DMyOutputDir=$artifacts" "/DMyVersion=$Version" $iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup build failed.' }

$setup = Join-Path $artifacts 'MiBrightnessSetup.exe'
if (-not (Test-Path $setup)) { throw 'Setup executable was not created.' }

$hashFile = Join-Path $artifacts 'SHA256SUMS.txt'
$lines = foreach ($file in @($setup, $zip)) {
  $h = Get-FileHash $file -Algorithm SHA256
  '{0}  {1}' -f $h.Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
$lines | Set-Content -Path $hashFile -Encoding ASCII

Get-Item $setup, $zip, $hashFile | Select-Object Name, Length, LastWriteTime | Format-Table -AutoSize
