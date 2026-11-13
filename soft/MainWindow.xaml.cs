using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Automation;
using System.Windows;
using System.Windows.Controls;
using TextBox = System.Windows.Controls.TextBox;
using TrueNorth.TradeFlow.Account;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Orders;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Risk;
using TrueNorth.TradeFlow.Trading;

namespace TrueNorth.TradeFlow;

public partial class MainWindow : Window
{
    private readonly SettingsStore _settingsStore;
    private readonly WakeProtectionController _wakeProtection;
    private readonly ITrueNorthSessionService _trueNorthSession;
    private readonly IAiAnalysisSource _aiAnalysisSource;
    private readonly AiTestReportStore _aiTestReportStore;
    private readonly RecoveryCoordinator? _recoveryCoordinator;
    private readonly NotificationDispatcher? _notificationDispatcher;
    private readonly TelegramCredentialStore? _telegramCredentialStore;
    private readonly Func<LocalOrderLifecycleExampleSession> _orderLifecycleExampleFactory;
    private readonly LocalTradingScenario? _localTradingScenario;
    private readonly string? _localScenarioUnavailableReason;
    private readonly SemaphoreSlim _profileCommandGate = new(1, 1);
    private readonly ObservableCollection<AssetDraft> _assetRows = [];
    private SettingsProfile _savedProfile;
    private SettingsFileStatus _fileStatus;
    private string? _loadMessage;
    private bool _canRecover;
    private readonly bool _fallbackReadOnly;
    private bool _updatingMode;
    private bool _ready;
    private bool _profileCommandBusy;
    private bool _allowClose;
    private bool _appliedPreventSleep;
    private TrueNorthSessionState _trueNorthSessionState = TrueNorthSessionState.Unknown;
    private string _trueNorthSessionMessage = "Вход откроется в отдельном окне Microsoft Edge. После ручного входа нажмите «Проверить сессию».";
    private long _lastTrueNorthSessionRevision;
    private long _trueNorthSessionAttempt;
    private bool _trueNorthSessionActionInProgress;
    private bool _trueNorthSessionCheckInProgress;
    private bool _applicationClosing;
    private CancellationTokenSource? _aiBatchCancellation;
    private AiTestBatchReport? _lastAiBatch;
    private string? _savedAiReportPath;
    private bool _aiBatchActive;
    private bool _aiBatchCompleted;
    private bool _aiReportSaveInProgress;
    private IReadOnlyList<AccountSnapshot>? _accountExampleSnapshots;
    private LocalOrderLifecycleExampleSession? _orderLifecycleExampleSession;
    private bool _accountExampleLoading;

    internal MainWindow(
        SettingsStore settingsStore,
        SettingsLoadResult loaded,
        WakeProtectionController wakeProtection,
        ITrueNorthSessionService? trueNorthSession = null,
        IAiAnalysisSource? aiAnalysisSource = null,
        AiTestReportStore? aiTestReportStore = null,
        RecoveryCoordinator? recoveryCoordinator = null,
        NotificationDispatcher? notificationDispatcher = null,
        TelegramCredentialStore? telegramCredentialStore = null,
        string? processRecoveryStatus = null,
        Func<LocalOrderLifecycleExampleSession>? orderLifecycleExampleFactory = null,
        LocalTradingScenario? localTradingScenario = null,
        string? localScenarioUnavailableReason = null)
    {
        InitializeComponent();
        _settingsStore = settingsStore;
        _savedProfile = loaded.Profile.Copy();
        _fileStatus = loaded.Status;
        _loadMessage = loaded.Message;
        _canRecover = loaded.CanRecover;
        _fallbackReadOnly = loaded.Status is SettingsFileStatus.UnsupportedVersion or SettingsFileStatus.Unavailable;
        _wakeProtection = wakeProtection;
        _trueNorthSession = trueNorthSession ?? new TrueNorthSessionService();
        _aiAnalysisSource = aiAnalysisSource ?? new SyntheticAiAnalysisSource();
        _aiTestReportStore = aiTestReportStore ?? new AiTestReportStore(AiTestReportStore.DefaultDirectory());
        _recoveryCoordinator = recoveryCoordinator;
        _notificationDispatcher = notificationDispatcher;
        _telegramCredentialStore = telegramCredentialStore;
        _localTradingScenario = localTradingScenario;
        _localScenarioUnavailableReason = localScenarioUnavailableReason;
        _orderLifecycleExampleFactory = orderLifecycleExampleFactory
            ?? (() => new LocalOrderLifecycleExampleSession());
        _wakeProtection.StateChanged += UpdatePowerStatusFromController;
        if (_localTradingScenario is not null)
            _localTradingScenario.StateChanged += UpdateLocalTradingStatusFromScenario;
        if (_recoveryCoordinator is not null)
            _recoveryCoordinator.StatusChanged += UpdateRecoveryStatusFromCoordinator;
        _appliedPreventSleep = _savedProfile.Settings.PreventSleep;
        SimulationContent.StartRequested += (_, _) => SimulationContent.Start(
            SimulationNotificationPreferences.Capture(_savedProfile.Settings));
        AccountDashboardPage.ShowExampleRequested += async (_, _) => await ShowAccountExampleAsync();
        AccountDashboardPage.ExitExampleRequested += (_, _) => ExitAccountExample();
        AccountDashboardPage.ContinueWithOwnerParametersRequested += async (_, _) =>
            await ApplyLocalOrderChoiceAsync(continuing: true);
        AccountDashboardPage.TransferToManualRequested += async (_, _) =>
            await ApplyLocalOrderChoiceAsync(continuing: false);
        AccountDashboardPage.HistoryRequested += (_, _) =>
        {
            NavigationList.SelectedIndex = 4;
            FocusAccountControl(AccountHistoryPage.BackButton);
        };
        AccountHistoryPage.ShowExampleRequested += async (_, _) => await ShowAccountExampleAsync();
        AccountHistoryPage.ExitExampleRequested += (_, _) => ExitAccountExample();
        AccountHistoryPage.BackToOverviewRequested += (_, _) =>
        {
            NavigationList.SelectedIndex = 0;
            FocusAccountControl(AccountDashboardPage.OpenHistoryButton);
        };

        AssetsList.ItemsSource = _assetRows;
        NavigationList.SelectedIndex = 0;
        VersionText.Text = $"Версия {GetType().Assembly.GetName().Version?.ToString(3)}";
        SetModeSelection(_savedProfile.SelectedMode);
        LoadDraft(_savedProfile.Settings);
        LoadAiTestDefaults(_savedProfile.Settings);
        _ready = true;

        UpdateMonitoringIntent();
        UpdateLocalTradingStatus();
        UpdateRecoveryStatus();
        UpdateProcessRecoveryStatus(processRecoveryStatus);
        UpdatePowerStatus();
        UpdateTrueNorthSessionCard();
        UpdateModeLabel();
        UpdatePage();
        ShowLoadStatus();
    }

    internal LocalOrderLifecycleExampleSession? OrderLifecycleExampleSession => _orderLifecycleExampleSession;

    internal void AllowApplicationClose()
    {
        _allowClose = true;
        DisposeOrderLifecycleExample();
    }

    internal void PrepareForApplicationClose()
    {
        _applicationClosing = true;
        DisposeOrderLifecycleExample();
        _aiBatchCancellation?.Cancel();
        _trueNorthSessionAttempt++;
        UpdateTrueNorthSessionCard();
    }

