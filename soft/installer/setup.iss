#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\bin\Release\net10.0-windows\win-x64\publish"
#endif

[Setup]
AppId={{72A12E93-00C7-4EA7-96F1-EA8966A77931}
AppName=TrueNorth TradeFlow
AppVersion={#AppVersion}
AppVerName=TrueNorth TradeFlow {#AppVersion}
DefaultDirName={localappdata}\Programs\TrueNorth TradeFlow
DisableDirPage=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
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
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\TrueNorth TradeFlow"; Filename: "{app}\TrueNorth.TradeFlow.exe"
Name: "{group}\Удалить TrueNorth TradeFlow"; Filename: "{uninstallexe}"

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "TrueNorth TradeFlow"; ValueData: """{app}\TrueNorth.TradeFlow.exe"" --startup"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "TrueNorth TradeFlow"; Tasks: not startup; Flags: deletevalue

[Code]
const
  ProductName = 'TrueNorth TradeFlow';
  ProductAppIdKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{72A12E93-00C7-4EA7-96F1-EA8966A77931}_is1';
  AppMutexName = 'Local\TrueNorth.TradeFlow';
  ObserverMutexName = 'Local\TrueNorth.TradeFlow.RecoveryObserver';
  UpdateRequestEventName = 'Local\TrueNorth.TradeFlow.UpdateRequest';
  UpdateReadyEventName = 'Local\TrueNorth.TradeFlow.UpdateReady';
  UpdateRefusedEventName = 'Local\TrueNorth.TradeFlow.UpdateRefused';
  UpdateCommitEventName = 'Local\TrueNorth.TradeFlow.UpdateCommit';
  UpdateCancelEventName = 'Local\TrueNorth.TradeFlow.UpdateCancel';
  DisableEventName = 'Local\TrueNorth.TradeFlow.RecoveryDisable';
  SetupMutexName = 'Local\TrueNorth.TradeFlow.Setup';
  SynchronizeAccess = $00100000;
  EventModifyState = $0002;
  WaitObject0 = $00000000;
  WaitAbandoned0 = $00000080;
  WaitTimeout = $00000102;
  ErrorAlreadyExists = 183;
  PrepareTimeoutSlices = 150;
  PrepareTimeoutSliceMs = 200;
  AppExitTimeoutMs = 30000;
  ObserverExitTimeoutMs = 15000;
  LegacyManualExitTimeoutMs = 120000;

function CreateMutex(lpMutexAttributes: LongWord; bInitialOwner: Boolean; lpName: String): THandle;
  external 'CreateMutexW@kernel32.dll stdcall';
function CreateEvent(lpEventAttributes: LongWord; bManualReset: Boolean; bInitialState: Boolean; lpName: String): THandle;
  external 'CreateEventW@kernel32.dll stdcall';
function OpenEvent(dwDesiredAccess: LongWord; bInheritHandle: Boolean; lpName: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function SetEvent(hEvent: THandle): Boolean;
  external 'SetEvent@kernel32.dll stdcall';
function ResetEvent(hEvent: THandle): Boolean;
  external 'ResetEvent@kernel32.dll stdcall';
function ReleaseMutex(hMutex: THandle): Boolean;
  external 'ReleaseMutex@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function GetLastError(): LongWord;
  external 'GetLastError@kernel32.dll stdcall';

var
  SetupMutex: THandle;
  SetupMutexOwned: Boolean;
  AppMutex: THandle;
  AppMutexOwned: Boolean;
  ObserverMutex: THandle;
  ObserverMutexOwned: Boolean;
  DisableEvent: THandle;
  UpdateRequestEvent: THandle;
  UpdateReadyEvent: THandle;
  UpdateRefusedEvent: THandle;
  UpdateCommitEvent: THandle;
  UpdateCancelEvent: THandle;
  UpdateRequestSent: Boolean;
  UpdateCommitSent: Boolean;
  UpdateResponseRefused: Boolean;
  RecoveryDisableSetBySetup: Boolean;
  ChangeSucceeded: Boolean;
  ChangeFailure: String;

function IsWaitSuccess(Value: LongWord): Boolean;
begin
  Result := (Value = WaitObject0) or (Value = WaitAbandoned0);
end;

function PostCommitFailure(Reason: String): String;
begin
  Result := Reason;
  if UpdateCommitSent then
    Result := Reason + ' Штатный выход уже разрешён; приложение или наблюдатель ещё могут завершать остановку. Файлы не изменены. После завершения запустите приложение вручную.';
end;

function AcquireSetupMutex(): Boolean;
var
  LastError: LongWord;
begin
  Result := False;
  SetupMutex := CreateMutex(0, True, SetupMutexName);
  if SetupMutex = 0 then
  begin
    ChangeFailure := 'Не удалось проверить, не запущен ли другой установщик.';
    Exit;
  end;
  LastError := GetLastError();
  if LastError = ErrorAlreadyExists then
  begin
    CloseHandle(SetupMutex);
    SetupMutex := 0;
    ChangeFailure := 'Другой установщик или удаление TrueNorth TradeFlow уже выполняется.';
    Exit;
  end;
  SetupMutexOwned := True;
  Result := True;
end;

function RegisteredProduct(var InstalledVersion: String; var RegisteredLocation: String;
  var ErrorText: String): Boolean;
var
  DisplayName: String;
  SelectedLocation: String;
begin
  Result := False;
  InstalledVersion := '';
  RegisteredLocation := '';
  ErrorText := '';
  if not RegKeyExists(HKCU, ProductAppIdKey) then
  begin
    ErrorText := 'Регистрация TrueNorth TradeFlow не найдена.';
    Exit;
  end;
  if not RegQueryStringValue(HKCU, ProductAppIdKey, 'DisplayName', DisplayName)
     or not RegQueryStringValue(HKCU, ProductAppIdKey, 'InstallLocation', RegisteredLocation)
     or not RegQueryStringValue(HKCU, ProductAppIdKey, 'DisplayVersion', InstalledVersion) then
  begin
    ErrorText := 'Регистрация установленной версии TrueNorth TradeFlow неполна; файлы не будут заменены.';
    Exit;
  end;

  if (DisplayName <> ProductName) or (Length(RegisteredLocation) < 3) then
  begin
    ErrorText := 'Регистрация TrueNorth TradeFlow содержит недопустимый путь установки; обновление остановлено.';
    Exit;
  end;
  if (RegisteredLocation[2] <> ':')
     or ((RegisteredLocation[3] <> '\') and (RegisteredLocation[3] <> '/')) then
  begin
    ErrorText := 'Путь зарегистрированной установки должен быть абсолютным локальным путём; обновление остановлено.';
    Exit;
  end;

  RegisteredLocation := ExpandFileName(RegisteredLocation);
  SelectedLocation := ExpandFileName(ExpandConstant('{app}'));
  if (Length(RegisteredLocation) <= 3)
     or (CompareText(AddBackslash(RegisteredLocation), AddBackslash(SelectedLocation)) <> 0)
     or not DirExists(RegisteredLocation)
     or not FileExists(AddBackslash(RegisteredLocation) + 'TrueNorth.TradeFlow.exe') then
  begin
    ErrorText := 'Путь регистрации не совпадает с выбранным каталогом приложения или его EXE отсутствует; обновление остановлено.';
    Exit;
  end;
  Result := True;
end;

function AcquireFreshRuntimeMutex(Name: String; Purpose: String; var Handle: THandle;
  var Owned: Boolean): Boolean;
var
  WaitResult: LongWord;
begin
  Result := False;
  Handle := CreateMutex(0, False, Name);
  if Handle = 0 then
  begin
    ChangeFailure := 'Не удалось проверить ' + Purpose + ' перед чистой установкой.';
    Exit;
  end;

  WaitResult := WaitForSingleObject(Handle, 0);
  if WaitResult = WaitObject0 then
  begin
    Owned := True;
    Result := True;
    Exit;
  end;
  if WaitResult = WaitAbandoned0 then
  begin
    Owned := True;
    ChangeFailure := 'Обнаружено неожиданное завершение ' + Purpose + '; чистая установка остановлена.';
    Exit;
  end;
  if WaitResult = WaitTimeout then
    ChangeFailure := 'Уже работает ' + Purpose + '. Закройте его обычным способом и повторите чистую установку.'
  else
    ChangeFailure := 'Не удалось безопасно проверить ' + Purpose + '; чистая установка остановлена.';
end;

function AcquireFreshInstallLocks(): Boolean;
begin
  Result := False;
  if not AcquireFreshRuntimeMutex(AppMutexName, 'экземпляр TrueNorth TradeFlow', AppMutex, AppMutexOwned) then
    Exit;
  if not AcquireFreshRuntimeMutex(ObserverMutexName, 'наблюдатель восстановления', ObserverMutex,
    ObserverMutexOwned) then
    Exit;
  Result := True;
end;

function OpenUpdateEvents(): Boolean;
begin
  Result := False;
  DisableEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, DisableEventName);
  UpdateRequestEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, UpdateRequestEventName);
  UpdateReadyEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, UpdateReadyEventName);
  UpdateRefusedEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, UpdateRefusedEventName);
  UpdateCommitEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, UpdateCommitEventName);
  UpdateCancelEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, UpdateCancelEventName);
  Result := (DisableEvent <> 0) and (UpdateRequestEvent <> 0) and (UpdateReadyEvent <> 0)
    and (UpdateRefusedEvent <> 0) and (UpdateCommitEvent <> 0) and (UpdateCancelEvent <> 0);
end;

function WaitForUpdateResponse(): Boolean;
var
  Attempt: Integer;
  WaitResult: LongWord;
begin
  Result := False;
  for Attempt := 1 to PrepareTimeoutSlices do
  begin
    WaitResult := WaitForSingleObject(UpdateRefusedEvent, 0);
    if WaitResult = WaitObject0 then
    begin
      UpdateResponseRefused := True;
      ChangeFailure := 'Приложение не подтвердило безопасное сохранение состояния; обновление остановлено.';
      Exit;
    end;

    WaitResult := WaitForSingleObject(UpdateReadyEvent, PrepareTimeoutSliceMs);
    if WaitResult = WaitObject0 then
    begin
      if WaitForSingleObject(UpdateRefusedEvent, 0) = WaitObject0 then
      begin
        UpdateResponseRefused := True;
        ChangeFailure := 'Приложение отказалось завершить подготовку; обновление остановлено.';
        Exit;
      end;
      Result := True;
      Exit;
    end;
    if WaitResult <> WaitTimeout then
    begin
      ChangeFailure := 'Не удалось дождаться ответа приложения; обновление остановлено.';
      Exit;
    end;
  end;
  ChangeFailure := 'Приложение не завершило подготовку за 30 секунд; обновление остановлено.';
end;

function RequestCurrentAppShutdown(InstalledVersion: String; IsUninstall: Boolean): Boolean;
var
  WaitResult: LongWord;
  ManualCloseInstructions: String;
begin
  Result := False;
  AppMutex := CreateMutex(0, False, AppMutexName);
  if AppMutex = 0 then
  begin
    ChangeFailure := 'Не удалось проверить состояние приложения; файлы не будут заменены.';
    Exit;
  end;

  WaitResult := WaitForSingleObject(AppMutex, 0);
  if IsWaitSuccess(WaitResult) then
  begin
    if WaitResult = WaitAbandoned0 then
    begin
      AppMutexOwned := True;
      ChangeFailure := 'Предыдущий процесс завершился неожиданно; откройте приложение, дождитесь сверки и повторите обновление.';
      Exit;
    end;
    AppMutexOwned := True;
  end
  else if WaitResult = WaitTimeout then
  begin
    if InstalledVersion = '0.1.0' then
    begin
      DisableEvent := OpenEvent(EventModifyState or SynchronizeAccess, False, DisableEventName);
      if DisableEvent = 0 then
      begin
        ChangeFailure := 'Не удалось подтвердить экземпляр версии 0.1.0; закройте приложение через трей и повторите обновление.';
        Exit;
      end;
      if IsUninstall then
        ManualCloseInstructions := 'Дождитесь завершения текущих операций и сохранения журнала. Закройте приложение через трей, дождитесь окна выхода после сверки и выберите «Оставить заявки и позиции»; заявки не отменяйте. '
          + 'Если исход операции неизвестен, вернитесь в приложение и завершите сверку. После штатного закрытия нажмите OK; программа удаления проверит остановку приложения и наблюдателя.'
      else
        ManualCloseInstructions := 'Дождитесь завершения текущих операций и сохранения журнала. Закройте приложение через трей, дождитесь окна выхода после сверки и выберите «Оставить заявки и позиции»; заявки не отменяйте. '
          + 'Если исход операции неизвестен, вернитесь в приложение и завершите сверку. После штатного закрытия нажмите OK; установщик проверит остановку приложения и наблюдателя.';
      if IsUninstall then
        MsgBox('Версия 0.1.0 не поддерживает безопасный запрос удаления.' + #13#10 + #13#10
          + ManualCloseInstructions, mbInformation, MB_OK)
      else
        MsgBox('Версия 0.1.0 не поддерживает безопасный запрос обновления.' + #13#10 + #13#10
          + ManualCloseInstructions, mbInformation, MB_OK);
      WaitResult := WaitForSingleObject(AppMutex, LegacyManualExitTimeoutMs);
      if WaitResult = WaitAbandoned0 then
      begin
        AppMutexOwned := True;
        ChangeFailure := 'Приложение завершилось неожиданно; обновление остановлено.';
        Exit;
      end;
      if WaitResult <> WaitObject0 then
      begin
        ChangeFailure := 'Версия 0.1.0 осталась запущена; завершите её обычным способом и повторите обновление.';
        Exit;
      end;
      AppMutexOwned := True;
      if WaitForSingleObject(DisableEvent, 0) <> WaitObject0 then
      begin
        ChangeFailure := 'Не подтверждено штатное завершение версии 0.1.0; обновление остановлено.';
        Exit;
      end;
    end
    else
    begin
      if not OpenUpdateEvents() then
      begin
        ChangeFailure := 'Установленное приложение не поддерживает безопасный протокол обновления; файлы не будут заменены.';
        Exit;
      end;
      if WaitForSingleObject(UpdateCancelEvent, 0) = WaitObject0 then
      begin
        ChangeFailure := 'Приложение ещё завершает отмену предыдущего запроса; повторите обновление через несколько секунд.';
        Exit;
      end;

      ResetEvent(UpdateRequestEvent);
      ResetEvent(UpdateReadyEvent);
      ResetEvent(UpdateRefusedEvent);
      ResetEvent(UpdateCommitEvent);
      if not SetEvent(UpdateRequestEvent) then
      begin
        ChangeFailure := 'Не удалось запросить штатную остановку приложения.';
        Exit;
      end;
      UpdateRequestSent := True;
      if not WaitForUpdateResponse() then
        Exit;
      if not SetEvent(UpdateCommitEvent) then
      begin
        ChangeFailure := 'Не удалось подтвердить приложению готовность установщика.';
        Exit;
      end;
      UpdateCommitSent := True;

      WaitResult := WaitForSingleObject(AppMutex, AppExitTimeoutMs);
      if WaitResult = WaitAbandoned0 then
      begin
        AppMutexOwned := True;
        ChangeFailure := PostCommitFailure('Приложение завершилось неожиданно; установщик не заменит его файлы.');
        Exit;
      end;
      if WaitResult <> WaitObject0 then
      begin
        ChangeFailure := PostCommitFailure('Приложение не завершилось за 30 секунд; установщик не заменит его файлы.');
        Exit;
      end;
      AppMutexOwned := True;
    end;
  end
  else
  begin
    ChangeFailure := 'Не удалось проверить владение экземпляром приложения; обновление остановлено.';
    Exit;
  end;

  if DisableEvent = 0 then
    DisableEvent := CreateEvent(0, True, False, DisableEventName);
  if (DisableEvent = 0) or not SetEvent(DisableEvent) then
  begin
    ChangeFailure := PostCommitFailure('Не удалось остановить восстановление процесса; файлы не будут заменены.');
    Exit;
  end;
  RecoveryDisableSetBySetup := True;

  ObserverMutex := CreateMutex(0, False, ObserverMutexName);
  if ObserverMutex = 0 then
  begin
    ChangeFailure := PostCommitFailure('Не удалось проверить наблюдатель восстановления; обновление остановлено.');
    Exit;
  end;
  WaitResult := WaitForSingleObject(ObserverMutex, ObserverExitTimeoutMs);
  if WaitResult = WaitAbandoned0 then
  begin
    ObserverMutexOwned := True;
    ChangeFailure := PostCommitFailure('Наблюдатель восстановления завершился неожиданно; файлы не будут заменены.');
    Exit;
  end;
  if WaitResult <> WaitObject0 then
  begin
    ChangeFailure := PostCommitFailure('Наблюдатель восстановления не остановился за 15 секунд; файлы не будут заменены.');
    Exit;
  end;
  ObserverMutexOwned := True;
  Result := True;
end;

procedure AbortChangePreparation();
begin
  if UpdateRequestSent and not UpdateResponseRefused and not UpdateCommitSent
     and (UpdateCancelEvent <> 0) then
  begin
    SetEvent(UpdateCancelEvent);
  end;
  if RecoveryDisableSetBySetup and (DisableEvent <> 0) and not UpdateCommitSent then
    ResetEvent(DisableEvent);
  RecoveryDisableSetBySetup := False;
end;

procedure ReleaseRuntimeLocks();
begin
  if ObserverMutex <> 0 then
  begin
    if ObserverMutexOwned then
      ReleaseMutex(ObserverMutex);
    CloseHandle(ObserverMutex);
    ObserverMutex := 0;
    ObserverMutexOwned := False;
  end;
  if AppMutex <> 0 then
  begin
    if AppMutexOwned then
      ReleaseMutex(AppMutex);
    CloseHandle(AppMutex);
    AppMutex := 0;
    AppMutexOwned := False;
  end;
  if DisableEvent <> 0 then
  begin
    CloseHandle(DisableEvent);
    DisableEvent := 0;
  end;
  if UpdateRequestEvent <> 0 then
  begin
    CloseHandle(UpdateRequestEvent);
    UpdateRequestEvent := 0;
  end;
  if UpdateReadyEvent <> 0 then
  begin
    CloseHandle(UpdateReadyEvent);
    UpdateReadyEvent := 0;
  end;
  if UpdateRefusedEvent <> 0 then
  begin
    CloseHandle(UpdateRefusedEvent);
    UpdateRefusedEvent := 0;
  end;
  if UpdateCommitEvent <> 0 then
  begin
    CloseHandle(UpdateCommitEvent);
    UpdateCommitEvent := 0;
  end;
  if UpdateCancelEvent <> 0 then
  begin
    CloseHandle(UpdateCancelEvent);
    UpdateCancelEvent := 0;
  end;
end;

procedure ReleaseSetupMutex();
begin
  if SetupMutex <> 0 then
  begin
    if SetupMutexOwned then
      ReleaseMutex(SetupMutex);
    CloseHandle(SetupMutex);
    SetupMutex := 0;
    SetupMutexOwned := False;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := AcquireSetupMutex();
  if not Result then
    MsgBox(ChangeFailure, mbError, MB_OK);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  InstalledVersion: String;
  RegisteredLocation: String;
  RegistrationError: String;
begin
  Result := '';
  NeedsRestart := False;
  if ChangeSucceeded then
    Exit;
  if UpdateCommitSent then
  begin
    Result := 'Предыдущий запрос уже разрешил штатный выход. Закройте установщик, дождитесь завершения приложения и повторите попытку.';
    Exit;
  end;
  UpdateRequestSent := False;
  UpdateResponseRefused := False;
  RecoveryDisableSetBySetup := False;
  ChangeFailure := '';
  ChangeSucceeded := False;
  if not RegKeyExists(HKCU, ProductAppIdKey) then
  begin
    if not AcquireFreshInstallLocks() then
    begin
      if ChangeFailure = '' then
        ChangeFailure := 'Не удалось проверить работающие процессы; файлы не изменены.';
      ReleaseRuntimeLocks();
      Result := ChangeFailure;
      Exit;
    end;
    ChangeSucceeded := True;
    Exit;
  end;
  if not RegisteredProduct(InstalledVersion, RegisteredLocation, RegistrationError) then
  begin
    Result := RegistrationError;
    Exit;
  end;
  if MsgBox('Обновление приостановит мониторинг на время замены файлов. Сохранённые заявки и позиции останутся без изменений; перед следующим запуском приложение выполнит сверку.'
    + #13#10 + #13#10 + 'Продолжить подготовку обновления?', mbConfirmation, MB_YESNO) <> IDYES then
  begin
    Result := 'Обновление отменено. Файлы приложения не изменены.';
    Exit;
  end;
  if not RequestCurrentAppShutdown(InstalledVersion, False) then
  begin
    if ChangeFailure = '' then
      ChangeFailure := 'Не удалось безопасно подготовить обновление; файлы не изменены.';
    AbortChangePreparation();
    ReleaseRuntimeLocks();
    Result := ChangeFailure;
    Exit;
  end;
  ChangeSucceeded := True;
end;

procedure DeinitializeSetup();
begin
  if not ChangeSucceeded then
    AbortChangePreparation();
  ReleaseRuntimeLocks();
  ReleaseSetupMutex();
end;

function InitializeUninstall(): Boolean;
var
  InstalledVersion: String;
  RegisteredLocation: String;
  RegistrationError: String;
begin
  Result := AcquireSetupMutex();
  if not Result then
  begin
    MsgBox(ChangeFailure, mbError, MB_OK);
    Exit;
  end;
  if not RegisteredProduct(InstalledVersion, RegisteredLocation, RegistrationError) then
  begin
    Result := False;
    MsgBox(RegistrationError, mbError, MB_OK);
    ReleaseSetupMutex();
    Exit;
  end;
  if not RequestCurrentAppShutdown(InstalledVersion, True) then
  begin
    Result := False;
    if ChangeFailure = '' then
      ChangeFailure := 'Не удалось безопасно подготовить удаление; файлы не изменены.';
    AbortChangePreparation();
    ReleaseRuntimeLocks();
    MsgBox(ChangeFailure, mbError, MB_OK);
    ReleaseSetupMutex();
    Exit;
  end;
  ChangeSucceeded := True;
  Result := True;
end;

procedure DeinitializeUninstall();
begin
  if not ChangeSucceeded then
    AbortChangePreparation();
  ReleaseRuntimeLocks();
  ReleaseSetupMutex();
end;
