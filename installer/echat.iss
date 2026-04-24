#define MyAppName "εChat"
#define FileHandle FileOpen("..\src\EChat.MAUI\version.txt")
#define MyAppVersion FileRead(FileHandle)
#define MyAppPublisher "EChat"
#define MyAppExeName "echat.exe"
#define PublishDir "..\pub\win"
#expr FileClose(FileHandle)

[Setup]
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\EChat
DefaultGroupName={#MyAppName}
OutputDir=..\pub\distr
OutputBaseFilename=EChat-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
UninstallDisplayName={#MyAppName}
SetupIconFile={#PublishDir}\appicon.ico
UninstallDisplayIcon={app}\appicon.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]

var
  DownloadPage: TDownloadWizardPage;
  NeedDotNet, NeedWinAppSdk, PrereqChecked: Boolean;

// ── Detection ────────────────────────────────────────────────────────────────

// .NET 10 Desktop Runtime folders are named "10.x.y" under
// Microsoft.WindowsDesktop.App — use wildcard search with FindFirst.
function IsDotNet10Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst('C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then
  begin
    try
      repeat
        if FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

// Windows App SDK installs as an MSIX package — WindowsApps is ACL-protected
// so DirExists always fails there. Use PowerShell Get-AppxPackage instead.
function IsWinAppSdkInstalled: Boolean;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command ' +
    '"if (Get-AppxPackage -Name Microsoft.WindowsAppRuntime.1.7* ' +
    '-ErrorAction SilentlyContinue) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := (ResultCode = 0);
end;

// ── Wizard ───────────────────────────────────────────────────────────────────

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(
    'Downloading prerequisites',
    'Required runtime components are being downloaded and installed. Please wait.',
    nil);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ResultCode: Integer;
  Msg: String;
begin
  Result := True;

  if CurPageID <> wpReady then Exit;

  // Detect on first visit to the Ready page (PowerShell check ~1 s)
  if not PrereqChecked then
  begin
    PrereqChecked := True;
    NeedDotNet    := not IsDotNet10Installed;
    NeedWinAppSdk := not IsWinAppSdkInstalled;
  end;

  if not NeedDotNet and not NeedWinAppSdk then Exit;

  // Confirm with user
  Msg := 'The following prerequisites are missing and will be downloaded and installed automatically:' + #13#10#13#10;
  if NeedDotNet    then Msg := Msg + '  • .NET 10 Desktop Runtime (~55 MB)' + #13#10;
  if NeedWinAppSdk then Msg := Msg + '  • Windows App SDK Runtime (~15 MB)' + #13#10;
  Msg := Msg + #13#10 + 'An internet connection is required. Continue?';
  if MsgBox(Msg, mbConfirmation, MB_YESNO) = IDNO then
  begin
    Result := False;
    Exit;
  end;

  // Queue downloads
  DownloadPage.Clear;
  if NeedDotNet then
    DownloadPage.Add(
      'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe',
      'dotnet-desktop-runtime-10-x64.exe', '');
  if NeedWinAppSdk then
    DownloadPage.Add(
      'https://aka.ms/windowsappsdk/1.7/latest/windowsappruntimeinstall-x64.exe',
      'winappsdk-runtime-x64.exe', '');

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      MsgBox('Download failed: ' + GetExceptionMessage + #13#10 +
             'Check your internet connection and try again.', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  finally
    DownloadPage.Hide;
  end;

  // Install silently
  if NeedDotNet then
  begin
    if not Exec(ExpandConstant('{tmp}\dotnet-desktop-runtime-10-x64.exe'),
         '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
    begin
      MsgBox('Failed to install .NET 10 Desktop Runtime (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
             'Download manually: https://dotnet.microsoft.com/download/dotnet/10.0', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;

  if NeedWinAppSdk then
  begin
    if not Exec(ExpandConstant('{tmp}\winappsdk-runtime-x64.exe'),
         '--quiet', '', SW_SHOW, ewWaitUntilTerminated, ResultCode)
       or (ResultCode <> 0) then
    begin
      MsgBox('Failed to install Windows App SDK Runtime (exit code ' + IntToStr(ResultCode) + ').' + #13#10 +
             'Download manually: https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads', mbError, MB_OK);
      Result := False;
      Exit;
    end;
  end;
end;

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
