#define MyAppName "Instant Replay"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "Instant Replay"
#define MyAppExeName "replay.exe"
#define MyAppUrl "https://github.com/vpskvd/fbuzgcmodkjz"

[Setup]
AppId={{B3D9E5B1-2D6F-4B54-9C4D-1A8E3D9D7F10}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={localappdata}\Programs\InstantReplay
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir=..\dist
OutputBaseFilename=InstantReplaySetup
SetupIconFile=..\assets\icons\icon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
DisableWelcomePage=no
ShowLanguageDialog=no
CloseApplications=force
CloseApplicationsFilter=replay.exe
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\build\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
var
  DownloadPage: TDownloadWizardPage;
  DotNetChecked: Boolean;

function HasUpdateSwitch: Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if (CompareText(ParamStr(I), '--update') = 0) or (CompareText(ParamStr(I), '/update') = 0) then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function ParseVersionPart(var S: String): Integer;
var
  DotPos: Integer;
  Part: String;
begin
  DotPos := Pos('.', S);
  if DotPos > 0 then
  begin
    Part := Copy(S, 1, DotPos - 1);
    Delete(S, 1, DotPos);
  end
  else
  begin
    Part := S;
    S := '';
  end;
  Result := StrToIntDef(Part, -1);
end;

function CompareDotNetVersions(A, B: String): Integer;
var
  I, VA, VB: Integer;
begin
  for I := 1 to 3 do
  begin
    VA := ParseVersionPart(A);
    VB := ParseVersionPart(B);
    if VA < VB then begin Result := -1; Exit; end;
    if VA > VB then begin Result := 1; Exit; end;
  end;
  Result := 0;
end;

function IsValidDotNet8Tag(const S: String): Boolean;
var
  T: String;
  A, B, C: Integer;
begin
  Result := False;
  if Copy(S, 1, 2) <> '8.' then Exit;
  T := S;
  A := ParseVersionPart(T);
  B := ParseVersionPart(T);
  C := ParseVersionPart(T);
  Result := (A = 8) and (B >= 0) and (C >= 0) and (T = '');
end;

function FindDotNet8InDir(const Dir: String; var Best: String): Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if not DirExists(Dir) then Exit;
  if FindFirst(AddBackslash(Dir) + '8.*', FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          if IsValidDotNet8Tag(FindRec.Name) then
          begin
            if (Best = '') or (CompareDotNetVersions(FindRec.Name, Best) > 0) then
              Best := FindRec.Name;
            Result := True;
          end;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function FindDotNet8InRegistry(const RootKey: Integer; const SubKey: String; var Best: String): Boolean;
var
  Names: TArrayOfString;
  I: Integer;
begin
  Result := False;
  if RegGetValueNames(RootKey, SubKey, Names) then
  begin
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      if IsValidDotNet8Tag(Names[I]) then
      begin
        if (Best = '') or (CompareDotNetVersions(Names[I], Best) > 0) then
          Best := Names[I];
        Result := True;
      end;
    end;
  end;
end;

function GetInstalledDotNet8Version: String;
var
  Best: String;
begin
  Best := '';
  FindDotNet8InDir(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App'), Best);
  FindDotNet8InDir(ExpandConstant('{commonpf32}\dotnet\shared\Microsoft.WindowsDesktop.App'), Best);
  FindDotNet8InRegistry(HKEY_LOCAL_MACHINE, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Best);
  FindDotNet8InRegistry(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App', Best);
  Result := Best;
end;

function FetchLatestDotNet8Version: String;
var
  Req: Variant;
  Body, Marker: String;
  P, Q1, Q2: Integer;
begin
  Result := '';
  try
    Req := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    Req.SetTimeouts(4000, 4000, 4000, 6000);
    Req.Open('GET', 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json', False);
    Req.Send();
    if Req.Status = 200 then
    begin
      Body := Req.ResponseText;
      Marker := '"latest-runtime"';
      P := Pos(Marker, Body);
      if P > 0 then
      begin
        Delete(Body, 1, P + Length(Marker) - 1);
        Q1 := Pos('"', Body);
        if Q1 > 0 then
        begin
          Delete(Body, 1, Q1);
          Q2 := Pos('"', Body);
          if Q2 > 0 then
          begin
            Marker := Copy(Body, 1, Q2 - 1);
            if IsValidDotNet8Tag(Marker) then
              Result := Marker;
          end;
        end;
      end;
    end;
  except
    Result := '';
  end;
end;

function IsWebView2Installed: Boolean;
var
  Pv: String;
begin
  Result := False;
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0') then
  begin
    Result := True;
    Exit;
  end;
  if RegQueryStringValue(HKEY_LOCAL_MACHINE, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0') then
  begin
    Result := True;
    Exit;
  end;
  if RegQueryStringValue(HKEY_CURRENT_USER, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Pv) and (Pv <> '') and (Pv <> '0.0.0.0') then
    Result := True;
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

function DownloadRuntimeFile(const Url, FileName: String): String;
begin
  if WizardSilent then
  begin
    DownloadTemporaryFile(Url, FileName, '', @OnDownloadProgress);
    Result := ExpandConstant('{tmp}\') + FileName;
  end
  else
  begin
    DownloadPage.Clear;
    DownloadPage.Add(Url, FileName, '');
    DownloadPage.Show;
    try
      DownloadPage.Download;
      Result := ExpandConstant('{tmp}\') + FileName;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

function EnsurePrereqs(var ErrorMsg: String): Boolean;
var
  InstalledDotNet, LatestDotNet, InstallerPath, ModeArg: String;
  NeedDotNet, NeedWv2: Boolean;
  ResCode: Integer;
begin
  Result := True;
  if DotNetChecked then Exit;

  InstalledDotNet := GetInstalledDotNet8Version;
  LatestDotNet := FetchLatestDotNet8Version;

  NeedDotNet := (InstalledDotNet = '') or ((LatestDotNet <> '') and (CompareDotNetVersions(InstalledDotNet, LatestDotNet) < 0));
  NeedWv2 := not IsWebView2Installed;

  if NeedDotNet then
  begin
    try
      InstallerPath := DownloadRuntimeFile('https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe', 'windowsdesktop-runtime-win-x64.exe');
      if WizardSilent then ModeArg := '/quiet' else ModeArg := '/passive';
      if not Exec(InstallerPath, '/install ' + ModeArg + ' /norestart', '', SW_SHOWNORMAL, ewWaitUntilTerminated, ResCode) then
      begin
        ErrorMsg := 'Failed to launch the .NET 8 Desktop Runtime installer.';
        Result := False;
        Exit;
      end;
      if (ResCode <> 0) and (ResCode <> 3010) and (ResCode <> 1638) then
      begin
        if GetInstalledDotNet8Version = '' then
        begin
          ErrorMsg := Format('.NET 8 Desktop Runtime setup exited with code %d.', [ResCode]);
          Result := False;
          Exit;
        end;
      end;
    except
      if InstalledDotNet = '' then
      begin
        ErrorMsg := 'Failed to download .NET 8 Desktop Runtime. Check your internet connection and try again.';
        Result := False;
        Exit;
      end;
    end;
  end;

  if NeedWv2 then
  begin
    try
      InstallerPath := DownloadRuntimeFile('https://go.microsoft.com/fwlink/p/?LinkId=2124703', 'MicrosoftEdgeWebview2Setup.exe');
      Exec(InstallerPath, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResCode);
    except
      { WebView2 failure is non-fatal if already partially present }
    end;
  end;

  DotNetChecked := True;
end;

procedure InitializeWizard;
begin
  DotNetChecked := False;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  ErrMsg: String;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    if not EnsurePrereqs(ErrMsg) then
    begin
      SuppressibleMsgBox(ErrMsg, mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResCode: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM replay.exe /F /T', '', SW_HIDE, ewWaitUntilTerminated, ResCode);
  if WizardSilent and (not DotNetChecked) then
  begin
    if not EnsurePrereqs(Result) then
      Exit;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResCode: Integer;
begin
  if (CurStep = ssPostInstall) and (WizardSilent or HasUpdateSwitch) then
  begin
    if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
      Exec(ExpandConstant('{app}\{#MyAppExeName}'), '', ExpandConstant('{app}'), SW_SHOWNORMAL, ewNoWait, ResCode);
  end;
end;
