#ifndef Version
  #define Version "0.1.0"
#endif

#ifndef CommitSha
  #define CommitSha "unknown"
#endif

[Setup]
AppName=NoMercy MediaServer
AppVersion={#Version}
AppPublisher=NoMercy Entertainment
AppPublisherURL=https://nomercy.tv
AppSupportURL=https://github.com/NoMercy-Entertainment/nomercy-media-server
DefaultDirName={autopf}\NoMercy MediaServer
DefaultGroupName=NoMercy MediaServer
OutputBaseFilename=NoMercyMediaServer-{#Version}-windows-x64-setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupIconFile=..\..\assets\icons\icon.ico
UninstallDisplayIcon={app}\NoMercyMediaServer.exe
PrivilegesRequired=admin
WizardStyle=modern
MinVersion=10.0
AppComments=Build: {#CommitSha}
VersionInfoCopyright=Build {#CommitSha}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Types]
Name: "full"; Description: "Full installation (Server + App + Service + CLI)"
Name: "server"; Description: "Server only"
Name: "custom"; Description: "Custom installation"; Flags: iscustom

[Components]
Name: "server"; Description: "NoMercy MediaServer"; Types: full server custom; Flags: fixed
Name: "app"; Description: "NoMercy App (desktop media interface)"; Types: full
Name: "service"; Description: "NoMercy Launcher (system tray control)"; Types: full
Name: "cli"; Description: "NoMercy CLI (command-line tool)"; Types: full

[Dirs]
; Writable directory for external dependencies (FFmpeg, cloudflared, yt-dlp, etc.)
; The server downloads these at runtime and needs write access
Name: "{app}\binaries"; Permissions: users-modify
Name: "{app}\binaries\ffmpeg"; Permissions: users-modify
Name: "{app}\binaries\tesseract"; Permissions: users-modify
Name: "{app}\binaries\tesseract\tessdata"; Permissions: users-modify

[Files]
; Shared runtime and dependencies (always installed with server).
; This must recurse: the payload's subdirectories carry runtimes\ (the libnfs
; natives, without which NFS mounts fail with "Unable to load DLL 'nfs'"),
; Assets\, BuildHost-*\ and the satellite resource folder for every language.
; The optional executables are excluded here so they stay on their own components.
Source: "..\..\installer-payload\*"; DestDir: "{app}"; Components: server; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "NoMercyApp.exe,NoMercyLauncher.exe,nomercy.exe"

; Main executables per component
Source: "..\..\installer-payload\NoMercyApp.exe"; DestDir: "{app}"; Components: app; Flags: ignoreversion
Source: "..\..\installer-payload\NoMercyLauncher.exe"; DestDir: "{app}"; Components: service; Flags: ignoreversion
Source: "..\..\installer-payload\nomercy.exe"; DestDir: "{app}"; Components: cli; Flags: ignoreversion

; Icon
Source: "..\..\assets\icons\icon.ico"; DestDir: "{app}"; DestName: "icon.ico"; Flags: ignoreversion

[Icons]
Name: "{group}\NoMercy MediaServer"; Filename: "{app}\NoMercyMediaServer.exe"; IconFilename: "{app}\icon.ico"
Name: "{group}\NoMercy MediaServer"; Filename: "{app}\NoMercyLauncher.exe"; IconFilename: "{app}\icon.ico"; Components: service
Name: "{group}\{cm:UninstallProgram,NoMercy MediaServer}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\NoMercy MediaServer"; Filename: "{app}\NoMercyLauncher.exe"; IconFilename: "{app}\icon.ico"; Components: service; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Components: service
Name: "installservice"; Description: "Install as Windows Service (auto-start on boot)"; GroupDescription: "Service:"; Components: server
Name: "addtopath"; Description: "Add CLI to system PATH"; GroupDescription: "CLI:"; Components: cli

[Run]
Filename: "sc.exe"; Parameters: "create NoMercyMediaServer binPath= ""\""{app}\NoMercyMediaServer.exe\"" --service"" start= auto DisplayName= ""NoMercy MediaServer"""; Flags: runhidden; Tasks: installservice
Filename: "sc.exe"; Parameters: "start NoMercyMediaServer"; Flags: runhidden; Tasks: installservice
; skipifsilent is omitted when /LaunchAfterInstall=1 is passed (auto-update with auto-start enabled).
; The [Code] section handles conditional launch via PostInstall pascal code.
Filename: "{app}\NoMercyLauncher.exe"; Description: "Launch NoMercy MediaServer"; Flags: nowait postinstall skipifsilent; Components: service; Check: ShouldShowLauncherEntry

[UninstallRun]
Filename: "sc.exe"; Parameters: "stop NoMercyMediaServer"; Flags: runhidden; Tasks: installservice
Filename: "sc.exe"; Parameters: "delete NoMercyMediaServer"; Flags: runhidden; Tasks: installservice

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
function NeedsAddPath(Param: string): boolean;
var
  OrigPath: string;
begin
  if not RegQueryStringValue(HKEY_LOCAL_MACHINE,
    'SYSTEM\CurrentControlSet\Control\Session Manager\Environment',
    'Path', OrigPath)
  then begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Param + ';', ';' + OrigPath + ';') = 0;
end;

// Returns the /LaunchAfterInstall parameter value (1 = launch, 0 or absent = skip).
function LaunchAfterInstallParam: Boolean;
begin
  Result := ExpandConstant('{param:LaunchAfterInstall|0}') = '1';
end;

// Returns the /UpdateCacheCleanup parameter value (1 = delete installer, 0 or absent = skip).
function UpdateCacheCleanupParam: Boolean;
begin
  Result := ExpandConstant('{param:UpdateCacheCleanup|0}') = '1';
end;

// Used by the [Run] entry to decide whether to show the interactive "launch" checkbox.
// When /LaunchAfterInstall=1 the entry is hidden and we launch from CurStepChanged instead.
function ShouldShowLauncherEntry: Boolean;
begin
  Result := not LaunchAfterInstallParam;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  InstallerPath: string;
  ExecResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    // Delete the installer from the update cache after a successful install.
    // Only triggered when the Launcher spawned us with /UpdateCacheCleanup=1.
    if UpdateCacheCleanupParam then
    begin
      InstallerPath := ExpandConstant('{srcexe}');
      if FileExists(InstallerPath) then
        DeleteFile(InstallerPath);
      // Also delete the .sha256 sidecar
      if FileExists(InstallerPath + '.sha256') then
        DeleteFile(InstallerPath + '.sha256');
    end;

    // When /LaunchAfterInstall=1 the interactive [Run] entry is suppressed via Check:
    // ShouldShowLauncherEntry.  We launch the Launcher here unconditionally so it
    // starts even in /SILENT mode, honouring the user's auto-start preference.
    if LaunchAfterInstallParam then
    begin
      Exec(ExpandConstant('{app}\NoMercyLauncher.exe'), '', '', SW_SHOW,
           ewNoWait, ExecResultCode);
    end;
  end;
end;
