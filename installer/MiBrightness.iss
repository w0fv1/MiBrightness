#ifndef MyVersion
  #define MyVersion "1.1.0"
#endif
#ifndef MySourceDir
  #define MySourceDir "."
#endif
#ifndef MyPublishDir
  #define MyPublishDir "."
#endif
#ifndef MyOutputDir
  #define MyOutputDir "."
#endif

[Setup]
AppId={{B1E743F5-AC28-4AB3-A87E-25A6550FAD9F}
AppName=MiBrightness
AppVersion={#MyVersion}
AppPublisher=w0fv1
DefaultDirName={localappdata}\Programs\MiBrightness
DefaultGroupName=MiBrightness
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2
SolidCompression=yes
OutputDir={#MyOutputDir}
OutputBaseFilename=MiBrightnessSetup
UninstallDisplayName=MiBrightness
CloseApplications=yes
RestartApplications=no
WizardStyle=modern

[Files]
Source: "{#MyPublishDir}\MiBrightness.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MySourceDir}\installer\config.default.json"; DestDir: "{localappdata}\MiBrightness"; DestName: "config.json"; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{autoprograms}\MiBrightness"; Filename: "{app}\MiBrightness.exe"; WorkingDir: "{app}"
Name: "{autoprograms}\MiBrightness 配置"; Filename: "{app}\MiBrightness.exe"; Parameters: "--config"; WorkingDir: "{app}"

[Run]
Filename: "powershell.exe"; Parameters: "-NoLogo -NoProfile -WindowStyle Hidden -Command ""Get-ScheduledTask -TaskName 'MiBrightness Bemfa Bridge','MiBrightness Bemfa HTTP Bridge','MiBrightness Controller','MiBrightness' -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false -ErrorAction SilentlyContinue; $a=New-ScheduledTaskAction -Execute '{app}\MiBrightness.exe'; $t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $p=New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited; Register-ScheduledTask -TaskName 'MiBrightness' -Action $a -Trigger $t -Principal $p -Force | Out-Null"""; Flags: runhidden waituntilterminated
Filename: "{app}\MiBrightness.exe"; Description: "启动 MiBrightness"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoLogo -NoProfile -WindowStyle Hidden -Command ""Unregister-ScheduledTask -TaskName 'MiBrightness' -Confirm:$false -ErrorAction SilentlyContinue"""; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: files; Name: "{localappdata}\MiBrightness\restart.flag"
Type: files; Name: "{localappdata}\MiBrightness\status.json"
