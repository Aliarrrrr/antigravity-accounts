#if (PREPROCVER >> 8) != 0x060703
  #error Inno Setup 6.7.3 is required.
#endif
#ifndef SourceRoot
  #error SourceRoot is required.
#endif
#ifndef MyAppVersion
  #error MyAppVersion is required.
#endif
#ifndef MyFileVersion
  #error MyFileVersion derived from the published GUI is required.
#endif
#define MyAppName "Antigravity Profile Hub Next"

[Setup]
AppId={{E5F89079-8053-4E9D-BAA6-E34871D987C4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
DefaultDirName={localappdata}\Programs\AntigravityProfileHubNext
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=AGProfileHubNext-Setup-win-x64-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=no
ChangesEnvironment=no
UninstallDisplayIcon={app}\hub\AGProfileHubNext.Gui.exe
VersionInfoVersion={#MyFileVersion}

[Tasks]
Name: "desktopicon"; Description: "Create a Next desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\hub\AGProfileHubNext.Gui.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\hub\AGProfileHubNext.Gui.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\hub\AGProfileHubNext.Gui.exe"; Description: "Launch Next"; Flags: nowait postinstall skipifsilent

[Code]
#include "UninstallArguments.iss"
function InitializeUninstall(): Boolean;
var ExitCode: Integer; Arguments: String;
begin
  Result := TryReadRollbackArguments(Arguments);
  if not Result then begin
    SuppressibleMsgBox('Invalid /HubDataRoot. Specify one absolute drive or UNC path without quotes or control characters. Uninstall was not started.', mbError, MB_OK, IDOK);
    Exit;
  end;
  if FileExists(ExpandConstant('{app}\cli\agprofile-next.exe')) then
  begin
    if not Exec(ExpandConstant('{app}\cli\agprofile-next.exe'), Arguments, ExpandConstant('{app}'), SW_HIDE, ewWaitUntilTerminated, ExitCode) then
      Result := False
    else
      Result := ExitCode = 0;
    if not Result then
      MsgBox('Next could not close its profiles or restore callback ownership. Save work, close Next profiles explicitly, then retry uninstall. No force stop was performed.', mbError, MB_OK);
  end;
end;
