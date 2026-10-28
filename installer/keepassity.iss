#define AppName "keepassity"
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\dist\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out\dist"
#endif

[Setup]
AppId={{A7E3B1F2-9D44-4C7A-A1B2-6F5E8D9C0123}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=simplenty
AppPublisherURL=https://github.com/simplenty/keepassity
AppSupportURL=https://github.com/simplenty/keepassity/issues
AppUpdatesURL=https://github.com/simplenty/keepassity/releases
DefaultDirName={localappdata}\Programs\keepassity
DefaultGroupName=keepassity
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=keepassity-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
UninstallDisplayIcon={app}\Keepassity.Mock.exe

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\keepassity"; Filename: "{app}\Keepassity.Mock.exe"; Parameters: "--light"
Name: "{autodesktop}\keepassity"; Filename: "{app}\Keepassity.Mock.exe"; Parameters: "--light"

[Run]
Filename: "{app}\Keepassity.Mock.exe"; Parameters: "--light"; Description: "Launch keepassity"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
