#define AppName "Atlas"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "Atlas"
#define AppExeName "Atlas.exe"
; Racine contenant publish\ et Atlas.html (".." depuis le dossier installer, "." à la racine du dépôt)
#ifndef Root
  #define Root ".."
#endif

[Setup]
AppId={{B6A1E5B6-7A53-4C87-9C2D-ATLAS2026}}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Atlas
DefaultGroupName=Atlas
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir={#Root}\dist
OutputBaseFilename=Atlas-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\Atlas.exe

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Files]
Source: "{#Root}\publish\Atlas.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#Root}\Atlas.html"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autodesktop}\Atlas"; Filename: "{app}\Atlas.exe"; WorkingDir: "{app}"
Name: "{userprograms}\Atlas\Atlas"; Filename: "{app}\Atlas.exe"; WorkingDir: "{app}"

[UninstallDelete]
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\edge-profile"

[Run]
Filename: "{app}\Atlas.exe"; Description: "Lancer Atlas"; Flags: nowait postinstall skipifsilent
