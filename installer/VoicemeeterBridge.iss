; Inno Setup script for VoicemeeterBridge.
;
; Build with build/build-installer.sh, which publishes the app to ..\publish and
; passes the version in:   ISCC.exe /DAppVersion=0.1.0 VoicemeeterBridge.iss
; Without /DAppVersion the version is read from the published executable.
;
; The app runs in the user session (it needs the Voicemeeter window of the same
; session), so this is a per-user install with no elevation: files go under
; %LOCALAPPDATA%\Programs and the optional autostart entry goes in HKCU\...\Run.

#define AppName "VoicemeeterBridge"
#define AppPublisher "Protovision"
#define AppExeName "VoicemeeterBridge.exe"
#define PublishDir "..\publish"

#ifndef AppVersion
  #define AppVersion GetStringFileInfo(PublishDir + "\" + AppExeName, "ProductVersion")
#endif

[Setup]
AppId={{7A3C2F0E-5D6B-4C1A-9E2F-3B8D4C21A7F0}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
; Same mutex the app holds while running: Setup asks the user to close it first.
AppMutex=Local\VoicemeeterBridge.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "autostart"; Description: "Start {#AppName} when I sign in to Windows"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} logs"; Filename: "{localappdata}\{#AppName}\logs"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Must match StartupRegistration.Command in the app: the quoted exe path, no arguments.
; Leaving the task unticked keeps whatever the user set from the tray menu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Start {#AppName} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "taskkill.exe"; Parameters: "/F /IM {#AppExeName}"; Flags: runhidden; RunOnceId: "StopBridge"

[Code]
// Settings and logs under %LOCALAPPDATA%\VoicemeeterBridge are kept on uninstall
// unless the user says otherwise, so the token survives a reinstall.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: string;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    // The tray menu may have added this entry after install; remove it either way.
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    DataDir := ExpandConstant('{localappdata}\{#AppName}');
    if DirExists(DataDir) then
    begin
      if MsgBox('Also delete the settings (including the auth token) and logs in' + #13#10 +
                DataDir + '?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
