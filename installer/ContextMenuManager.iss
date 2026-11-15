; Context Menu Manager per-user installer (Inno Setup 6). Built by build\package.ps1:
;   ISCC.exe /DAppVersion=0.1.2 /DSourceDir=<publish folder> /DOutputDir=<folder> installer\ContextMenuManager.iss
; No admin: installs to %LocalAppData%\Programs\ContextMenuManager and writes only HKCU.

#ifndef AppVersion
  #error Pass /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass /DSourceDir=<dotnet publish output>
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

#define AppName "Context Menu Manager"
#define AppExe "ContextMenuManager.exe"

[Setup]
; AppId must match UpdateProtocol.AppId. Never change it, or Windows will see a different app.
AppId={{5F37C578-C736-4332-9161-A7E9C9EC6C78}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
UninstallDisplayName={#AppName}
AppPublisher=Andy Rostad
AppPublisherURL=https://github.com/arostad
AppSupportURL=https://github.com/arostad/ContextMenuManager
AppUpdatesURL=https://github.com/arostad/ContextMenuManager/releases
AppCopyright=Copyright (c) 2026 Andy Rostad. MIT License.
VersionInfoVersion={#AppVersion}.0
VersionInfoProductVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={userpf}\ContextMenuManager
; Fixed per-user folder. The uninstaller and updater assume it is the app's own folder.
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=yes
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\src\ContextMenuManager\Assets\app.ico
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
AppMutex=ContextMenuManager.Running
CloseApplications=yes
RestartApplications=no
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
OutputDir={#OutputDir}
OutputBaseFilename=ContextMenuManagerSetup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Files the in-app updater leaves in the install folder (error notes, half-finished downloads).
Type: files; Name: "{app}\update-error.txt"
Type: files; Name: "{app}\ContextMenuManager.zip.*.new"
Type: filesandordirs; Name: "{app}\.cmm-update-*"
Type: dirifempty; Name: "{app}"

[Code]
// The uninstaller removes only the program files and shortcuts. It never touches the user's data folder
// (%LocalAppData%\ContextMenuManager: backups, settings, state, log) or any menu entries the app turned off.
function InitializeUninstall(): Boolean;
var
  Msg: String;
begin
  Result := True;
  if UninstallSilent() then
    Exit;
  Msg := 'This removes Context Menu Manager from this PC.' + #13#10 + #13#10 +
    'Menu entries you turned off stay off. To bring them back, open Context Menu Manager and turn them on before you uninstall.' + #13#10 + #13#10 +
    'Your backups and settings are kept in:' + #13#10 + ExpandConstant('{localappdata}\ContextMenuManager') + #13#10 + #13#10 +
    'Continue?';
  Result := MsgBox(Msg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;