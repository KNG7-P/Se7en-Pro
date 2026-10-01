; -- Se7en Pro 32-bit (x86) Inno Setup Script --

#define MyAppName "Se7en Pro"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.5"
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
