#if (PREPROCVER >> 8) != 0x060703
  #error Inno Setup 6.7.3 is required.
#endif

[Setup]
AppName=Hub uninstall argument contract test
AppVersion=1.0
DefaultDirName={tmp}\hub-uninstall-contract
CreateAppDir=no
Uninstallable=no
CreateUninstallRegKey=no
PrivilegesRequired=lowest
OutputBaseFilename=InstallerArgumentsTests

[Code]
#include "..\installer\UninstallArguments.iss"
var Lines: TStringList; Failures, Checks: Integer;

procedure CheckArguments(Name: String; HasCustomRoot: Boolean; DataRoot: String; ExpectedAccepted: Boolean; ExpectedArguments: String);
var Arguments: String; Accepted: Boolean;
begin
  Accepted := TryBuildRollbackArguments(HasCustomRoot, DataRoot, Arguments);
  Checks := Checks + 1;
  if (Accepted = ExpectedAccepted) and (Arguments = ExpectedArguments) then
    Lines.Add('PASS ' + Name)
  else begin
    Failures := Failures + 1;
    Lines.Add('FAIL ' + Name);
  end;
end;

function InitializeSetup(): Boolean;
var Arguments, Expected: String; Accepted: Boolean;
begin
  Lines := TStringList.Create;
  try
    CheckArguments('default root', False, '', True, 'rollback');
    CheckArguments('drive absolute', True, 'D:\synthetic', True, 'rollback --data-root "D:\synthetic"');
    CheckArguments('spaces and Unicode', True, 'D:\synthetic space\中文', True, 'rollback --data-root "D:\synthetic space\中文"');
    CheckArguments('drive root quote boundary', True, 'D:\', True, 'rollback --data-root "D:\\"');
    CheckArguments('trailing slash quote boundary', True, 'D:\synthetic\', True, 'rollback --data-root "D:\synthetic\\"');
    CheckArguments('UNC absolute', True, '\\synthetic-server\share\test', True, 'rollback --data-root "\\synthetic-server\share\test"');
    CheckArguments('explicit empty', True, '', False, '');
    CheckArguments('relative', True, 'synthetic', False, '');
    CheckArguments('drive relative', True, 'D:synthetic', False, '');
    CheckArguments('root relative', True, '\synthetic', False, '');
    CheckArguments('missing UNC share', True, '\\synthetic-server\', False, '');
    CheckArguments('device namespace', True, '\\.\synthetic', False, '');
    CheckArguments('embedded quote', True, 'D:\synthetic"bad', False, '');
    CheckArguments('control newline', True, 'D:\synthetic' + #10 + 'bad', False, '');
    CheckArguments('control tab', True, 'D:\synthetic' + #9 + 'bad', False, '');
    CheckArguments('invalid drive', True, '1:\synthetic', False, '');
    Accepted := TryReadRollbackArguments(Arguments);
    Expected := ExpandConstant('{param:ExpectedRead|default}');
    Checks := Checks + 1;
    if ((Expected = 'default') and Accepted and (Arguments = 'rollback')) or
       ((Expected = 'custom') and Accepted and (Arguments = 'rollback --data-root "D:\synthetic space\中文"')) or
       ((Expected = 'reject') and not Accepted and (Arguments = '')) then
      Lines.Add('PASS command-line parameter routing')
    else begin
      Failures := Failures + 1;
      Lines.Add('FAIL command-line parameter routing');
    end;
    Lines.Add(IntToStr(Checks - Failures) + '/' + IntToStr(Checks) + ' passed');
    Lines.SaveToFile(ExpandConstant('{param:Report}'));
  finally
    Lines.Free;
  end;
  Result := False; // Abort before any installation, registry writes or uninstaller creation.
end;
