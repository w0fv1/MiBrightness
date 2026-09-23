param()
$ErrorActionPreference = 'Stop'

$src = Split-Path -Parent $MyInvocation.MyCommand.Path
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\MiBrightness'
$dataDir = Join-Path $env:LOCALAPPDATA 'MiBrightness'
$exe = Join-Path $installDir 'MiBrightness.exe'
$config = Join-Path $dataDir 'config.json'

Write-Host 'Installing MiBrightness...' -ForegroundColor Cyan

Get-Process MiBrightness -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
  Where-Object {
    $_.CommandLine -like '*bemfa-bridge.ps1*' -or
    $_.CommandLine -like '*bemfa-http-bridge.ps1*' -or
    $_.CommandLine -like '*brightness-server.ps1*'
  } |
  ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

New-Item -ItemType Directory -Force -Path $installDir, $dataDir, (Join-Path $dataDir 'logs') | Out-Null

Copy-Item (Join-Path $src 'MiBrightness.exe') $exe -Force
Copy-Item (Join-Path $src 'Uninstall.ps1') (Join-Path $installDir 'Uninstall.ps1') -Force

if (-not (Test-Path $config)) {
  $cfg = Get-Content (Join-Path $src 'config.default.json') -Raw | ConvertFrom-Json
  $candidate = Get-NetIPConfiguration -ErrorAction SilentlyContinue |
    Where-Object {
      $_.IPv4DefaultGateway -and
      $_.InterfaceAlias -notmatch 'Meta|Clash|TUN|VPN|Loopback'
    } |
    Select-Object -First 1
  if ($candidate) { $cfg.InterfaceAlias = $candidate.InterfaceAlias }
  $cfg | ConvertTo-Json -Depth 5 | Set-Content -Path $config -Encoding UTF8
}

foreach ($name in @('MiBrightness Bemfa Bridge','MiBrightness Bemfa HTTP Bridge','MiBrightness Controller','MiBrightness')) {
  Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName 'MiBrightness' -Action $action -Trigger $trigger -Principal $principal -Force | Out-Null

try {
  $programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
  $shell = New-Object -ComObject WScript.Shell

  $cfgLink = $shell.CreateShortcut((Join-Path $programs 'MiBrightness 配置.lnk'))
  $cfgLink.TargetPath = $exe
  $cfgLink.Arguments = '--config'
  $cfgLink.WorkingDirectory = $installDir
  $cfgLink.IconLocation = $exe
  $cfgLink.Save()

  $runLink = $shell.CreateShortcut((Join-Path $programs 'MiBrightness.lnk'))
  $runLink.TargetPath = $exe
  $runLink.WorkingDirectory = $installDir
  $runLink.IconLocation = $exe
  $runLink.Save()
} catch {}

Start-Process $exe
Start-Sleep -Seconds 2

Write-Host ''
Write-Host 'MiBrightness installed.' -ForegroundColor Green
Write-Host ('Program: ' + $exe)
Write-Host ('Config : ' + $config)
Write-Host ('Logs   : ' + (Join-Path $dataDir 'logs'))
Write-Host 'If this is a fresh install, open "MiBrightness 配置" from Start and set the Bemfa private key.'