    internal void ApplyTrueNorthSessionResult(TrueNorthSessionResult result)
    {
        if (_applicationClosing || result.Revision < _lastTrueNorthSessionRevision)
            return;

        _lastTrueNorthSessionRevision = result.Revision;
        _trueNorthSessionState = result.State;
        _trueNorthSessionMessage = result.Message;
        UpdateTrueNorthSessionCard();
    }

    private async void OpenTrueNorth_Click(object sender, RoutedEventArgs e) =>
        await RunTrueNorthSessionActionAsync(checkSession: false);

    private async void CheckTrueNorthSession_Click(object sender, RoutedEventArgs e) =>
        await RunTrueNorthSessionActionAsync(checkSession: true);

    private async Task RunTrueNorthSessionActionAsync(bool checkSession)
    {
        if (_trueNorthSessionActionInProgress || _applicationClosing)
            return;

        _trueNorthSessionActionInProgress = true;
        _trueNorthSessionCheckInProgress = checkSession;
        var attempt = ++_trueNorthSessionAttempt;
        UpdateTrueNorthSessionCard();
        try
        {
            var result = checkSession
                ? await _trueNorthSession.CheckAsync()
                : await _trueNorthSession.OpenAsync();
            await DispatchToUiAsync(() =>
            {
                if (attempt == _trueNorthSessionAttempt && !_applicationClosing)
                    ApplyTrueNorthSessionResult(result);
            });
        }
        catch
        {
            await DispatchToUiAsync(() =>
            {
                if (attempt == _trueNorthSessionAttempt && !_applicationClosing)
                    ApplyTrueNorthSessionResult(TrueNorthSessionService.Unknown(
                        Math.Max(_lastTrueNorthSessionRevision + 1, attempt),
                        "Не удалось выполнить действие в Edge. Повторите попытку."));
            });
        }
        finally
        {
            await DispatchToUiAsync(() =>
            {
                if (attempt == _trueNorthSessionAttempt)
                {
                    _trueNorthSessionActionInProgress = false;
                    _trueNorthSessionCheckInProgress = false;
                    UpdateTrueNorthSessionCard();
                }
            });
        }
    }

    private Task DispatchToUiAsync(Action action)
    {
        if (Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return Task.CompletedTask;
        return Dispatcher.InvokeAsync(action, System.Windows.Threading.DispatcherPriority.Normal).Task;
    }

    private void UpdateTrueNorthSessionCard()
    {
        if (_trueNorthSessionActionInProgress)
        {
            TrueNorthSessionStateText.Text = _trueNorthSessionCheckInProgress
                ? "Проверяем сессию…"
                : "Открываем Edge…";
            TrueNorthSessionDetailsText.Text = "Статус обновится после загрузки TrueNorth в отдельном окне браузера.";
        }
        else
        {
            TrueNorthSessionStateText.Text = _trueNorthSessionState switch
            {
                TrueNorthSessionState.SignedOut => "Требуется войти в TrueNorth",
                TrueNorthSessionState.Authenticated => "Вход в TrueNorth подтверждён",
                _ => "Статус входа неизвестен"
            };
            TrueNorthSessionDetailsText.Text = _trueNorthSessionMessage;
        }

        var brushKey = _trueNorthSessionActionInProgress || _trueNorthSessionState == TrueNorthSessionState.Unknown
            ? "QuietBrush"
            : _trueNorthSessionState == TrueNorthSessionState.SignedOut ? "ErrorBrush" : "AccentBrush";
        TrueNorthSessionIndicator.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, brushKey);
        TrueNorthSessionStateText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        TrueNorthSessionCard.SetResourceReference(Border.BorderBrushProperty,
            _trueNorthSessionActionInProgress || _trueNorthSessionState == TrueNorthSessionState.Unknown
                ? "BorderBrush"
                : brushKey);

        var canOpen = _trueNorthSessionState != TrueNorthSessionState.Authenticated;
        OpenTrueNorthButton.Visibility = canOpen ? Visibility.Visible : Visibility.Collapsed;
        OpenTrueNorthButton.Content = _trueNorthSessionState == TrueNorthSessionState.SignedOut ? "Войти снова" : "Войти";
        AutomationProperties.SetName(OpenTrueNorthButton,
            _trueNorthSessionState == TrueNorthSessionState.SignedOut
                ? "Войти снова в TrueNorth в отдельном окне Microsoft Edge"
                : "Войти в TrueNorth в отдельном окне Microsoft Edge");
        CheckTrueNorthSessionButton.Content = _trueNorthSessionActionInProgress
            ? "Проверяем…"
            : "Проверить сессию";
        CheckTrueNorthSessionButton.Style = (Style)FindResource(
            _trueNorthSessionState == TrueNorthSessionState.Authenticated ? "PrimaryAction" : "SecondaryAction");
        OpenTrueNorthButton.Style = (Style)FindResource(
            _trueNorthSessionState == TrueNorthSessionState.Authenticated ? "SecondaryAction" : "PrimaryAction");
        OpenTrueNorthButton.IsEnabled = !_trueNorthSessionActionInProgress && !_applicationClosing;
        CheckTrueNorthSessionButton.IsEnabled = !_trueNorthSessionActionInProgress && !_applicationClosing;
    }

    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePage();

    private async Task ShowAccountExampleAsync()
    {
        if (_accountExampleLoading || _orderLifecycleExampleSession is not null)
            return;
        _accountExampleLoading = true;
        _accountExampleSnapshots = AccountExample.Create();
        AccountDashboardPage.SetSnapshots(_accountExampleSnapshots);
        AccountDashboardPage.SetEntryEligibilityResult(EntryEligibilityExample.Evaluate());
        AccountHistoryPage.SetSnapshots(_accountExampleSnapshots);
        UpdateAccountTestDataBadge();
        var exampleSession = _orderLifecycleExampleFactory();
        _orderLifecycleExampleSession = exampleSession;
        try
        {
            var result = await exampleSession.InitializeAsync();
            if (ReferenceEquals(_orderLifecycleExampleSession, exampleSession))
                AccountDashboardPage.SetOrderLifecycleExample(exampleSession.CreatePresentation(result));
        }
        catch (Exception)
        {
            if (ReferenceEquals(_orderLifecycleExampleSession, exampleSession))
                AccountDashboardPage.SetOrderLifecycleExample(exampleSession.CreatePresentation(
                    new OrderLifecycleResult(false, "Локальное намерение не удалось загрузить из примера.")));
        }
        finally
        {
            _accountExampleLoading = false;
        }
        FocusAccountControl(NavigationList.SelectedIndex == 4
            ? AccountHistoryPage.BackButton
            : AccountDashboardPage.OpenHistoryButton);
    }

    private void ExitAccountExample()
    {
        DisposeOrderLifecycleExample();
        _accountExampleSnapshots = null;
        AccountDashboardPage.SetOrderLifecycleExample(null);
        AccountDashboardPage.SetEntryEligibilityResult(null);
        AccountDashboardPage.SetSnapshots(null);
        AccountHistoryPage.SetSnapshots(null);
        UpdateAccountTestDataBadge();
        FocusAccountControl(NavigationList.SelectedIndex == 4
            ? AccountHistoryPage.ExampleButton
            : AccountDashboardPage.ExampleButton);
    }

