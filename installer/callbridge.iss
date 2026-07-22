#define AppName "CallBridge"
#define AppPublisher "IT HealthTech"
#define AppURL "https://ithealthtech.com"
#define AppExeName "CallBridge.exe"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef SourceDirectory
  #define SourceDirectory "..\publish"
#endif

#ifndef OutputDirectory
  #define OutputDirectory "..\dist"
#endif

[Setup]
AppId={{97C2500A-50B2-4C30-A687-AED1244BFE11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}

OutputDir={#OutputDirectory}
OutputBaseFilename=CallBridge-Setup-{#AppVersion}

Compression=lzma2/max
SolidCompression=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog

WizardStyle=modern
SetupLogging=yes
RestartIfNeededByRun=no
CloseApplications=yes
CloseApplicationsFilter={#AppExeName}

UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
Uninstallable=yes
CreateUninstallRegKey=yes

VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} Windows Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) {#AppPublisher}

#ifexist "assets\CallBridge.ico"
SetupIconFile=assets\CallBridge.ico
#endif

#ifexist "license.txt"
LicenseFile=license.txt
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; \
    Description: "Create a desktop shortcut"; \
    GroupDescription: "Additional shortcuts:"; \
    Flags: unchecked

Name: "startup"; \
    Description: "Start CallBridge automatically when I sign in"; \
    GroupDescription: "Startup options:"; \
    Flags: unchecked

Name: "firewall"; \
    Description: "Allow CallBridge through Windows Firewall"; \
    GroupDescription: "Network options:"; \
    Flags: unchecked

[Dirs]
; Machine-wide application data.
Name: "{commonappdata}\CallBridge"; \
    Permissions: users-modify

Name: "{commonappdata}\CallBridge\logs"; \
    Permissions: users-modify

; Per-user configuration directory.
Name: "{userappdata}\CallBridge"

[Files]
Source: "{#SourceDirectory}\*"; \
    DestDir: "{app}"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Optional default configuration.
; It is installed only if the destination file does not already exist.
#ifexist "{#SourceDirectory}\config\appsettings.default.json"
Source: "{#SourceDirectory}\config\appsettings.default.json"; \
    DestDir: "{commonappdata}\CallBridge"; \
    DestName: "appsettings.json"; \
    Flags: onlyifdoesntexist uninsneveruninstall
#endif

[Icons]
Name: "{group}\CallBridge"; \
    Filename: "{app}\{#AppExeName}"; \
    WorkingDir: "{app}"

Name: "{group}\Uninstall CallBridge"; \
    Filename: "{uninstallexe}"

Name: "{autodesktop}\CallBridge"; \
    Filename: "{app}\{#AppExeName}"; \
    WorkingDir: "{app}"; \
    Tasks: desktopicon

Name: "{userstartup}\CallBridge"; \
    Filename: "{app}\{#AppExeName}"; \
    WorkingDir: "{app}"; \
    Tasks: startup

[Registry]
; Register the installed version and path for application diagnostics.
Root: HKLM; \
    Subkey: "Software\IT HealthTech\CallBridge"; \
    ValueType: string; \
    ValueName: "InstallPath"; \
    ValueData: "{app}"; \
    Flags: uninsdeletekey

Root: HKLM; \
    Subkey: "Software\IT HealthTech\CallBridge"; \
    ValueType: string; \
    ValueName: "Version"; \
    ValueData: "{#AppVersion}"

; Example custom URI handler:
; callbridge://dial?number=15551234567

Root: HKCR; \
    Subkey: "callbridge"; \
    ValueType: string; \
    ValueData: "URL:CallBridge Protocol"; \
    Flags: uninsdeletekey

Root: HKCR; \
    Subkey: "callbridge"; \
    ValueType: string; \
    ValueName: "URL Protocol"; \
    ValueData: ""

Root: HKCR; \
    Subkey: "callbridge\DefaultIcon"; \
    ValueType: string; \
    ValueData: """{app}\{#AppExeName}"",0"

Root: HKCR; \
    Subkey: "callbridge\shell\open\command"; \
    ValueType: string; \
    ValueData: """{app}\{#AppExeName}"" ""%1"""

[Run]
; Add the application to Windows Firewall when selected.
Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall add rule name=""CallBridge"" dir=in action=allow program=""{app}\{#AppExeName}"" enable=yes profile=private,domain"; \
    Flags: runhidden waituntilterminated; \
    Tasks: firewall

Filename: "{app}\{#AppExeName}"; \
    Description: "Launch CallBridge"; \
    WorkingDir: "{app}"; \
    Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; \
    Parameters: "/F /IM {#AppExeName}"; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "StopCallBridge"

Filename: "{sys}\netsh.exe"; \
    Parameters: "advfirewall firewall delete rule name=""CallBridge"" program=""{app}\{#AppExeName}"""; \
    Flags: runhidden waituntilterminated; \
    RunOnceId: "RemoveFirewallRule"

[UninstallDelete]
; Remove temporary and cache data.
; Deliberately preserve configuration and logs unless the user requests removal.
Type: filesandordirs; Name: "{localappdata}\CallBridge\cache"
Type: filesandordirs; Name: "{localappdata}\CallBridge\temp"

[Code]
const
  DotNetDesktopRuntimeRegistryKey =
    'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App';

function IsDotNetDesktopRuntimeInstalled(): Boolean;
var
  Versions: TArrayOfString;
begin
  Result :=
    RegGetValueNames(
      HKLM64,
      DotNetDesktopRuntimeRegistryKey,
      Versions
    ) and (GetArrayLength(Versions) > 0);
end;

function IsCallBridgeRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result :=
    Exec(
      ExpandConstant('{cmd}'),
      '/C tasklist /FI "IMAGENAME eq {#AppExeName}" | find /I "{#AppExeName}"',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    ) and (ResultCode = 0);
end;

function StopCallBridge(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;

  if not IsCallBridgeRunning() then
    Exit;

  if MsgBox(
       'CallBridge is currently running and must be closed before installation can continue.' +
       #13#10#13#10 +
       'Close CallBridge now?',
       mbConfirmation,
       MB_YESNO
     ) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  if not Exec(
       ExpandConstant('{sys}\taskkill.exe'),
       '/F /IM {#AppExeName}',
       '',
       SW_HIDE,
       ewWaitUntilTerminated,
       ResultCode
     ) then
  begin
    MsgBox(
      'CallBridge could not be closed automatically. Close it manually and run the installer again.',
      mbError,
      MB_OK
    );
    Result := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := StopCallBridge();

  if not Result then
    Exit;

  if not IsDotNetDesktopRuntimeInstalled() then
  begin
    if MsgBox(
         'A compatible Microsoft .NET Desktop Runtime was not detected.' +
         #13#10#13#10 +
         'The installer can continue, but CallBridge may not start unless the application was published as self-contained.' +
         #13#10#13#10 +
         'Continue installation?',
         mbConfirmation,
         MB_YESNO
       ) = IDNO then
    begin
      Result := False;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ConfigDirectory: String;
  LogsDirectory: String;
begin
  if CurStep = ssPostInstall then
  begin
    ConfigDirectory := ExpandConstant('{commonappdata}\CallBridge');
    LogsDirectory := ExpandConstant('{commonappdata}\CallBridge\logs');

    ForceDirectories(ConfigDirectory);
    ForceDirectories(LogsDirectory);
  end;
end;