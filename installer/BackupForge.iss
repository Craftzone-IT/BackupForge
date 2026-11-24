; BackupForge installer (SPEC 12.) — built by CI: iscc /DAppVersion=x.y.z /DPublishDir=..\artifacts\publish BackupForge.iss
#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish"
#endif

[Setup]
AppId={{6F1B6A33-2B3E-4E4B-9C61-4F0D6E1B7A21}
AppName=BackupForge
AppVersion={#AppVersion}
VersionInfoVersion={#AppVersion}
AppPublisher=BackupForge
DefaultDirName={autopf}\BackupForge
DefaultGroupName=BackupForge
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.14393
OutputDir=Output
OutputBaseFilename=BackupForge-Setup-{#AppVersion}
LicenseFile=license.txt
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
ChangesEnvironment=yes
UninstallDisplayName=BackupForge
UninstallDisplayIcon={app}\BackupForge.exe
CloseApplications=yes

[Languages]
Name: "hu"; MessagesFile: "compiler:Languages\Hungarian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
hu.AddToPath=A bf.exe hozzáadása a PATH-hoz
en.AddToPath=Add bf.exe to the PATH
hu.LaunchGui=A konfigurátor indítása
en.LaunchGui=Start the configurator
hu.KeepData=Megtartod a beállításokat és az előzményeket (C:\ProgramData\BackupForge)?%n%nA mentési célokhoz, a Versions mappákhoz és a restic-tárolókhoz az eltávolító soha nem nyúl.
en.KeepData=Keep the settings and the history (C:\ProgramData\BackupForge)?%n%nThe uninstaller never touches backup targets, Versions folders or restic repositories.
hu.SetupFailed=A beállítás nem sikerült (lásd a telepítési naplót): %1
en.SetupFailed=Setup step failed (see the setup log): %1

[Tasks]
Name: "path"; Description: "{cm:AddToPath}"

[Files]
Source: "{#PublishDir}\service\BackupForge.Service.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\cli\bf.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\gui\BackupForge.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PublishDir}\gui\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\third_party\restic\restic.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\third_party\restic\LICENSE"; DestDir: "{app}"; DestName: "restic-LICENSE.txt"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\docs\EVENT-IDS.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\SQL-PERMISSIONS.md"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{group}\BackupForge"; Filename: "{app}\BackupForge.exe"
Name: "{autoprograms}\BackupForge"; Filename: "{app}\BackupForge.exe"

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: path; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\BackupForge.exe"; Description: "{cm:LaunchGui}"; Flags: postinstall nowait skipifsilent runascurrentuser shellexec

[Code]
var
  SetupLog: String;

function NeedsAddPath(Dir: String): Boolean;
var
  Paths: String;
begin
  if not RegQueryStringValue(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment', 'Path', Paths) then
    Result := True
  else
    Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

function Sc(Params: String): Integer;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\sc.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('sc ' + Params + ' -> ' + IntToStr(Code));
  Result := Code;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  // upgrade: stop the running service first (running jobs get 60 s to finish as interrupted)
  Sc('stop BackupForge');
  Sleep(3000);
  Result := '';
end;

procedure RunHelper(Params: String);
var
  Code: Integer;
begin
  SetupLog := ExpandConstant('{commonappdata}\BackupForge\setup.log');
  ForceDirectories(ExpandConstant('{commonappdata}\BackupForge'));
  Exec(ExpandConstant('{app}\BackupForge.Service.exe'), Params + ' --log "' + SetupLog + '"', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Log('helper ' + Params + ' -> ' + IntToStr(Code));
  if Code <> 0 then
  begin
    if WizardSilent then Log(FmtMessage(CustomMessage('SetupFailed'), [SetupLog]))
    else MsgBox(FmtMessage(CustomMessage('SetupFailed'), [SetupLog]), mbError, MB_OK);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Params, ConfigFile, SecretsFile: String;
begin
  if CurStep = ssPostInstall then
  begin
    Params := '--setup-install';
    ConfigFile := ExpandConstant('{param:CONFIG|}');
    SecretsFile := ExpandConstant('{param:SECRETS|}');
    if ConfigFile <> '' then Params := Params + ' --config "' + ConfigFile + '"';
    if SecretsFile <> '' then Params := Params + ' --secrets "' + SecretsFile + '"';
    RunHelper(Params);
    if Sc('query BackupForge') <> 0 then
      Sc('create BackupForge binPath= "\"' + ExpandConstant('{app}') + '\BackupForge.Service.exe\"" start= delayed-auto DisplayName= "BackupForge mentés"')
    else
      Sc('config BackupForge binPath= "\"' + ExpandConstant('{app}') + '\BackupForge.Service.exe\"" start= delayed-auto');
    Sc('description BackupForge "BackupForge: fájl-, restic- és SQL-mentés"');
    Sc('sidtype BackupForge unrestricted');
    Sc('failure BackupForge reset= 86400 actions= restart/60000/restart/60000/restart/60000');
    Sc('start BackupForge');
  end;
end;

var
  PurgeData: Boolean;

function InitializeUninstall(): Boolean;
var
  I: Integer;
begin
  PurgeData := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/PURGE') = 0 then PurgeData := True;
  if (not PurgeData) and (not UninstallSilent) then
    PurgeData := MsgBox(CustomMessage('KeepData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON1) = IDNO;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Code: Integer;
  Params: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    Sc('stop BackupForge');
    Sleep(5000);
    Params := '--setup-uninstall';
    if PurgeData then Params := Params + ' --purge';
    Exec(ExpandConstant('{app}\BackupForge.Service.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code);
    Sc('delete BackupForge');
  end;
end;
