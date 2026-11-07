; shft. installer. Build with scripts/build-release.ps1, which passes the version and the publish folder.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
; Never change AppId: it is what makes a new version an update and not a second install.
AppId={{6F1C2A54-3C0E-4E8B-9B57-2E7A5D1C9F31}
AppName=shft.
AppVersion={#AppVersion}
AppVerName=shft. {#AppVersion}
AppPublisher=rodolfoxm
AppPublisherURL=https://github.com/rodolfoxm/shft
AppSupportURL=https://github.com/rodolfoxm/shft/issues
AppUpdatesURL=https://github.com/rodolfoxm/shft/releases
DefaultDirName={localappdata}\Programs\shft
DisableDirPage=auto
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=shft.-Setup-{#AppVersion}
SetupIconFile=..\Assets\shft.ico
UninstallDisplayIcon={app}\shft.exe
UninstallDisplayName=shft.
WizardStyle=modern
WizardImageFile=wizard.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2/max
SolidCompression=yes
; [Code] closes the app, whichever copy is running.
CloseApplications=no
VersionInfoCompany=rodolfoxm
VersionInfoDescription=shft. installer
VersionInfoProductName=shft.
VersionInfoVersion={#AppVersion}

[Languages]
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
brazilianportuguese.RemoveData=Remover também as preferências do shft.?%n%nDock, barra e favoritos serão apagados. Escolha Não para manter.
english.RemoveData=Also remove your shft. preferences?%n%nDock, bar and favourites will be deleted. Choose No to keep them.
brazilianportuguese.StillRunning=Não foi possível fechar o shft. Encerre-o e tente de novo.
english.StillRunning=shft. could not be closed. Quit it and try again.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
; Old build output must not mix with the new one. %APPDATA%\shft is never touched here.
Type: filesandordirs; Name: "{app}\*"

[Icons]
Name: "{autoprograms}\shft."; Filename: "{app}\shft.exe"
Name: "{autodesktop}\shft."; Filename: "{app}\shft.exe"; Tasks: desktopicon

[Registry]
; The app writes this value when "start with Windows" is on; this only removes it on uninstall.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "shft"; Flags: dontcreatekey uninsdeletevalue

[UninstallRun]
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""shft"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Run]
Filename: "{app}\shft.exe"; Description: "{cm:LaunchProgram,shft.}"; Flags: nowait postinstall skipifsilent
Filename: "{app}\shft.exe"; Flags: nowait; Check: RestartAfterSilentUpdate

[Code]
const
  EVENT_MODIFY_STATE = 2;
  QuitEvent = 'shft.Quit';
  AppMutex = 'shft.SingleInstance';

var
  WasRunning: Boolean;

function OpenEvent(Access: Cardinal; Inherit: Boolean; Name: string): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ Same signal as "shft.exe --quit": the app restores the taskbar and the reserved screen space itself. }
function QuitRunningApp: Boolean;
var
  Handle: THandle;
  I: Integer;
begin
  Result := True;
  if not CheckForMutexes(AppMutex) then Exit;
  WasRunning := True;
  Handle := OpenEvent(EVENT_MODIFY_STATE, False, QuitEvent);
  if Handle <> 0 then
  begin
    SetEvent(Handle);
    CloseHandle(Handle);
  end;
  for I := 1 to 50 do
  begin
    if not CheckForMutexes(AppMutex) then
    begin
      Sleep(2500); { the taskbar watchdog outlives the app by a moment and keeps the files in use }
      Exit;
    end;
    Sleep(200);
  end;
  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not QuitRunningApp then Result := CustomMessage('StillRunning');
end;

function RestartAfterSilentUpdate: Boolean;
begin
  Result := WasRunning and WizardSilent;
end;

function InitializeUninstall: Boolean;
begin
  Result := QuitRunningApp;
  if not Result then MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{userappdata}\shft'), True, True, True);
end;
