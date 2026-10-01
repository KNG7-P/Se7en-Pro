; -- Se7en Pro 64-bit Inno Setup Script --

#define MyAppName "Se7en Pro"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.5"
#endif
#define MyAppPublisher "Se7en Pro"
#define MyAppURL "https://github.com/yesmaynameisO/Se7en-Pro"
#define MyAppExeName "Se7enPro.exe"

#ifndef SourcePath
  #define SourcePath "..\dist\staging-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif
#ifndef MyIcon
  #define MyIcon "..\Se7enPro\Assets\app.ico"
#endif

#ifndef OutputBaseFilename
  #define OutputBaseFilename "Se7enPro_v" + MyAppVersion + "_Setup_x64"
#endif

[Setup]
AppId={{D37B40A5-51FE-49A8-94BE-4B6A68F8C82B}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile={#MyIcon}
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Compression=lzma2/ultra64
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
