#define AppName "Keepassity"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\..\out\dist\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out\dist"
#endif
#ifndef AppExe
  #define AppExe "Keepassity.exe"
#endif
; Overridable so scripts/verify-installer-runtime.ps1 can build a throwaway
; installer with its own AppId/mutex and without shell integration, exercising
; install -> upgrade -> run -> uninstall against a temporary folder while the
; real installed copy stays untouched.
#ifndef AppId
  #define AppId "{{A7E3B1F2-9D44-4C7A-A1B2-6F5E8D9C0123}"
#endif
#ifndef AppMutexName
  #define AppMutexName "Local\keepassity-windows-app"
#endif

[Setup]
AppId={#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=simplenty
AppPublisherURL=https://github.com/simplenty/keepassity
AppSupportURL=https://github.com/simplenty/keepassity/issues
AppUpdatesURL=https://github.com/simplenty/keepassity/releases
; Per-user install only: the wizard never asks for elevation and never writes
; HKLM/HKCR. This is deliberate (see ADR-0029); no admin override is offered.
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
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExe}
; Shell integration and branding.
; ChangesAssociations: ask Explorer to refresh its association cache after
; install/uninstall (Inno Setup help: [Setup] ChangesAssociations).
ChangesAssociations=yes
; Setup/uninstaller icon (the same multi-size icon the app uses).
SetupIconFile=..\apps\windows\Keepassity.App\Assets\app.ico
; Refuse to install/uninstall while keepassity is running. The name must match
; the mutex the app creates at startup (App.xaml.cs, App.OnLaunched:
; new Mutex(true, @"Local\keepassity-windows-app")).
AppMutex={#AppMutexName}
; GPL-3.0-or-later license page in the wizard.
LicenseFile=..\LICENSE
VersionInfoVersion={#AppVersion}
VersionInfoProductName=Keepassity
VersionInfoDescription=Keepassity Setup
VersionInfoCompany=simplenty
VersionInfoCopyright=Copyright (C) 2026 the keepassity contributors

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

#ifndef TestMode
[Icons]
Name: "{autoprograms}\keepassity"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\keepassity"; Filename: "{app}\{#AppExe}"

[Registry]
; Per-user .kdbx registration. The installer runs with PrivilegesRequired=lowest,
; so this writes HKCU\Software\Classes only (never HKLM/HKCR).
;
; Windows 10/11 decides the default app from the user's choice, so this
; registers keepassity as a *candidate* handler only:
;   * the ProgID carries the open command, icon and display name;
;   * .kdbx\OpenWithProgids lists the ProgID so it appears in "Open with";
;   * Applications\<exe>\SupportedTypes advertises the file type to the shell.
; The .kdbx default value is deliberately NOT written: silently taking over the
; default handler is an anti-pattern (and ignored by Windows 10+ anyway).
Root: HKCU; Subkey: "Software\Classes\keepassity.kdbx"; ValueType: string; ValueName: ""; ValueData: "Keepassity database"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keepassity.kdbx\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\keepassity.kdbx\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""; Flags: uninsdeletekey
; A value under a shared key belongs to us, not the key: delete only the value.
Root: HKCU; Subkey: "Software\Classes\.kdbx\OpenWithProgids"; ValueType: string; ValueName: "keepassity.kdbx"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".kdbx"; ValueData: ""; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "Keepassity"; Flags: uninsdeletekey

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch Keepassity"; Flags: nowait postinstall skipifsilent
#endif

[InstallDelete]
; Remove the pre-1.0 binary name so an upgrade does not leave a stale executable
; (v0.1.0 shipped Keepassity.Mock.exe; v1.0.0 ships Keepassity.exe). Only exact
; files we used to install are listed: a wildcard could delete a user file that
; happens to sit in the installation folder.
Type: files; Name: "{app}\Keepassity.Mock.exe"
Type: files; Name: "{app}\Keepassity.Mock.pdb"

; Deliberately no [UninstallDelete] section. Inno Setup removes the files it
; installed and only removes directories it can leave empty; `filesandordirs
; {app}` would delete a vault, a backup or any other file a user put next to the
; executable. An uninstall must never remove data it did not create.
