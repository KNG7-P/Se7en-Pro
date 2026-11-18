; -- Se7en Pro 32-bit (x86) Inno Setup Script --

#define MyAppName "Se7en Pro"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.7"
#endif
#define MyAppPublisher "Se7en Pro"
#define MyAppURL "https://github.com/KNG7-P/Se7en-Pro"
#define MyAppExeName "Se7enPro.exe"

#ifndef SourcePath
  #define SourcePath "..\dist\staging-x86"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef MyIcon
  #define MyIcon "..\Se7enPro\Assets\app.ico"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "Se7enPro_v" + MyAppVersion + "_Setup_x86"
#endif

[Setup]
AppId={{A79C32B1-40DE-42F7-82BD-5C7B79E7D93A}
AppName={#MyAppName} (32-bit)
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf32}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyIcon}
Compression=lzma2/ultra
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBaseFilename}
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
DisableDirPage=no
DisableProgramGroupPage=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourcePath}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Puts the machine back the way it was: stops leftover engines, restores the system
; proxy and removes TUN bypass routes. Without it the proxy still points at a dead
; local port after uninstall and the user loses internet.
Filename: "{app}\{#MyAppExeName}"; Parameters: "--cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "Se7enCleanup"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Se7en"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/F /IM Se7enPro.exe /IM Se7enPro.Tunnel.exe /IM Se7enPro.Aether.exe /IM Se7enPro.Tor.exe /IM Se7enPro.Shard.exe /IM Se7enPro.Lantern.exe /T', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
  Result := '';
end;