    private async Task ApplyLocalOrderChoiceAsync(bool continuing)
    {
        var exampleSession = _orderLifecycleExampleSession;
        if (exampleSession is null)
            return;
        AccountDashboardPage.ContinueOwnerParametersButton.IsEnabled = false;
        AccountDashboardPage.TransferToManualButton.IsEnabled = false;
        var result = continuing
            ? await exampleSession.ContinueWithOwnerParametersAsync()
            : await exampleSession.TransferToManualAsync();
        if (ReferenceEquals(_orderLifecycleExampleSession, exampleSession))
        {
            AccountDashboardPage.SetOrderLifecycleExample(exampleSession.CreatePresentation(result));
            AccountDashboardPage.OrderLifecyclePanelElement.BringIntoView();
        }
    }

    private void DisposeOrderLifecycleExample()
    {
        var exampleSession = _orderLifecycleExampleSession;
        _orderLifecycleExampleSession = null;
        exampleSession?.Dispose();
    }

    private void FocusAccountControl(System.Windows.Controls.Control control)
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
        {
            if (!control.IsVisible || !control.IsEnabled)
                return;

            control.BringIntoView();
            UpdateLayout();
            System.Windows.Input.Keyboard.Focus(control);
        }));
    }

    private async void Mode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updatingMode)
            return;

        var requested = SelectedMode;
        if (requested == _savedProfile.SelectedMode)
            return;

        await _profileCommandGate.WaitAsync();
        _profileCommandBusy = true;
        UpdateLocalTradingStatus();
        try
        {
            if (requested == _savedProfile.SelectedMode)
            {
                SetModeSelection(_savedProfile.SelectedMode);
                return;
            }

            if (_fallbackReadOnly)
            {
                SetModeSelection(_savedProfile.SelectedMode);
                UpdateModeLabel();
                var message = ReadOnlyFallbackMessage();
                SetOverviewModeStatus(message);
                SetStatus(message, warning: true);
                return;
            }

            var updated = _savedProfile.Copy();
            updated.SelectedMode = requested;
            if (requested == "Test")
                updated.SavedOperatingIntent = false;

            TradingLoopOutcome? scenarioResult = null;
            if (_localTradingScenario is not null)
            {
                scenarioResult = await _localTradingScenario.UpdateProfileAsync(updated);
                if (!scenarioResult.Succeeded)
                {
                    SetModeSelection(_savedProfile.SelectedMode);
                    UpdateModeLabel();
                    UpdateMonitoringIntent();
                    UpdateLocalTradingStatus();
                    var message = "Режим приложения не изменён: локальный профиль не подтвердил сохранение режима и Stop. "
                        + scenarioResult.Status;
                    SetOverviewModeStatus(message);
                    SetStatus(message, warning: true);
                    return;
                }
            }

            var result = _settingsStore.Save(updated);
            if (!result.Succeeded)
            {
                SetModeSelection(_savedProfile.SelectedMode);
                UpdateModeLabel();
                UpdateMonitoringIntent();
                UpdateLocalTradingStatus();
                var message = _localTradingScenario is null
                    ? result.Message ?? "Не удалось сохранить выбранный режим."
                    : "Режим приложения не сохранён. Локальный профиль обновлён отдельно и остаётся безопасно остановленным. "
                        + (result.Message ?? result.Status.ToString());
                SetOverviewModeStatus(message);
                SetStatus(message, warning: true);
                return;
            }

            _savedProfile = result.Profile!;
            _notificationDispatcher?.UpdateSettings(_savedProfile.Settings);
            _recoveryCoordinator?.UpdateSettings(_savedProfile.Settings);
            _fileStatus = SettingsFileStatus.Loaded;
            _loadMessage = null;
            _canRecover = false;
            UpdateModeLabel();
            SetOverviewModeStatus(null);
            ShowRecoveryButton(false);
            UpdateMonitoringIntent();
            UpdateLocalTradingStatus();
            SetStatus(scenarioResult is { Succeeded: false }
                ? "Режим сохранён. Локальный цикл остановлен, но часть заявок требует сверки: " + scenarioResult.Status
                : "Режим сохранён. Несохранённые поля формы оставлены без изменений.",
                warning: scenarioResult is { Succeeded: false });
        }
        finally
        {
            _profileCommandBusy = false;
            _profileCommandGate.Release();
            UpdateLocalTradingStatus();
        }
    }

    private void UpdatePage()
    {
        var selected = NavigationList.SelectedIndex;
        var simulation = selected == 2;
        OverviewPage.Visibility = selected == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = selected == 1 ? Visibility.Visible : Visibility.Collapsed;
        SimulationContent.Visibility = simulation ? Visibility.Visible : Visibility.Collapsed;
        AiTestPage.Visibility = selected == 3 ? Visibility.Visible : Visibility.Collapsed;
        AccountHistoryPage.Visibility = selected == 4 ? Visibility.Visible : Visibility.Collapsed;
        if (selected == 4)
            MainScrollViewer.ScrollToVerticalOffset(0);
        OperatingStatusBadge.Visibility = simulation ? Visibility.Collapsed : Visibility.Visible;
        SimulationStatusBadge.Visibility = simulation ? Visibility.Visible : Visibility.Collapsed;
        UpdateAccountTestDataBadge();
    }

    private void UpdateAccountTestDataBadge() =>
        AccountTestDataBadge.Visibility = _accountExampleSnapshots is not null
            && NavigationList.SelectedIndex is 0 or 4
                ? Visibility.Visible
                : Visibility.Collapsed;

    private void LoadAiTestDefaults(AppSettings settings)
    {
        var savedAssets = (settings.Assets ?? []).Where(asset => !string.IsNullOrWhiteSpace(asset)).ToArray();
        AiTestAssetsTextBox.Text = savedAssets.Length == 0 ? "BTC, ETH, SOL" : string.Join(", ", savedAssets);
        AiTestEntryTypeComboBox.SelectedIndex = settings.EntryType switch
        {
            "Market" => 0,
            "Limit" => 1,
            _ => 1
        };
        AiTestDefaultsText.Text = savedAssets.Length == 0 && settings.EntryType is not ("Market" or "Limit")
            ? "Примеры BTC, ETH, SOL и Limit; настройки не меняются."
            : "Значения из настроек; правки действуют только для этого пакета.";
    }

    private void AiTestInput_TextChanged(object sender, TextChangedEventArgs e) => AiTestInputErrorText.Text = string.Empty;

    private async void StartAiTest_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBatchActive || _aiReportSaveInProgress || _applicationClosing)
            return;

        var entryType = (AiTestEntryTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!AiAnalysisRequestFactory.TryCreateBatch(AiTestAssetsTextBox.Text, entryType,
                out var requests, out var error))
        {
            AiTestInputErrorText.Text = error;
            AiTestStatusText.Text = "Пакет не запущен.";
            return;
        }

        AiTestInputErrorText.Text = string.Empty;
        _lastAiBatch = null;
        _savedAiReportPath = null;
        _aiBatchActive = true;
        _aiBatchCompleted = false;
        _aiBatchCancellation = new CancellationTokenSource();
        AiTestProgressBar.Value = 0;
        AiTestSummaryText.Text = string.Empty;
        AiTestPathText.Text = string.Empty;
        RetryAiReportButton.Visibility = Visibility.Collapsed;
        OpenAiReportButton.IsEnabled = false;
        StartAiTestButton.IsEnabled = false;
        CancelAiTestButton.IsEnabled = true;
        AiTestStatusText.Text = $"Запущено запросов: 0/{requests.Count}. Используется только синтетический источник.";

        var progress = new Progress<AiTestProgress>(DispatchAiTestProgress);
        try
        {
            var report = await AiTestBatchRunner.RunAsync(requests, _aiAnalysisSource, progress,
                _aiBatchCancellation.Token).ConfigureAwait(false);
            Task? saveTask = null;
            await DispatchToUiAsync(() =>
            {
                _aiBatchCompleted = true;
                AiTestProgressBar.Value = report.ProgressFraction;
                _lastAiBatch = report;
                AiTestSummaryText.Text = FormatAiTestSummary(report);
                AiTestStatusText.Text = report.WasCancelled
                    ? "Пакет отменён; запущенные и не запущенные элементы учтены."
                    : "Синтетический пакет завершён.";
                saveTask = SaveAiTestReportAsync();
            }).ConfigureAwait(false);
            if (saveTask is not null)
                await saveTask.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await DispatchToUiAsync(() =>
                AiTestStatusText.Text = $"Не удалось завершить синтетический пакет: {exception.GetType().Name}.")
                .ConfigureAwait(false);
        }
        finally
        {
            await DispatchToUiAsync(() =>
            {
                _aiBatchActive = false;
                _aiBatchCancellation?.Dispose();
                _aiBatchCancellation = null;
                StartAiTestButton.IsEnabled = !_applicationClosing;
                CancelAiTestButton.IsEnabled = false;
            }).ConfigureAwait(false);
        }
    }

    private void DispatchAiTestProgress(AiTestProgress progress)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        try
        {
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => UpdateAiTestProgress(progress)));
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void UpdateAiTestProgress(AiTestProgress progress)
    {
        if (_applicationClosing || !_aiBatchActive || _aiBatchCompleted)
            return;
        AiTestProgressBar.Value = (double)progress.CompletedRequests / progress.TotalRequests;
        AiTestStatusText.Text = progress.LastOutcome is null
            ? $"Обработано {progress.CompletedRequests}/{progress.TotalRequests}."
            : $"Обработано {progress.CompletedRequests}/{progress.TotalRequests}. Последний: {progress.CurrentAsset}, {AiTestReportFormatter.OutcomeLabel(progress.LastOutcome.Value)}.";
    }

    private static string FormatAiTestSummary(AiTestBatchReport report) =>
        $"Всего: {report.Results.Count}. SETUP полностью по формату и данным: {report.Count(AiTestOutcome.CompleteSetup)}; "
        + $"NO_SETUP: {report.Count(AiTestOutcome.ValidNoSetup)}; неполные/неверные: {report.Count(AiTestOutcome.IncompleteOrInvalid)}; "
        + $"неубедительные: {report.Count(AiTestOutcome.Inconclusive)}; ошибки: {report.Count(AiTestOutcome.RequestError)}; "
        + $"тайм-ауты: {report.Count(AiTestOutcome.Timeout)}; отменены: {report.Count(AiTestOutcome.Cancelled)}; "
        + $"не запускались: {report.Count(AiTestOutcome.NotStarted)}. Формат не подтверждает торговую пригодность.";

    private void CancelAiTest_Click(object sender, RoutedEventArgs e)
    {
        if (_aiBatchActive)
        {
            AiTestStatusText.Text = "Запрошена отмена пакета…";
            _aiBatchCancellation?.Cancel();
        }
    }

    private async void RetryAiReport_Click(object sender, RoutedEventArgs e)
    {
        if (_lastAiBatch is null || _aiBatchActive || _aiReportSaveInProgress)
            return;
        await SaveAiTestReportAsync();
    }

    private async Task SaveAiTestReportAsync()
    {
        if (_lastAiBatch is null || _aiReportSaveInProgress)
            return;

        var report = _lastAiBatch;
        _aiReportSaveInProgress = true;
        StartAiTestButton.IsEnabled = false;
        RetryAiReportButton.IsEnabled = false;
        string? savedPath = null;
        Exception? saveError = null;
        try
        {
            savedPath = await _aiTestReportStore.SaveAsync(report).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            saveError = exception;
        }

        await DispatchToUiAsync(() =>
        {
            if (saveError is null)
            {
                _savedAiReportPath = savedPath;
                AiTestPathText.Text = $"Отчёт сохранён: {_savedAiReportPath}";
                RetryAiReportButton.Visibility = Visibility.Collapsed;
                OpenAiReportButton.IsEnabled = true;
            }
            else
            {
                _savedAiReportPath = null;
                AiTestPathText.Text = $"Не удалось сохранить отчёт ({saveError.Message}). Результаты сохранены в памяти; можно повторить запись.";
                RetryAiReportButton.Visibility = Visibility.Visible;
                RetryAiReportButton.IsEnabled = true;
                OpenAiReportButton.IsEnabled = false;
            }
            _aiReportSaveInProgress = false;
            RetryAiReportButton.IsEnabled = _lastAiBatch is not null;
            StartAiTestButton.IsEnabled = !_applicationClosing && !_aiBatchActive;
        }).ConfigureAwait(false);
    }

    private void OpenAiReport_Click(object sender, RoutedEventArgs e)
    {
        if (_savedAiReportPath is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_savedAiReportPath) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            AiTestPathText.Text = $"Не удалось открыть отчёт ({exception.Message}). Его путь: {_savedAiReportPath}";
        }
    }

    private string SelectedMode => (ModeList.SelectedItem as ListBoxItem)?.Tag as string ?? "Test";

    private void UpdateModeLabel()
    {
        CurrentModeText.Text = _savedProfile.SelectedMode;
    }

    private void UpdateMonitoringIntent()
    {
        if (_localTradingScenario is null)
        {
            OperatingIntentText.Text = _localScenarioUnavailableReason is not null
                ? "Локальное намерение неизвестно: отдельный профиль не открылся."
                : _savedProfile.SavedOperatingIntent
                    ? "Сохранённое намерение: запустить. Фактический мониторинг не запущен."
                    : "Сохранённое намерение: остановить. Фактический мониторинг не запущен.";
            return;
        }
        var state = _localTradingScenario.Snapshot();
        OperatingIntentText.Text = "Сохранённое локальное намерение: "
            + (state.SavedOperatingIntent ? "Start." : "Stop.");
    }

    private void RealStart_Click(object sender, RoutedEventArgs e)
    {
        LocalScenarioActionStatusText.Text = "Реальный Start недоступен: чтение счёта, AI-провайдер и проверенные маршруты не подключены.";
    }

    private async void LocalScenarioStart_Click(object sender, RoutedEventArgs e)
    {
        await _profileCommandGate.WaitAsync();
        _profileCommandBusy = true;
        UpdateLocalTradingStatus();
        try
        {
            if (_localTradingScenario is null)
            {
                LocalScenarioActionStatusText.Text = _localScenarioUnavailableReason
                    ?? "Локальный сценарий не подключён в этой конфигурации окна.";
                return;
            }
            if (_savedProfile.SelectedMode != "TrueNorth AI setups"
                || _localTradingScenario.Snapshot().SelectedMode != "TrueNorth AI setups")
            {
                LocalScenarioActionStatusText.Text = "Для локального Start выберите режим TrueNorth AI setups и сохраните его в обоих профилях.";
                return;
            }
            if (_fallbackReadOnly || _savedProfile.IsIncomplete())
            {
                LocalScenarioActionStatusText.Text = _fallbackReadOnly
                    ? ReadOnlyFallbackMessage()
                    : "Сначала сохраните активы, тип входа, размер и плечо в настройках.";
                return;
            }

            LocalScenarioStartButton.IsEnabled = false;
            try
            {
                var result = await _localTradingScenario.StartAsync(_savedProfile);
                if (!result.Succeeded)
                {
                    LocalScenarioActionStatusText.Text = result.Status;
                    return;
                }

                UpdateMonitoringIntent();
                LocalScenarioActionStatusText.Text = result.Status;
            }
            catch (Exception)
            {
                LocalScenarioActionStatusText.Text = "Локальный Start не завершён; проверьте сохранённое состояние перед повтором.";
            }
        }
        finally
        {
            _profileCommandBusy = false;
            _profileCommandGate.Release();
            UpdateLocalTradingStatus();
        }
    }

    private async void LocalScenarioStop_Click(object sender, RoutedEventArgs e)
    {
        await _profileCommandGate.WaitAsync();
        _profileCommandBusy = true;
        UpdateLocalTradingStatus();
        LocalScenarioStopButton.IsEnabled = false;
        try
        {
            if (_localTradingScenario is null)
            {
                LocalScenarioActionStatusText.Text = _localScenarioUnavailableReason
                    ?? "Локальный сценарий не подключён в этой конфигурации окна.";
                return;
            }
            var stopped = await _localTradingScenario.StopAsync();
            UpdateMonitoringIntent();
            LocalScenarioActionStatusText.Text = stopped.Status;
        }
        catch (Exception)
        {
            LocalScenarioActionStatusText.Text = "Локальный Stop не завершил сверку; допуск остаётся закрытым.";
        }
        finally
        {
            _profileCommandBusy = false;
            _profileCommandGate.Release();
            UpdateLocalTradingStatus();
        }
    }

    private async void LocalScenarioClosePosition_Click(object sender, RoutedEventArgs e)
    {
        if (_localTradingScenario is null)
            return;
        try
        {
            if (!await _localTradingScenario.CloseNextPositionAtTakeProfitAsync())
            {
                LocalScenarioActionStatusText.Text = "Нет защищённой локальной позиции для синтетического TP.";
                return;
            }
            var result = await _localTradingScenario.RunOnceAsync();
            LocalScenarioActionStatusText.Text = result.Status;
        }
        catch (Exception)
        {
            LocalScenarioActionStatusText.Text = "Синтетическое закрытие не подтверждено; локальный журнал требует сверки.";
        }
        finally
        {
            UpdateLocalTradingStatus();
        }
    }

    private void UpdateLocalTradingStatusFromScenario()
    {
        if (_applicationClosing || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(UpdateLocalTradingStatusFromScenario));
            return;
        }
        UpdateLocalTradingStatus();
    }

    private void UpdateLocalTradingStatus()
    {
        if (_localTradingScenario is null)
        {
            LocalScenarioCard.Visibility = _localScenarioUnavailableReason is null
                ? Visibility.Collapsed : Visibility.Visible;
            LocalScenarioActivityText.Text = "Локальный цикл не подключён.";
            LocalScenarioStatusText.Text = string.Empty;
            LocalScenarioStatusText.Visibility = Visibility.Collapsed;
            LocalScenarioPauseText.Text = _localScenarioUnavailableReason
                ?? "Локальный сценарий недоступен в этой конфигурации окна.";
            LocalScenarioProfileReviewText.Text = "Отдельный сохранённый профиль не загружен.";
            LocalScenarioActionStatusText.Text = string.Empty;
            LocalScenarioStartButton.IsEnabled = false;
            LocalScenarioStopButton.IsEnabled = false;
            LocalScenarioClosePositionButton.Visibility = Visibility.Collapsed;
            OperatingStatusText.Text = _localScenarioUnavailableReason is null
                ? "ТОРГОВЫЕ ФУНКЦИИ НЕ ЗАПУЩЕНЫ"
                : "ЛОКАЛЬНЫЙ СЦЕНАРИЙ ОТКЛЮЧЁН";
            _wakeProtection.SetActive(false);
            return;
        }

        LocalScenarioCard.Visibility = Visibility.Visible;
        var state = _localTradingScenario.Snapshot();
        LocalScenarioActivityText.Text = state.Activity;
        LocalScenarioStatusText.Text = state.LastStatus;
        LocalScenarioStatusText.Visibility = string.IsNullOrWhiteSpace(state.LastStatus)
            ? Visibility.Collapsed : Visibility.Visible;
        LocalScenarioPauseText.Text = state.PauseReason;
        LocalScenarioProfileReviewText.Text = state.ProfileReview;
        OperatingStatusText.Text = state.IsSearching ? "ЛОКАЛЬНЫЙ ПОИСК"
            : state.HasOwnedMonitoring ? "СОПРОВОЖДЕНИЕ ПОЗИЦИЙ"
            : state.SavedOperatingIntent && state.IsAdmitting ? "ОЖИДАНИЕ СРОКА ПОИСКА"
            : state.SavedOperatingIntent ? "ЛОКАЛЬНЫЙ ЦИКЛ ПРИОСТАНОВЛЕН"
            : "ЛОКАЛЬНЫЙ ЦИКЛ ОСТАНОВЛЕН";
        LocalScenarioStartButton.IsEnabled = !_profileCommandBusy && _localTradingScenario.IsReady
            && _savedProfile.SelectedMode == "TrueNorth AI setups"
            && state.SelectedMode == "TrueNorth AI setups"
            && !_savedProfile.IsIncomplete() && !state.SavedOperatingIntent && !_fallbackReadOnly;
        LocalScenarioStopButton.IsEnabled = !_profileCommandBusy && state.SavedOperatingIntent;
        LocalScenarioClosePositionButton.Visibility = state.OwnedPositionCount > 0
            ? Visibility.Visible : Visibility.Collapsed;
        LocalScenarioClosePositionButton.IsEnabled = state.OwnedPositionCount > 0;
        _wakeProtection.SetActive(state.IsSearching || state.HasOwnedMonitoring);
    }

    private void UpdateRecoveryStatus()
    {
        var pending = _recoveryCoordinator?.HasPendingRecovery == true;
        RecoveryStatusText.Text = pending ? _recoveryCoordinator!.StatusText : string.Empty;
        RecoveryStatusText.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRecoveryStatusFromCoordinator()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (Dispatcher.CheckAccess())
            UpdateRecoveryStatus();
        else
            Dispatcher.BeginInvoke(new Action(UpdateRecoveryStatus));
    }

    internal void UpdateProcessRecoveryStatus(string? message)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => UpdateProcessRecoveryStatus(message)));
            return;
        }

        ProcessRecoveryStatusText.Text = message ?? string.Empty;
        ProcessRecoveryStatusText.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void LoadDraft(AppSettings settings)
    {
        _assetRows.Clear();
        foreach (var asset in settings.Assets ?? [])
            _assetRows.Add(new AssetDraft(asset ?? string.Empty, _assetRows.Count + 1));

        EntryTypeComboBox.SelectedIndex = settings.EntryType switch
        {
            "Market" => 1,
            "Limit" => 2,
            _ => 0
        };
        CandidateSelectionMethodComboBox.SelectedIndex = settings.CandidateSelectionMethod switch
        {
            "NetProfitAtTakeProfit" => 1,
            _ => 0
        };
        MarginPercentTextBox.Text = FormatDecimal(settings.MarginBalancePercent);
        LeverageTextBox.Text = FormatDecimal(settings.Leverage);
        PositionCheckMinutesTextBox.Text = settings.PositionCheckMinutes.ToString(CultureInfo.CurrentCulture);
        LimitOrderCheckMinutesTextBox.Text = settings.LimitOrderCheckMinutes.ToString(CultureInfo.CurrentCulture);
        PartialFillCancelMinutesTextBox.Text = settings.PartialFillCancelMinutes.ToString(CultureInfo.CurrentCulture);
        NewSetupRetryMinutesTextBox.Text = settings.NewSetupRetryMinutes.ToString(CultureInfo.CurrentCulture);
        LimitReassessmentMinutesTextBox.Text = settings.LimitReassessmentMinutes.ToString(CultureInfo.CurrentCulture);
        ReadRetryCountTextBox.Text = settings.ReadRetryCount.ToString(CultureInfo.CurrentCulture);
        ReadRetryDelaySecondsTextBox.Text = settings.ReadRetryDelaySeconds.ToString(CultureInfo.CurrentCulture);
        RecoveryCheckSecondsTextBox.Text = settings.RecoveryCheckSeconds.ToString(CultureInfo.CurrentCulture);
        EmergencyCloseRetryCountTextBox.Text = settings.EmergencyCloseRetryCount.ToString(CultureInfo.CurrentCulture);
        EmergencyCloseRetryDelaySecondsTextBox.Text = settings.EmergencyCloseRetryDelaySeconds.ToString(CultureInfo.CurrentCulture);
        EmergencyCloseAfterNoticeSecondsTextBox.Text = settings.EmergencyCloseAfterNoticeSeconds.ToString(CultureInfo.CurrentCulture);
        NotificationsEnabledCheckBox.IsChecked = settings.NotificationsEnabled;
        OpeningNotificationsCheckBox.IsChecked = settings.OpeningNotifications;
        ClosingNotificationsCheckBox.IsChecked = settings.ClosingNotifications;
        OrderChangeNotificationsCheckBox.IsChecked = settings.OrderChangeNotifications;
        ErrorNotificationsCheckBox.IsChecked = settings.ErrorNotifications;
        RecoveryNotificationsCheckBox.IsChecked = settings.RecoveryNotifications;
        FailureRemindersEnabledCheckBox.IsChecked = settings.FailureRemindersEnabled;
        FailureReminderMinutesTextBox.Text = settings.FailureReminderMinutes.ToString(CultureInfo.CurrentCulture);
        TelegramEnabledCheckBox.IsChecked = _telegramCredentialStore?.IsEnabled == true;
        TelegramTokenPasswordBox.Clear();
        TelegramChatIdTextBox.Clear();
        NotificationTestStatusText.Text = string.Empty;
        TelegramSavedStatusText.Text = _telegramCredentialStore is null
            ? "Сохранённая пара здесь не отображается."
            : _telegramCredentialStore.HasCredentials
                ? _telegramCredentialStore.IsEnabled
                    ? "Пара Telegram сохранена и включена; её значения скрыты."
                    : "Пара Telegram сохранена, но отправка отключена."
                : "Пара Telegram не сохранена.";
        PreventSleepCheckBox.IsChecked = settings.PreventSleep;
        ClearFieldErrors();
    }

    private static string FormatDecimal(decimal? value) => value?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    private void AddAsset_Click(object sender, RoutedEventArgs e)
    {
        var row = new AssetDraft(string.Empty, _assetRows.Count + 1);
        _assetRows.Add(row);
        AssetsList.SelectedItem = row;
        AssetsList.ScrollIntoView(row);
        AssetsList.UpdateLayout();
        if (AssetsList.ItemContainerGenerator.ContainerFromItem(row) is ListBoxItem item
            && FindVisualChild<TextBox>(item) is { } textBox)
            textBox.Focus();
    }

    private void MoveAssetUp_Click(object sender, RoutedEventArgs e) => MoveSelectedAsset(-1);
    private void MoveAssetDown_Click(object sender, RoutedEventArgs e) => MoveSelectedAsset(1);

    private void MoveSelectedAsset(int offset)
    {
        var index = AssetsList.SelectedIndex;
        var destination = index + offset;
        if (index < 0 || destination < 0 || destination >= _assetRows.Count)
            return;

        _assetRows.Move(index, destination);
        RefreshAssetPositions();
        AssetsList.SelectedIndex = destination;
    }

    private void RemoveAsset_Click(object sender, RoutedEventArgs e)
    {
        if (AssetsList.SelectedItem is not AssetDraft selected)
            return;

        var index = AssetsList.SelectedIndex;
        _assetRows.Remove(selected);
        RefreshAssetPositions();
        if (_assetRows.Count > 0)
            AssetsList.SelectedIndex = Math.Min(index, _assetRows.Count - 1);
    }

    private void RefreshAssetPositions()
    {
        for (var index = 0; index < _assetRows.Count; index++)
            _assetRows[index].Position = index + 1;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        await _profileCommandGate.WaitAsync();
        _profileCommandBusy = true;
        UpdateLocalTradingStatus();
        try
        {
            await SaveSettingsCoreAsync(sender, e);
        }
        finally
        {
            _profileCommandBusy = false;
            _profileCommandGate.Release();
            UpdateLocalTradingStatus();
        }
    }

    private async Task SaveSettingsCoreAsync(object sender, RoutedEventArgs e)
    {
        if (_fallbackReadOnly)
        {
            SetStatus(ReadOnlyFallbackMessage(), warning: true);
            return;
        }

        var profile = CollectDraft(out var parseErrors);
        var validation = SettingsValidator.Validate(profile);
        var errors = new Dictionary<string, string>(validation.Errors, StringComparer.Ordinal);
        foreach (var (field, message) in parseErrors)
            errors[field] = message;

        ShowFieldErrors(errors);
        if (errors.Count != 0)
        {
            SetStatus("Исправьте отмеченные значения. Пустые торговые поля можно заполнить позже.", warning: true);
            return;
        }

        var token = TelegramTokenPasswordBox.Password.Trim();
        var chatId = TelegramChatIdTextBox.Text.Trim();
        var telegramEnabled = TelegramEnabledCheckBox.IsChecked == true;
        var credentialsSaved = false;
        if (token.Length > 0 || chatId.Length > 0)
        {
            if (token.Length == 0 || chatId.Length == 0 || _telegramCredentialStore is null
                || !_telegramCredentialStore.SavePair(token, chatId, telegramEnabled))
            {
                SetStatus("Пара Telegram не сохранена. Проверьте токен и ID чата. Если сохранённая пара уже была, она не изменена.", warning: true);
                return;
            }
            credentialsSaved = true;
            TelegramTokenPasswordBox.Clear();
            TelegramChatIdTextBox.Clear();
            TelegramSavedStatusText.Text = telegramEnabled
                ? "Пара Telegram сохранена и включена; её значения скрыты."
                : "Пара Telegram сохранена, но отправка отключена.";
        }
        else if (_telegramCredentialStore is not null
                 && !telegramEnabled && !_telegramCredentialStore.SetEnabled(false))
        {
            TelegramSavedStatusText.Text = "Telegram отключён в этой сессии, но настройку отключения не удалось сохранить.";
            SetStatus("Не удалось сохранить отключение Telegram. Новые отправки в этой сессии остановлены.", warning: true);
            return;
        }
        else if (_telegramCredentialStore is not null
                 && telegramEnabled && !_telegramCredentialStore.SetEnabled(true))
        {
            SetStatus("Для Telegram сначала сохраните новую пару токен и ID чата или отключите Telegram.", warning: true);
            return;
        }
        else if (_telegramCredentialStore is null && telegramEnabled)
        {
            SetStatus("Защищённая конфигурация Telegram недоступна. Сначала задайте пару токен и ID чата.", warning: true);
            return;
        }

        var result = _settingsStore.Save(validation.Profile, replaceCorruptFile: true);
        if (!result.Succeeded)
        {
            SetStatus(credentialsSaved
                ? "Пара Telegram сохранена отдельно, но общие настройки не сохранены. " + (result.Message ?? string.Empty)
                : result.Message ?? "Не удалось сохранить настройки.", warning: true);
            return;
        }

        _savedProfile = result.Profile!;
        _notificationDispatcher?.UpdateSettings(_savedProfile.Settings);
        _recoveryCoordinator?.UpdateSettings(_savedProfile.Settings);
        var localResult = _localTradingScenario is null
            ? null : await _localTradingScenario.UpdateProfileAsync(_savedProfile);
        _fileStatus = SettingsFileStatus.Loaded;
        _loadMessage = null;
        _canRecover = false;
        SetOverviewModeStatus(null);
        _appliedPreventSleep = _savedProfile.Settings.PreventSleep;
        _wakeProtection.SetEnabled(_appliedPreventSleep);
        LoadDraft(_savedProfile.Settings);
        UpdateMonitoringIntent();
        UpdateLocalTradingStatus();
        UpdatePowerStatus();
        ShowRecoveryButton(false);
        SetStatus(localResult is { Succeeded: false }
            ? "Настройки сохранены, но локальный цикл не завершил сверку: " + localResult.Status
            : _savedProfile.IsIncomplete()
            ? "Настройки сохранены. Торговый профиль пока заполнен частично."
            : "Настройки сохранены.", warning: localResult is { Succeeded: false });
    }

    private SettingsProfile CollectDraft(out Dictionary<string, string> parseErrors)
    {
        parseErrors = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new AppSettings
        {
            Assets = _assetRows.Select(row => (string?)row.Text).ToList(),
            EntryType = (EntryTypeComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
            CandidateSelectionMethod = (CandidateSelectionMethodComboBox.SelectedItem as ComboBoxItem)?.Tag as string
                ?? "",
            MarginBalancePercent = ParseOptionalDecimal(MarginPercentTextBox, nameof(AppSettings.MarginBalancePercent), parseErrors),
            Leverage = ParseOptionalDecimal(LeverageTextBox, nameof(AppSettings.Leverage), parseErrors),
            PositionCheckMinutes = ParseInteger(PositionCheckMinutesTextBox, nameof(AppSettings.PositionCheckMinutes), parseErrors),
            LimitOrderCheckMinutes = ParseInteger(LimitOrderCheckMinutesTextBox, nameof(AppSettings.LimitOrderCheckMinutes), parseErrors),
            PartialFillCancelMinutes = ParseInteger(PartialFillCancelMinutesTextBox, nameof(AppSettings.PartialFillCancelMinutes), parseErrors),
            NewSetupRetryMinutes = ParseInteger(NewSetupRetryMinutesTextBox, nameof(AppSettings.NewSetupRetryMinutes), parseErrors),
            LimitReassessmentMinutes = ParseInteger(LimitReassessmentMinutesTextBox, nameof(AppSettings.LimitReassessmentMinutes), parseErrors),
            ReadRetryCount = ParseInteger(ReadRetryCountTextBox, nameof(AppSettings.ReadRetryCount), parseErrors),
            ReadRetryDelaySeconds = ParseInteger(ReadRetryDelaySecondsTextBox, nameof(AppSettings.ReadRetryDelaySeconds), parseErrors),
            RecoveryCheckSeconds = ParseInteger(RecoveryCheckSecondsTextBox, nameof(AppSettings.RecoveryCheckSeconds), parseErrors),
            EmergencyCloseRetryCount = ParseInteger(EmergencyCloseRetryCountTextBox, nameof(AppSettings.EmergencyCloseRetryCount), parseErrors),
            EmergencyCloseRetryDelaySeconds = ParseInteger(EmergencyCloseRetryDelaySecondsTextBox, nameof(AppSettings.EmergencyCloseRetryDelaySeconds), parseErrors),
            EmergencyCloseAfterNoticeSeconds = ParseInteger(EmergencyCloseAfterNoticeSecondsTextBox, nameof(AppSettings.EmergencyCloseAfterNoticeSeconds), parseErrors),
            NotificationsEnabled = NotificationsEnabledCheckBox.IsChecked == true,
            OpeningNotifications = OpeningNotificationsCheckBox.IsChecked == true,
            ClosingNotifications = ClosingNotificationsCheckBox.IsChecked == true,
            OrderChangeNotifications = OrderChangeNotificationsCheckBox.IsChecked == true,
            ErrorNotifications = ErrorNotificationsCheckBox.IsChecked == true,
            RecoveryNotifications = RecoveryNotificationsCheckBox.IsChecked == true,
            FailureRemindersEnabled = FailureRemindersEnabledCheckBox.IsChecked == true,
            FailureReminderMinutes = ParseInteger(FailureReminderMinutesTextBox, nameof(AppSettings.FailureReminderMinutes), parseErrors),
            PreventSleep = PreventSleepCheckBox.IsChecked == true
        };

        var profile = _savedProfile.Copy();
        profile.Settings = settings;
        return profile;
    }

    private static decimal? ParseOptionalDecimal(TextBox textBox, string field, IDictionary<string, string> errors)
    {
        if (SettingsValidator.TryParseOptionalDecimal(textBox.Text, CultureInfo.CurrentCulture, out var value))
            return value;
        errors[field] = "Введите число с десятичным разделителем системной локали.";
        return null;
    }

    private static int ParseInteger(TextBox textBox, string field, IDictionary<string, string> errors)
    {
        if (int.TryParse(textBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out var value))
            return value;
        errors[field] = "Введите целое число.";
        return 0;
    }

    private void CancelSettings_Click(object sender, RoutedEventArgs e)
    {
        LoadDraft(_savedProfile.Settings);
        SetStatus(_loadMessage is null
            ? "Несохранённые изменения отменены."
            : "Несохранённые изменения отменены. " + _loadMessage,
            warning: _fileStatus is SettingsFileStatus.Corrupt or SettingsFileStatus.UnsupportedVersion or SettingsFileStatus.Unavailable);
    }

    private async void RecoverSettings_Click(object sender, RoutedEventArgs e)
    {
        await _profileCommandGate.WaitAsync();
        _profileCommandBusy = true;
        UpdateLocalTradingStatus();
        try
        {
            await RecoverSettingsCoreAsync();
        }
        finally
        {
            _profileCommandBusy = false;
            _profileCommandGate.Release();
            UpdateLocalTradingStatus();
        }
    }

    private async Task RecoverSettingsCoreAsync()
    {
        var result = _settingsStore.Recover();
        if (!result.Succeeded)
        {
            SetStatus(result.Message ?? "Не удалось восстановить настройки.", warning: true);
            return;
        }

        _savedProfile = result.Profile!;
        _fileStatus = SettingsFileStatus.Loaded;
        _loadMessage = null;
        _canRecover = false;
        _appliedPreventSleep = _savedProfile.Settings.PreventSleep;
        _wakeProtection.SetEnabled(_appliedPreventSleep);
        SetModeSelection(_savedProfile.SelectedMode);
        LoadDraft(_savedProfile.Settings);
        _notificationDispatcher?.UpdateSettings(_savedProfile.Settings);
        _recoveryCoordinator?.UpdateSettings(_savedProfile.Settings);
        var localResult = _localTradingScenario is null
            ? null : await _localTradingScenario.UpdateProfileAsync(_savedProfile);
        UpdateMonitoringIntent();
        UpdateLocalTradingStatus();
        UpdatePowerStatus();
        UpdateModeLabel();
        ShowRecoveryButton(false);
        SetStatus(localResult is { Succeeded: false }
            ? "Настройки восстановлены, но локальный цикл ожидает сверки: " + localResult.Status
            : "Последняя исправная копия восстановлена. Повреждённый файл сохранён рядом.",
            warning: localResult is { Succeeded: false });
    }

    private async void SendNotificationTest_Click(object sender, RoutedEventArgs e)
    {
        if (_notificationDispatcher is null)
        {
            NotificationTestStatusText.Text = "Тест недоступен в этой конфигурации окна.";
            return;
        }

        SendNotificationTestButton.IsEnabled = false;
        NotificationTestStatusText.Text = "Отправка теста...";
        try
        {
            var report = await _notificationDispatcher.SendTestAsync();
            var status = string.Join(Environment.NewLine,
                report.Channels.Select(result => $"{result.Channel}: {result.SafeStatus}"));
            await Dispatcher.InvokeAsync(() => NotificationTestStatusText.Text = status);
        }
        catch (Exception)
        {
            await Dispatcher.InvokeAsync(() =>
                NotificationTestStatusText.Text = "Тест завершён с неизвестным исходом; автоматический повтор запрещён.");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() => SendNotificationTestButton.IsEnabled = true);
        }
    }

    private void ShowLoadStatus()
    {
        ShowRecoveryButton(_fileStatus == SettingsFileStatus.Corrupt && _canRecover);
        if (_fallbackReadOnly)
            SetOverviewModeStatus(ReadOnlyFallbackMessage());
        if (!string.IsNullOrWhiteSpace(_loadMessage))
        {
            var warning = _fileStatus is SettingsFileStatus.Corrupt
                or SettingsFileStatus.UnsupportedVersion
                or SettingsFileStatus.Unavailable;
            SetStatus(_loadMessage, warning);
            return;
        }

        SetStatus(_savedProfile.IsIncomplete()
            ? "Профиль загружен. Торговые поля можно заполнить позже."
            : "Настройки загружены.", warning: false);
    }

    private void SetModeSelection(string mode)
    {
        _updatingMode = true;
        ModeList.SelectedItem = ModeList.Items.OfType<ListBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, mode, StringComparison.Ordinal));
        _updatingMode = false;
    }

    private void ShowRecoveryButton(bool visible) =>
        RecoverSettingsButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private void SetStatus(string message, bool warning)
    {
        SettingsStatusText.Text = message;
        SettingsStatusText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "MutedBrush");
    }

    private string ReadOnlyFallbackMessage() =>
        (_loadMessage ?? "Файл настроек недоступен.")
        + " Сохранение заблокировано, чтобы не заменить прежние значения. Перезапустите приложение после устранения причины.";

    private void SetOverviewModeStatus(string? message)
    {
        OverviewModeStatusText.Text = message ?? string.Empty;
        OverviewModeStatusText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearFieldErrors() => ShowFieldErrors(new Dictionary<string, string>());

    private void ShowFieldErrors(IReadOnlyDictionary<string, string> errors)
    {
        SetError(AssetsErrorText, errors, nameof(AppSettings.Assets));
        SetError(EntryTypeErrorText, errors, nameof(AppSettings.EntryType));
        SetError(CandidateSelectionMethodErrorText, errors, nameof(AppSettings.CandidateSelectionMethod));
        SetError(MarginPercentErrorText, errors, nameof(AppSettings.MarginBalancePercent));
        SetError(LeverageErrorText, errors, nameof(AppSettings.Leverage));
        SetError(PositionCheckMinutesErrorText, errors, nameof(AppSettings.PositionCheckMinutes));
        SetError(LimitOrderCheckMinutesErrorText, errors, nameof(AppSettings.LimitOrderCheckMinutes));
        SetError(PartialFillCancelMinutesErrorText, errors, nameof(AppSettings.PartialFillCancelMinutes));
        SetError(NewSetupRetryMinutesErrorText, errors, nameof(AppSettings.NewSetupRetryMinutes));
        SetError(LimitReassessmentMinutesErrorText, errors, nameof(AppSettings.LimitReassessmentMinutes));
        SetError(ReadRetryCountErrorText, errors, nameof(AppSettings.ReadRetryCount));
        SetError(ReadRetryDelaySecondsErrorText, errors, nameof(AppSettings.ReadRetryDelaySeconds));
        SetError(RecoveryCheckSecondsErrorText, errors, nameof(AppSettings.RecoveryCheckSeconds));
        SetError(EmergencyCloseRetryCountErrorText, errors, nameof(AppSettings.EmergencyCloseRetryCount));
        SetError(EmergencyCloseRetryDelaySecondsErrorText, errors, nameof(AppSettings.EmergencyCloseRetryDelaySeconds));
        SetError(EmergencyCloseAfterNoticeSecondsErrorText, errors, nameof(AppSettings.EmergencyCloseAfterNoticeSeconds));
        SetError(FailureReminderMinutesErrorText, errors, nameof(AppSettings.FailureReminderMinutes));
    }

    private static void SetError(TextBlock textBlock, IReadOnlyDictionary<string, string> errors, string field)
    {
        textBlock.Text = errors.TryGetValue(field, out var message) ? message : string.Empty;
    }

    private void UpdatePowerStatus()
    {
        PowerStatusText.SetResourceReference(TextBlock.ForegroundProperty,
            _wakeProtection.Error is null ? "MutedBrush" : "WarningBrush");
        PowerStatusText.Text = _wakeProtection.Error
            ?? (_wakeProtection.IsRequestHeld
                ? "Windows удерживает систему активной; экран может выключаться."
                : _appliedPreventSleep
                    ? "Торговля и мониторинг не запущены. Запрос к Windows сейчас не отправляется."
                    : "Защита от сна выключена.");
    }

    private void UpdatePowerStatusFromController()
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            return;

        if (Dispatcher.CheckAccess())
            UpdatePowerStatus();
        else
            Dispatcher.BeginInvoke(new Action(UpdatePowerStatus));
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            HideToTray();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
            return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        WindowState = WindowState.Normal;
        ShowInTaskbar = false;
        Hide();
    }
}

internal sealed class AssetDraft : INotifyPropertyChanged
{
    private string _text;
    private int _position;

    internal AssetDraft(string text, int position)
    {
        _text = text;
        _position = position;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            OnPropertyChanged();
        }
    }

    public int Position
    {
        get => _position;
        set
        {
            if (_position == value)
                return;
            _position = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
