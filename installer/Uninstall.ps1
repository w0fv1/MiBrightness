param([switch]$RemoveData)
$ErrorActionPreference = 'SilentlyContinue'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\MiBrightness'
$dataDir = Join-Path $env:LOCALAPPDATA 'MiBrightness'
$programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'

Get-Process MiBrightness -ErrorAction SilentlyContinue | Stop-Process -Force
Unregister-ScheduledTask -TaskName 'MiBrightness' -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path $programs 'MiBrightness.lnk') -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $programs 'MiBrightness 配置.lnk') -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
  Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host 'MiBrightness autostart has been removed.'
if ($RemoveData) {
  Write-Host 'Configuration, encrypted key and logs were also removed.'
} else {
  Write-Host ('Configuration was kept at: ' + $dataDir)
}

$cmd = 'ping 127.0.0.1 -n 2 >nul & rmdir /s /q "' + $installDir + '"'
Start-Process cmd.exe -ArgumentList '/c', $cmd -WindowStyle Hidden
