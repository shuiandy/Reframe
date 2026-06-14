; Reframe installer script for Inno Setup 6.3+
; ---------------------------------------------------------------------------
; Builds a wizard-based setup for Reframe (WinUI 3 / .NET 9 / unpackaged /
; requireAdministrator). The .NET runtime is framework-dependent: if the
; .NET 9 Desktop Runtime is missing on the target machine, the installer
; downloads and silently installs it before copying files.
;
; This script is meant to be compiled by tools\build-installer.ps1, which
; passes the required defines on the ISCC command line:
;
;   ISCC.exe /DMyAppVersion=1.2.0 /DPublishDir=C:\...\Reframe\publish_out Reframe.iss
;
;   MyAppVersion : version string, read from Reframe.csproj <Version>
;   PublishDir   : absolute path to the framework-dependent publish output
;
; Compiling the .iss directly in the IDE will fail unless these are defined;
; that is intentional - always build through build-installer.ps1.
; ---------------------------------------------------------------------------

#ifndef MyAppVersion
  #error MyAppVersion is not defined. Compile via tools\build-installer.ps1 (passes /DMyAppVersion=...).
#endif
#ifndef PublishDir
  #error PublishDir is not defined. Compile via tools\build-installer.ps1 (passes /DPublishDir=...).
#endif

#define MyAppName "Reframe"
#define MyAppExeName "Reframe.exe"
#define MyAppPublisher "shuiandy"
#define MyAppURL "https://github.com/shuiandy/Reframe"

; Evergreen link for the .NET 9 Desktop Runtime (x64). Resolved 2026-06:
;   301 -> https://builds.dotnet.microsoft.com/.../9.0.x/windowsdesktop-runtime-9.0.x-win-x64.exe
#define DotNetRuntimeUrl "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe"

[Setup]
; Stable, fixed AppId GUID. Generated once and hard-coded: upgrades and
; uninstall are keyed on this GUID, so it MUST NOT change between releases.
AppId={{8F3C1A47-2E9B-4D6A-9C7F-1B5E0A2D6F84}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}

; Reframe is itself an administrator application installed into Program Files.
PrivilegesRequired=admin
DefaultDirName={autopf}\Reframe
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes

; x64 only. Reframe targets win-x64.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; MIT license shown in the wizard.
LicenseFile=..\..\LICENSE

; Setup branding + Add/Remove Programs icon.
SetupIconFile=..\..\Assets\reframe.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

; Output: dist\Reframe-Setup-v<ver>-win-x64.exe (relative to this .iss in tools\installer\).
OutputDir=..\..\dist
OutputBaseFilename=Reframe-Setup-v{#MyAppVersion}-win-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Whole framework-dependent publish output (PublishDir passed in via /DPublishDir).
; recursesubdirs picks up the WinAppSDK native files, resources.pri, Assets, etc.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Start menu entry.
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; Optional desktop shortcut (desktopicon task, ticked by default).
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Offer to launch right after install. Reframe is requireAdministrator, but the
; setup is already elevated (PrivilegesRequired=admin), so the child launches OK.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Remove the start-on-login scheduled task Reframe creates (task name "Reframe",
; see Services\StartupTaskService.cs). Without this, uninstalling would leave a
; dangling ONLOGON task pointing at the deleted exe. /F so it never prompts;
; runhidden so no console flashes; the || exit /b 0 swallows the non-zero code
; when the task doesn't exist (autostart was never enabled). User config under
; %LOCALAPPDATA%\Reframe is deliberately left in place.
Filename: "{cmd}"; Parameters: "/c schtasks /Delete /TN ""Reframe"" /F || exit /b 0"; Flags: runhidden; RunOnceId: "DelReframeStartupTask"

[Code]
{ --------------------------------------------------------------------------
  .NET 9 Desktop Runtime detection + on-demand install.

  Detection: look for a 9.x folder under
    %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\9.*
  This is the install location for the WindowsDesktop (WPF/WinForms/WinUI host)
  shared framework that a framework-dependent WinUI 3 app needs. We avoid
  shelling out to `dotnet --list-runtimes` (dotnet may not be on PATH).

  If missing, PrepareToInstall downloads the evergreen installer and runs it
  silently. Exit codes: 0 = success, 3010 = success but reboot required,
  anything else = failure (we report it and abort the install).
  -------------------------------------------------------------------------- }

function IsDotNet9DesktopInstalled(): Boolean;
var
  BaseDir: String;
  FindRec: TFindRec;
begin
  Result := False;
  BaseDir := ExpandConstant('{commonpf}\dotnet\shared\Microsoft.WindowsDesktop.App');
  if not DirExists(BaseDir) then
    exit;

  { Enumerate version subfolders; succeed on the first one named 9.* }
  if FindFirst(BaseDir + '\*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if (FindRec.Name <> '.') and (FindRec.Name <> '..') then
          begin
            if Copy(FindRec.Name, 1, 2) = '9.' then
            begin
              Result := True;
              exit;
            end;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  DownloadPage: TDownloadWizardPage;
  InstallerPath: String;
  ResultCode: Integer;
begin
  Result := '';

  { Already present -> nothing to do. }
  if IsDotNet9DesktopInstalled() then
    exit;

  { Download the evergreen .NET 9 Desktop Runtime to a temp file. Uses Inno 6.3+
    built-in download support (CreateDownloadPage / idpDownload-style API). }
  DownloadPage := CreateDownloadPage(
    'Downloading .NET 9 Desktop Runtime',
    'Reframe needs the .NET 9 Desktop Runtime, which was not found on this PC. ' +
      'Setup will download and install it now.',
    nil);
  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetRuntimeUrl}', 'windowsdesktop-runtime-9-win-x64.exe', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      { User cancelled, or the download failed (no network, etc.). }
      Result := 'Failed to download the .NET 9 Desktop Runtime.' + #13#10 +
        'Please check your internet connection and try again, or install it ' +
        'manually from https://dotnet.microsoft.com/download/dotnet/9.0 ' +
        '(Desktop Runtime, x64).' + #13#10 + #13#10 +
        'Details: ' + GetExceptionMessage;
      exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  { Run the runtime installer silently. }
  InstallerPath := ExpandConstant('{tmp}\windowsdesktop-runtime-9-win-x64.exe');
  if not Exec(InstallerPath, '/install /quiet /norestart', '',
             SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Could not start the .NET 9 Desktop Runtime installer.' + #13#10 +
      'You can install it manually from ' +
      'https://dotnet.microsoft.com/download/dotnet/9.0 (Desktop Runtime, x64).';
    exit;
  end;

  case ResultCode of
    0:
      ; { success }
    3010:
      NeedsRestart := True; { success, reboot pending - harmless to continue }
  else
    Result := Format(
      'The .NET 9 Desktop Runtime installer failed (exit code %d).' + #13#10 +
      'Please install it manually from ' +
      'https://dotnet.microsoft.com/download/dotnet/9.0 (Desktop Runtime, x64), ' +
      'then run Reframe setup again.', [ResultCode]);
  end;
end;
