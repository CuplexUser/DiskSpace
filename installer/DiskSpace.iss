; Inno Setup script for DiskSpace.
;
; Build it through the build script rather than by hand, so the version in the installer
; matches the version in the executable:
;
;     ./build.ps1 Installer
;
; Compiling directly also works, as long as a publish already exists in artifacts/publish:
;
;     ISCC.exe installer\DiskSpace.iss /DAppVersion=0.1.0

#define AppName "DiskSpace"
#define AppPublisher "Martin Dahl"
#define AppExeName "DiskSpace.exe"

; Never reuse this GUID for another product. It is the identity Windows upgrades against,
; and it is what lets a new installer find and replace an older one instead of installing
; a second copy alongside it.
#define AppGuid "BCEA5450-13B1-47A1-84A6-41127B0D9079"

; AppVersion is what people see and may carry a pre-release label (0.2.0-beta.1).
; AppVersionNumeric is the plain three-part number, because the Windows version resource
; and the upgrade comparison can only work with digits.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#ifndef AppVersionNumeric
  #define AppVersionNumeric AppVersion
#endif

#ifndef PayloadDir
  #define PayloadDir "..\artifacts\publish"
#endif

#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

; Defined by the build script when publishing with -SelfContained. It suppresses the
; runtime prerequisite check, because the runtime is inside the executable.
; #define SelfContained

