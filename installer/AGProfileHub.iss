#ifndef SourceRoot
  #error SourceRoot must point to the published AGProfileHub directory.
#endif
#ifndef MyAppVersion
  #error MyAppVersion must match package-manifest.json.
#endif

#define MyAppName "Antigravity Profile Hub"
#define MyAppPublisher "Aliarrrrr"
#define MyAppExeName "AGProfileHub.Gui.exe"

[Setup]
AppId={{D9A7D463-9CFB-4DCA-AF2E-CEAF08E53E20}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\AntigravityProfileHub
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=AGProfileHub-Setup-win-x64-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
UninstallDisplayIcon={app}\hub\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} installer
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\hub\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\hub\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\hub\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
function DependencySummary(): String;
var
  Missing: String;
begin
  Missing := '';
  if not FileExists(ExpandConstant('{localappdata}\Programs\antigravity\Antigravity.exe')) then
    Missing := '- Official Antigravity Desktop' + #13#10;
  if not FileExists(ExpandConstant('{pf}\Sandboxie-Plus\Start.exe')) and
     not FileExists(ExpandConstant('{pf}\Sandboxie\Start.exe')) then
    Missing := Missing + '- Sandboxie Plus' + #13#10;
  Result := Missing;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Missing: String;
begin
  if (CurStep = ssPostInstall) and (not WizardSilent) then
  begin
    Missing := DependencySummary();
    if Missing <> '' then
      MsgBox('Installation completed, but these external dependencies were not detected:' + #13#10 + #13#10 +
        Missing + #13#10 + 'Install them before preparing or launching Profiles. No dependency was installed silently.',
        mbInformation, MB_OK);
  end;
end;
