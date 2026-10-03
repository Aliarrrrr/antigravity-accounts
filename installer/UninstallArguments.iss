function TryBuildRollbackArguments(HasCustomRoot: Boolean; HubDataRoot: String; var Arguments: String): Boolean;
var Index, ServerEnd: Integer; AbsoluteRoot: Boolean; UncTail: String;
begin
  Arguments := '';
  Result := False;
  if not HasCustomRoot then begin
    Arguments := 'rollback';
    Result := True;
    Exit;
  end;
  if HubDataRoot = '' then Exit;
  for Index := 1 to Length(HubDataRoot) do
    if (HubDataRoot[Index] = '"') or (Ord(HubDataRoot[Index]) < 32) or (Ord(HubDataRoot[Index]) = 127) then Exit;
  AbsoluteRoot := False;
  if Length(HubDataRoot) >= 3 then
    AbsoluteRoot := (((HubDataRoot[1] >= 'A') and (HubDataRoot[1] <= 'Z')) or
      ((HubDataRoot[1] >= 'a') and (HubDataRoot[1] <= 'z'))) and
      (HubDataRoot[2] = ':') and ((HubDataRoot[3] = '\') or (HubDataRoot[3] = '/'));
  if (Length(HubDataRoot) >= 5) and (Copy(HubDataRoot, 1, 2) = '\\') and
     (HubDataRoot[3] <> '.') and (HubDataRoot[3] <> '?') then begin
    UncTail := Copy(HubDataRoot, 3, Length(HubDataRoot));
    ServerEnd := Pos('\', UncTail);
    AbsoluteRoot := (ServerEnd > 1) and (Length(UncTail) > ServerEnd);
    if AbsoluteRoot then AbsoluteRoot := UncTail[ServerEnd + 1] <> '\';
  end;
  if not AbsoluteRoot then Exit;
  Arguments := 'rollback --data-root "' + HubDataRoot;
  // Windows argv parsing requires doubling trailing backslashes before a closing quote.
  Index := Length(HubDataRoot);
  while (Index > 0) and (HubDataRoot[Index] = '\') do begin
    Arguments := Arguments + '\';
    Index := Index - 1;
  end;
  Arguments := Arguments + '"';
  Result := True;
end;

function TryReadRollbackArguments(var Arguments: String): Boolean;
var Index: Integer; Parameter, DataRoot: String; HasCustomRoot: Boolean;
begin
  Arguments := '';
  HasCustomRoot := False;
  DataRoot := '';
  for Index := 1 to ParamCount do begin
    Parameter := ParamStr(Index);
    if (CompareText(Copy(Parameter, 1, 13), '/HubDataRoot=') = 0) or
       (CompareText(Parameter, '/HubDataRoot') = 0) then begin
      if HasCustomRoot then begin
        Result := False;
        Exit;
      end;
      HasCustomRoot := True;
      DataRoot := Copy(Parameter, 14, Length(Parameter));
    end;
  end;
  Result := TryBuildRollbackArguments(HasCustomRoot, DataRoot, Arguments);
end;