[Setup]
AppId={{{#AppGuid}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersionNumeric}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
OutputDir={#OutputDir}
OutputBaseFilename={#AppName}-{#AppVersion}-win-x64-setup
SetupIconFile={#SourcePath}..\src\DiskSpace.App\Assets\DiskSpace.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
ShowLanguageDialog=no

; The application requires administrator rights at runtime and installs under Program Files,
; so there is no useful per-user mode to offer.
PrivilegesRequired=admin

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; Upgrade behavior. An install that finds the same AppId already present replaces it in
; place: same directory, same Start menu entry, same choice of desktop icon, and one entry
; in Add/Remove Programs rather than two. The directory page is skipped, because moving an
; existing installation is not what someone running an upgrade is asking for.
UsePreviousAppDir=yes
UsePreviousGroup=yes
UsePreviousTasks=yes
DisableDirPage=auto
AllowNoIcons=yes

; Restart Manager finds a running copy and closes it, so an upgrade does not fail on a
; locked executable. The app creates no mutex of its own, which is why this is the mechanism.
CloseApplications=yes
RestartApplications=no

; Picked up automatically once a LICENSE file exists at the repository root.
#if FileExists(AddBackslash(SourcePath) + "..\LICENSE")
LicenseFile={#SourcePath}..\LICENSE
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadDir}\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Anything else the publish produced, for a self-contained or multi-file build.
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Excludes: "*.pdb,{#AppExeName}"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; runascurrentuser is what makes this work at all. A postinstall entry is otherwise launched
; with the credentials of the user who started Setup, before elevation, and CreateProcess
; cannot elevate: starting a requireAdministrator executable that way fails with code 740.
; Running it from Setup's own elevated token launches the app straight away, with no second
; UAC prompt on top of the one already given to the installer.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,DiskSpace}"; Flags: nowait postinstall skipifsilent runascurrentuser

[Code]
const
  AppGuid = '{#AppGuid}';
  RuntimeDownloadUrl = 'https://dotnet.microsoft.com/download/dotnet/10.0';

var
  InstalledVersion: String;

function UninstallRegKey: String;
begin
  Result := 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{' + AppGuid + '}_is1';
end;

{ The version already on the machine, or an empty string if this is a first install.

  The 64-bit view comes first because that is where this installer registers itself. The
  other two views are checked so an upgrade still finds installations this script did not
  make: a 32-bit installer would have written to WOW6432Node, and a per-user one to HKCU. }
function GetInstalledVersion: String;
begin
  Result := '';

  if IsX64Compatible and RegQueryStringValue(HKLM64, UninstallRegKey, 'DisplayVersion', Result) then
    Exit;

  if RegQueryStringValue(HKLM32, UninstallRegKey, 'DisplayVersion', Result) then
    Exit;

  RegQueryStringValue(HKCU, UninstallRegKey, 'DisplayVersion', Result);
end;

{ Drops a pre-release label or build metadata, so 0.2.0-beta.1 compares as 0.2.0. }
function NumericPart(Version: String): String;
var
  Position: Integer;
begin
  Result := Trim(Version);

  Position := Pos('-', Result);
  if Position > 0 then
    Result := Copy(Result, 1, Position - 1);

  Position := Pos('+', Result);
  if Position > 0 then
    Result := Copy(Result, 1, Position - 1);
end;

{ Negative when A is older than B, zero when equal, positive when A is newer. Returns zero
  for anything unparseable, which lets the install proceed rather than blocking on a
  version string this code failed to understand. }
function CompareVersions(A, B: String): Integer;
var
  PackedA, PackedB: Int64;
begin
  Result := 0;
  if StrToVersion(NumericPart(A), PackedA) and StrToVersion(NumericPart(B), PackedB) then
    Result := ComparePackedVersion(PackedA, PackedB);
end;

function IsUpgrade: Boolean;
begin
  Result := (InstalledVersion <> '') and
            (CompareVersions(InstalledVersion, '{#AppVersionNumeric}') < 0);
end;

{ Looks for any 10.x Windows Desktop runtime in the shared framework directory. This is
  where the official runtime installer puts it; a DOTNET_ROOT install elsewhere is not
  detected, which is why a miss only warns instead of blocking. }
function DesktopRuntimeInstalled: Boolean;
var
  FindRec: TFindRec;
  SharedFramework: String;
begin
  Result := False;
  SharedFramework := ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App');

  if FindFirst(SharedFramework + '\10.*', FindRec) then
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

{ Decides what to do about a copy that is already installed. An older version upgrades in
  place without asking. The same version asks first, because a reinstall is usually a
  mistake. An older installer over a newer install asks too, and refuses outright when
  silent, so an automated rollback has to be deliberate rather than accidental. }
function ConfirmExistingInstall: Boolean;
var
  Comparison: Integer;
begin
  Result := True;
  InstalledVersion := GetInstalledVersion;

  if InstalledVersion = '' then
    Exit;

  Comparison := CompareVersions(InstalledVersion, '{#AppVersionNumeric}');

  if Comparison < 0 then
    Exit;

  if Comparison > 0 then
  begin
    if WizardSilent then
    begin
      Log('Refusing to downgrade ' + InstalledVersion + ' to {#AppVersion} in silent mode.');
      Result := False;
      Exit;
    end;

    Result := MsgBox(
      'Version ' + InstalledVersion + ' is already installed, which is newer than ' +
      '{#AppVersion}.' + #13#10#13#10 + 'Replace it with the older version?',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
    Exit;
  end;

  if WizardSilent then
    Exit;

  Result := MsgBox(
    '{#AppName} {#AppVersion} is already installed.' + #13#10#13#10 +
    'Reinstall it over the existing copy?',
    mbConfirmation, MB_YESNO) = IDYES;
end;

function InitializeSetup: Boolean;
var
  Response: Integer;
  ErrorCode: Integer;
begin
  Result := ConfirmExistingInstall;
  if not Result then
    Exit;

#ifndef SelfContained
  if DesktopRuntimeInstalled or WizardSilent then
    Exit;

  Response := MsgBox(
    'DiskSpace needs the .NET 10 Desktop Runtime, which does not appear to be installed.' + #13#10#13#10 +
    'Click Yes to open the download page and stop the installation, or No to install anyway ' +
    'and add the runtime later.',
    mbConfirmation, MB_YESNO);

  if Response = IDYES then
  begin
    ShellExec('open', RuntimeDownloadUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
#endif
end;

{ Says plainly on the final page that this replaces an existing installation, so nobody has
  to infer it from the directory page not appearing. }
function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  Summary: String;
begin
  Summary := '';

  if IsUpgrade then
    Summary := 'Upgrade:' + NewLine + Space + 'DiskSpace ' + InstalledVersion +
               ' will be replaced with {#AppVersion}.' + NewLine + NewLine
  else if InstalledVersion <> '' then
    Summary := 'Reinstall:' + NewLine + Space + 'DiskSpace ' + InstalledVersion +
               ' will be reinstalled.' + NewLine + NewLine;

  Result := Summary + MemoDirInfo + NewLine + NewLine + MemoGroupInfo;

  if MemoTasksInfo <> '' then
    Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

{ Logs are the only account of what the tool deleted, so removing them is an explicit choice
  rather than a side effect of uninstalling. Quarantined folders are never touched here at
  all: they may still be the only copy of something, and they live on whichever volume had
  room, not under the application directory. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  LogDirectory: String;
begin
  if CurUninstallStep <> usPostUninstall then
    Exit;

  LogDirectory := ExpandConstant('{localappdata}\DiskSpace');
  if not DirExists(LogDirectory) then
    Exit;

  if MsgBox('Also remove the cleanup logs in ' + LogDirectory + '?' + #13#10#13#10 +
            'These record what DiskSpace deleted. Quarantined items are kept either way.',
            mbConfirmation, MB_YESNO) = IDYES then
    DelTree(LogDirectory, True, True, True);
end;
