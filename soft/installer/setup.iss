[Setup]
AppId={{72A12E93-00C7-4EA7-96F1-EA8966A77931}
AppName=TrueNorth TradeFlow
AppVersion={#AppVersion}
AppVerName=TrueNorth TradeFlow {#AppVersion}
DefaultDirName={localappdata}\Programs\TrueNorth TradeFlow
DefaultGroupName=TrueNorth TradeFlow
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
Uninstallable=yes
UninstallDisplayName=TrueNorth TradeFlow
UninstallDisplayIcon={app}\TrueNorth.TradeFlow.exe
OutputDir=..\..\..\artifacts\001-windows-shell
OutputBaseFilename=TrueNorth-TradeFlow-Setup-{#AppVersion}-win-x64
Compression=lzma2/normal
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "startup"; Description: "Запускать приложение при входе в Windows"; GroupDescription: "Параметры запуска:"; Flags: checkedonce

[Files]
Source: "..\bin\Release\net10.0-windows\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TrueNorth TradeFlow"; Filename: "{app}\TrueNorth.TradeFlow.exe"
Name: "{group}\Удалить TrueNorth TradeFlow"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TrueNorth TradeFlow"; ValueData: """{app}\TrueNorth.TradeFlow.exe"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Code]
function InitializeUninstall(): Boolean;
var
  ExitCode: Integer;
begin
  Result := True;
  if FileExists(ExpandConstant('{app}\TrueNorth.TradeFlow.exe')) then
  begin
    if not Exec(ExpandConstant('{app}\TrueNorth.TradeFlow.exe'), '--prepare-uninstall', '', SW_HIDE, ewWaitUntilTerminated, ExitCode) then
    begin
      MsgBox('Не удалось попросить приложение завершить работу. Удаление отменено.', mbError, MB_OK);
      Result := False;
    end
    else if ExitCode <> 0 then
    begin
      MsgBox('Приложение не завершилось. Удаление отменено; запущенные файлы не будут удалены принудительно.', mbError, MB_OK);
      Result := False;
    end;
  end;
end;
