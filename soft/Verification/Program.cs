using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TrueNorth.TradeFlow.Verification;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static int Main(string[] args)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        if (args.Contains("--process-recovery-test-worker", StringComparer.Ordinal))
            return ProcessRecoveryChecks.RunWorker(args);

        var entryEligibilityOnly = args.Contains("--entry-eligibility", StringComparer.Ordinal);
        var recoveryOnly = args.Contains("--notifications-recovery", StringComparer.Ordinal);
        var orderLifecycleOnly = args.Contains("--order-lifecycle", StringComparer.Ordinal);
        var processRecoveryRuntimeOnly = args.Contains("--process-recovery-runtime", StringComparer.Ordinal);
        var tradingLoopOnly = args.Contains("--trading-loop", StringComparer.Ordinal);
        var fixtureRoot = Path.Combine(ArtifactsDirectory(), ".fixtures", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(fixtureRoot);
            if (tradingLoopOnly)
            {
                TradingLoopChecks.CheckCoreAsync(Check, fixtureRoot).GetAwaiter().GetResult();
                var app = new App();
                app.InitializeComponent();
                try
                {
                    TradingLoopChecks.CheckMainWindow(Check, app, fixtureRoot, ArtifactsDirectory());
                }
                finally
                {
                    app.Shutdown();
                }
                Console.WriteLine($"PASS: {_checks} trading-loop assertions (--trading-loop)");
                return 0;
            }
            if (processRecoveryRuntimeOnly)
            {
                ProcessRecoveryChecks.CheckCore(Check, fixtureRoot);
                ProcessRecoveryChecks.CheckApplicationRuntime(Check, fixtureRoot);
                Console.WriteLine($"PASS: {_checks} process-recovery runtime assertions (--process-recovery-runtime)");
                return 0;
            }
            if (entryEligibilityOnly)
            {
                EntryEligibilityChecks.CheckCore(Check);
                EntryEligibilityChecks.CheckNativeWpf(Check, fixtureRoot);
                Console.WriteLine($"PASS: {_checks} entry-eligibility assertions (--entry-eligibility)");
                return 0;
            }
            if (orderLifecycleOnly)
            {
                OrderLifecycleChecks.CheckCore(Check, fixtureRoot);
                OrderLifecycleChecks.CheckNativeWpf(Check, fixtureRoot);
                Console.WriteLine($"PASS: {_checks} order-lifecycle assertions (--order-lifecycle)");
                return 0;
            }
            if (recoveryOnly)
            {
                RecoveryChecks.CheckCore(Check, fixtureRoot);
                ProcessRecoveryChecks.CheckCore(Check, fixtureRoot);
                ProcessRecoveryChecks.CheckRuntime(Check, fixtureRoot);
                NotificationChecks.CheckCore(Check, fixtureRoot);
                var app = new App();
                app.InitializeComponent();
                try
                {
                    RecoveryChecks.CheckNativeWpf(Check, fixtureRoot);
                    ProcessRecoveryChecks.CheckNativeWpf(Check, fixtureRoot);
                    NotificationChecks.CheckNativeWpf(Check, fixtureRoot,
                        exerciseWindowsBalloon: args.Contains("--observe-windows-notification", StringComparer.Ordinal));
                }
                finally
                {
                    app.Shutdown();
                }
                Console.WriteLine($"PASS: {_checks} notifications-recovery assertions (--notifications-recovery)");
                return 0;
            }

            CheckDefaultsAndMigration(fixtureRoot);
            CheckValidationAndCulture();
            CheckRoundTrip(fixtureRoot);
            CheckMalformedAndRecovery(fixtureRoot);
            CheckUnsupportedAndUnavailable(fixtureRoot);
            CheckFailedWriteKeepsLastGood(fixtureRoot);
            TrueNorthSessionChecks.CheckMarkerClassifier(Check);
            TrueNorthSessionChecks.CheckTransportAsync(Check, TrueNorthSessionChecks.ArtifactsDirectory()).GetAwaiter().GetResult();
            SimulationChecks.CheckScenarios(Check);
            AiTestChecks.CheckCore(Check, fixtureRoot);
            AccountReadChecks.CheckCore(Check);
            EntryEligibilityChecks.CheckCore(Check);
            OrderLifecycleChecks.CheckCore(Check, fixtureRoot);
            OrderLifecycleChecks.CheckNativeWpf(Check, fixtureRoot);
            TradingLoopChecks.CheckCoreAsync(Check, fixtureRoot).GetAwaiter().GetResult();
            RecoveryChecks.CheckCore(Check, fixtureRoot);
            ProcessRecoveryChecks.CheckCore(Check, fixtureRoot);
            ProcessRecoveryChecks.CheckRuntime(Check, fixtureRoot);
            NotificationChecks.CheckCore(Check, fixtureRoot);
            CheckNativeWpf(fixtureRoot);
            Console.WriteLine($"PASS: {_checks} assertions");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL after {_checks} assertions: {exception}");
            return 1;
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            if (entryEligibilityOnly || recoveryOnly || orderLifecycleOnly || processRecoveryRuntimeOnly)
                Application.Current?.Shutdown();
            try
            {
                Directory.Delete(fixtureRoot, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void CheckDefaultsAndMigration(string root)
    {
        var defaultPath = Path.Combine(root, "defaults", "settings.json");
        var reads = 0;
        var defaults = new SettingsStore(defaultPath, () => { reads++; return null; }).Load();
        Check(defaults.Status == SettingsFileStatus.Missing, "missing settings are distinct from read errors");
        Check(!File.Exists(defaultPath), "loading defaults does not write a file");
        Check(reads == 1, "legacy value is read once only when settings are missing");
        Check(defaults.Profile.SelectedMode == "Test" && !defaults.Profile.SavedOperatingIntent,
            "first-run mode is Test and saved operating intent is stopped");

        var settings = defaults.Profile.Settings;
        Check(settings.Assets.Count == 0 && settings.EntryType is null
              && settings.MarginBalancePercent is null && settings.Leverage is null,
            "first-run trading inputs are empty");
        Check(settings.PositionCheckMinutes == 60 && settings.LimitOrderCheckMinutes == 2
              && settings.PartialFillCancelMinutes == 60 && settings.NewSetupRetryMinutes == 240
              && settings.LimitReassessmentMinutes == 240,
            "monitoring and AI interval defaults match the plan");
        Check(settings.ReadRetryCount == 3 && settings.ReadRetryDelaySeconds == 10
              && settings.RecoveryCheckSeconds == 60 && settings.EmergencyCloseRetryCount == 3
              && settings.EmergencyCloseRetryDelaySeconds == 10 && settings.EmergencyCloseAfterNoticeSeconds == 30,
            "read and close recovery defaults are independent and correct");
        Check(settings.NotificationsEnabled && settings.OpeningNotifications && settings.ClosingNotifications
              && settings.OrderChangeNotifications && settings.ErrorNotifications && settings.RecoveryNotifications
              && settings.FailureRemindersEnabled && settings.FailureReminderMinutes == 60 && settings.PreventSleep,
            "notification and power defaults match the plan");

        foreach (var legacyValue in new[] { false, true })
        {
            var path = Path.Combine(root, $"legacy-{legacyValue}", "settings.json");
            var legacyReads = 0;
            var store = new SettingsStore(path, () => { legacyReads++; return legacyValue; });
            var migrated = store.Load();
            Check(migrated.Status == SettingsFileStatus.Missing && migrated.Profile.Settings.PreventSleep == legacyValue,
                $"legacy PreventSleep={legacyValue} is applied only when the file is missing");
            var saved = store.Save(migrated.Profile);
            Check(saved.Succeeded && legacyReads == 1 && File.Exists(path),
                $"legacy PreventSleep={legacyValue} survives the first JSON save without another registry read "
                + $"(status={saved.Status}, reads={legacyReads}, exists={File.Exists(path)}, message={saved.Message})");
        }
    }

    private static void CheckValidationAndCulture()
    {
        var incomplete = SettingsValidator.Validate(new SettingsProfile());
        Check(incomplete.IsValid && incomplete.Profile.IsIncomplete(), "missing trading values are allowed and marked incomplete");

        var duplicate = CompleteProfile();
        duplicate.Settings.Assets = [" BTC ", "btc"];
        Check(SettingsValidator.Validate(duplicate).Errors.ContainsKey(nameof(AppSettings.Assets)),
            "asset uniqueness ignores case after trimming");

        var badPercent = CompleteProfile();
        badPercent.Settings.MarginBalancePercent = 100.01m;
        Check(SettingsValidator.Validate(badPercent).Errors.ContainsKey(nameof(AppSettings.MarginBalancePercent)),
            "balance percent above 100 is rejected");
        badPercent.Settings.MarginBalancePercent = 0;
        Check(SettingsValidator.Validate(badPercent).Errors.ContainsKey(nameof(AppSettings.MarginBalancePercent)),
            "zero balance percent is rejected");

        var badLeverage = CompleteProfile();
        badLeverage.Settings.Leverage = 0.5m;
        Check(SettingsValidator.Validate(badLeverage).Errors.ContainsKey(nameof(AppSettings.Leverage)),
            "leverage below one is rejected");

        var badInterval = CompleteProfile();
        badInterval.Settings.LimitOrderCheckMinutes = 0;
        Check(SettingsValidator.Validate(badInterval).Errors.ContainsKey(nameof(AppSettings.LimitOrderCheckMinutes)),
            "nonpositive intervals are rejected");
        badInterval.Settings.ReadRetryCount = -1;
        Check(SettingsValidator.Validate(badInterval).Errors.ContainsKey(nameof(AppSettings.ReadRetryCount)),
            "negative additional retry counts are rejected");

        Check(SettingsValidator.TryParseOptionalDecimal("12,5", CultureInfo.GetCultureInfo("ru-RU"), out var ruValue)
              && ruValue == 12.5m,
            "decimal input uses the current locale separator");
        Check(SettingsValidator.TryParseOptionalDecimal("12.5", CultureInfo.GetCultureInfo("en-US"), out var usValue)
              && usValue == 12.5m,
            "decimal input accepts the English locale separator");
        Check(!SettingsValidator.TryParseOptionalDecimal("12,5", CultureInfo.GetCultureInfo("en-US"), out _),
            "decimal input does not silently reinterpret another locale separator");
        Check(!SettingsValidator.TryParseOptionalDecimal("1,5", CultureInfo.GetCultureInfo("en-US"), out _),
            "decimal input rejects grouping separators instead of silently changing the value");
    }

    private static void CheckRoundTrip(string root)
    {
        var path = Path.Combine(root, "roundtrip", "settings.json");
        var store = new SettingsStore(path);
        var profile = CompleteProfile();
        var expected = SettingsValidator.Validate(profile).Profile;
        var priorCulture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var saved = store.Save(profile);
            Check(saved.Succeeded, "a complete profile is saved");
            var json = File.ReadAllText(path);
            Check(json.Contains("12.5", StringComparison.Ordinal) && !json.Contains("12,5", StringComparison.Ordinal),
                "decimal persistence uses invariant JSON numbers under ru-RU");
            var loaded = store.Load();
            Check(loaded.Status == SettingsFileStatus.Loaded, "saved profile reloads");
            Check(ProfilesEqual(expected, loaded.Profile), "all settings, asset order, mode and operating intent round-trip");
        }
        finally
        {
            CultureInfo.CurrentCulture = priorCulture;
        }
    }

    private static void CheckMalformedAndRecovery(string root)
    {
        var path = Path.Combine(root, "recovery", "settings.json");
        var store = new SettingsStore(path);
        var firstGood = CompleteProfile();
        firstGood.Settings.Assets = ["BTC", "ETH"];
        firstGood.Settings.MarginBalancePercent = 10;
        Check(store.Save(firstGood).Succeeded, "initial good settings are saved");

        var latest = firstGood.Copy();
        latest.Settings.MarginBalancePercent = 20;
        latest.SelectedMode = "TrueNorth AI setups";
        Check(store.Save(latest).Succeeded, "second settings save creates a last-known-good backup");
        Check(ProfilesEqual(SettingsValidator.Validate(firstGood).Profile,
                new SettingsStore(path + ".bak").Load().Profile),
            "backup contains the previous verified profile");

        File.WriteAllText(path, "{broken");
        var corruptBytes = File.ReadAllBytes(path);
        var corrupt = store.Load();
        Check(corrupt.Status == SettingsFileStatus.Corrupt && corrupt.CanRecover,
            "malformed JSON is reported as corrupt with recovery available");
        var recovery = store.Recover();
        Check(recovery.Succeeded && ProfilesEqual(SettingsValidator.Validate(firstGood).Profile, recovery.Profile!),
            "explicit recovery loads only the verified backup");
        var archives = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt.*");
        Check(archives.Length == 1 && File.ReadAllBytes(archives[0]).SequenceEqual(corruptBytes),
            "the corrupt original is preserved byte for byte");

        var malformedPath = Path.Combine(root, "malformed", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
        File.WriteAllText(malformedPath, "{\"formatVersion\":1}");
        var malformed = new SettingsStore(malformedPath).Load();
        Check(malformed.Status == SettingsFileStatus.Corrupt && !malformed.CanRecover,
            "a parseable file missing required sections is corrupt rather than a default profile");

        var nullAssetsPath = Path.Combine(root, "null-assets", "settings.json");
        var nullAssetsStore = new SettingsStore(nullAssetsPath);
        Check(nullAssetsStore.Save(CompleteProfile()).Succeeded, "null-asset fixture starts from a valid JSON file");
        var nullAssetsJson = File.ReadAllText(nullAssetsPath)
            .Replace("\"assets\": [", "\"assets\": null", StringComparison.Ordinal);
        File.WriteAllText(nullAssetsPath, nullAssetsJson);
        Check(nullAssetsStore.Load().Status == SettingsFileStatus.Corrupt,
            "assets:null is rejected as corrupt rather than normalized to an empty profile");
    }

    private static void CheckUnsupportedAndUnavailable(string root)
    {
        var unsupportedPath = Path.Combine(root, "unsupported", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(unsupportedPath)!);
        const string unsupportedJson = "{\"formatVersion\":999,\"profile\":\"future body\"}";
        File.WriteAllText(unsupportedPath, unsupportedJson);
        var unsupportedStore = new SettingsStore(unsupportedPath);
        var unsupported = unsupportedStore.Load();
        Check(unsupported.Status == SettingsFileStatus.UnsupportedVersion,
            "a parseable future version is protected even when its body is unfamiliar");
        var refused = unsupportedStore.Save(CompleteProfile(), replaceCorruptFile: true);
        Check(refused.Status == SettingsOperationStatus.UnsupportedVersion
              && File.ReadAllText(unsupportedPath) == unsupportedJson,
            "unsupported settings are never overwritten by this version");

        var oversizedVersionPath = Path.Combine(root, "oversized-version", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oversizedVersionPath)!);
        const string oversizedVersionJson = "{\"formatVersion\":999999999999999999999,\"profile\":null}";
        File.WriteAllText(oversizedVersionPath, oversizedVersionJson);
        Check(new SettingsStore(oversizedVersionPath).Load().Status == SettingsFileStatus.UnsupportedVersion,
            "numeric versions outside Int32 are protected as unsupported");

        var directoryPath = Path.Combine(root, "unreadable", "settings.json");
        Directory.CreateDirectory(directoryPath);
        var legacyReads = 0;
        var unavailableStore = new SettingsStore(directoryPath, () => { legacyReads++; return false; });
        var unavailable = unavailableStore.Load();
        Check(unavailable.Status == SettingsFileStatus.Unavailable && legacyReads == 0,
            "an inaccessible target is distinct from a missing file and does not trigger migration");
        Check(unavailableStore.Save(CompleteProfile()).Status == SettingsOperationStatus.Unavailable,
            "an inaccessible target cannot be replaced");

        var unlockPath = Path.Combine(root, "unlock", "settings.json");
        Directory.CreateDirectory(unlockPath);
        var fallbackStore = new SettingsStore(unlockPath);
        var lockedLoad = fallbackStore.Load();
        Check(lockedLoad.Status == SettingsFileStatus.Unavailable, "locked startup returns a read-only fallback");
        var goodPath = Path.Combine(root, "unlock-good", "settings.json");
        var goodWriter = new SettingsStore(goodPath);
        Check(goodWriter.Save(CompleteProfileWithPercent(24)).Succeeded, "unlock fixture's valid profile is prepared");
        var goodBytes = File.ReadAllBytes(goodPath);
        Directory.Delete(unlockPath);
        File.WriteAllBytes(unlockPath, goodBytes);
        var blockedSave = fallbackStore.Save(CompleteProfileWithPercent(88));
        Check(blockedSave.Status == SettingsOperationStatus.Unavailable
              && File.ReadAllBytes(unlockPath).SequenceEqual(goodBytes),
            "unlocking a path does not let a stale fallback overwrite the valid file; reload is required");
    }

    private static void CheckFailedWriteKeepsLastGood(string root)
    {
        var path = Path.Combine(root, "write-failure", "settings.json");
        var store = new SettingsStore(path);
        var original = CompleteProfile();
        Check(store.Save(original).Succeeded, "last-known-good source profile is saved");
        var originalBytes = File.ReadAllBytes(path);
        var failingStore = new SettingsStore(path, commitOverride: (_, _, _) => throw new IOException("synthetic write refusal"));
        var failed = failingStore.Save(CompleteProfileWithPercent(37));
        Check(!failed.Succeeded && failed.Status == SettingsOperationStatus.Failed,
            "a staging/commit failure is reported consistently");
        Check(File.ReadAllBytes(path).SequenceEqual(originalBytes)
              && ProfilesEqual(SettingsValidator.Validate(original).Profile, failingStore.Load().Profile),
            "a failed commit leaves the active last-known-good settings unchanged");
    }

    private static void CheckNativeWpf(string root)
    {
        var path = Path.Combine(root, "wpf", "settings.json");
        var store = new SettingsStore(path);
        var initial = CompleteProfile();
        initial.Settings.Assets = ["BTC", "ETH"];
        initial.Settings.MarginBalancePercent = 25;
        initial.Settings.PreventSleep = false;
        initial.SelectedMode = "Test";
        initial.SavedOperatingIntent = true;
        Check(store.Save(initial).Succeeded, "isolated WPF fixture is prepared");
        var originalSettings = SettingsValidator.Validate(initial).Profile.Settings.Copy();

        var app = Application.Current as App;
        if (app is null)
        {
            if (Application.Current is not null)
                throw new InvalidOperationException("A non-TradeFlow WPF application is already active in this verification process.");
            app = new App();
            app.InitializeComponent();
        }
        var appliedRequests = new List<bool>();
        var controller = new WakeProtectionController(false, value => { appliedRequests.Add(value); return true; });
        var session = new TrueNorthSessionChecks.RecordingSessionService();
        var window = new MainWindow(store, store.Load(), controller, session);
        try
        {
            window.Show();
            window.UpdateLayout();
            PumpDispatcher();
            Check(Get<TextBlock>(window, "OperatingIntentText").Text.Contains("Фактический мониторинг не запущен", StringComparison.Ordinal),
                "saved running intent never starts monitoring in this stage");
            TrueNorthSessionChecks.CheckOverview(window, session, Check, PumpDispatcher);

            Get<ListBox>(window, "NavigationList").SelectedIndex = 1;
            window.UpdateLayout();
            var assets = Get<ListBox>(window, "AssetsList");
            var rows = (System.Collections.ObjectModel.ObservableCollection<AssetDraft>)assets.ItemsSource;
            rows[1].Text = "ETHUSDT-PERP";
            window.UpdateLayout();
            var firstAssetItem = assets.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Asset row was not generated.");
            var assetEditor = FindVisualChild<TextBox>(firstAssetItem)
                ?? throw new InvalidOperationException("Asset text box was not generated.");
            Check(assetEditor.ActualWidth > 200, "asset editors stretch across the available row width");
            string[] keyboardControlNames =
            [
                "AssetsList", "AddAssetButton", "AssetUpButton", "AssetDownButton", "RemoveAssetButton",
                "EntryTypeComboBox", "MarginPercentTextBox", "LeverageTextBox", "PositionCheckMinutesTextBox",
                "LimitOrderCheckMinutesTextBox", "PartialFillCancelMinutesTextBox", "NewSetupRetryMinutesTextBox",
                "LimitReassessmentMinutesTextBox", "ReadRetryCountTextBox", "ReadRetryDelaySecondsTextBox",
                "RecoveryCheckSecondsTextBox", "EmergencyCloseRetryCountTextBox", "EmergencyCloseRetryDelaySecondsTextBox",
                "EmergencyCloseAfterNoticeSecondsTextBox", "NotificationsEnabledCheckBox", "OpeningNotificationsCheckBox",
                "ClosingNotificationsCheckBox", "OrderChangeNotificationsCheckBox", "ErrorNotificationsCheckBox",
                "RecoveryNotificationsCheckBox", "FailureRemindersEnabledCheckBox", "FailureReminderMinutesTextBox",
                "PreventSleepCheckBox", "SaveSettingsButton", "CancelSettingsButton"
            ];
            var nonTabStops = keyboardControlNames.Where(name =>
            {
                var control = Get<Control>(window, name);
                return !control.Focusable || !control.IsTabStop;
            }).ToArray();
            Check(nonTabStops.Length == 0 && assetEditor.Focusable && assetEditor.IsTabStop,
                "settings fields and actions remain on the native keyboard focus path "
                + $"(controls: {string.Join(", ", nonTabStops)})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-window.png"));

            var margin = Get<TextBox>(window, "MarginPercentTextBox");
            var modes = Get<ListBox>(window, "ModeList");
            var entryTypes = Get<ComboBox>(window, "EntryTypeComboBox");
            Check(entryTypes.Items.Count == 3 && entryTypes.Focusable && entryTypes.IsTabStop
                  && (entryTypes.SelectedItem as ComboBoxItem)?.Tag as string == "Limit",
                "entry selector is keyboard accessible and shows its saved value");
            var savedEntryIndex = entryTypes.SelectedIndex;
            entryTypes.SelectedIndex = 0;
            window.UpdateLayout();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-window-entry-empty.png"));
            entryTypes.ApplyTemplate();
            window.UpdateLayout();
            var entryPopup = (Popup?)entryTypes.Template.FindName("Popup", entryTypes)
                ?? throw new InvalidOperationException("Entry type popup was not found.");
            entryTypes.IsDropDownOpen = true;
            PumpDispatcher();
            if (entryPopup.Child is FrameworkElement popupContent)
            {
                Check(entryPopup.IsOpen && popupContent.ActualWidth > 0 && popupContent.ActualHeight > 0,
                    "entry popup opens with a laid-out option panel");
                RenderElement(popupContent, Path.Combine(ArtifactsDirectory(), "settings-window-entry-popup.png"));
            }
            else
                throw new InvalidOperationException("Entry type popup content is unavailable.");
            entryTypes.IsDropDownOpen = false;
            entryTypes.SelectedIndex = savedEntryIndex;
            margin.Text = "42";
            modes.SelectedIndex = 1;
            var afterModeSave = store.Load();
            Check(afterModeSave.Profile.SelectedMode == "TrueNorth AI setups"
                  && afterModeSave.Profile.Settings.MarginBalancePercent == 25
                  && afterModeSave.Profile.Settings.Assets.SequenceEqual(["BTC", "ETH"])
                  && margin.Text == "42",
                "mode auto-save persists independently and retains the unsaved form draft");

            Click(Get<Button>(window, "AddAssetButton"));
            rows[2].Text = "SOL";
            assets.SelectedIndex = 2;
            Click(Get<Button>(window, "AssetUpButton"));
            Check(rows.Select(row => row.Text).SequenceEqual(["BTC", "SOL", "ETHUSDT-PERP"]),
                "asset move-up preserves visible order");
            Click(Get<Button>(window, "AssetDownButton"));
            Check(rows.Select(row => row.Text).SequenceEqual(["BTC", "ETHUSDT-PERP", "SOL"]),
                "asset move-down preserves visible order");
            assets.SelectedIndex = 2;
            Click(Get<Button>(window, "RemoveAssetButton"));
            Check(rows.Select(row => row.Text).SequenceEqual(["BTC", "ETHUSDT-PERP"]), "asset removal updates the ordered list");
            entryTypes.SelectedIndex = 1;
            Click(Get<Button>(window, "SaveSettingsButton"));
            var afterSave = store.Load().Profile;
            var expectedSettings = originalSettings.Copy();
            expectedSettings.Assets = ["BTC", "ETHUSDT-PERP"];
            expectedSettings.MarginBalancePercent = 42;
            expectedSettings.EntryType = "Market";
            Check(JsonSerializer.Serialize(afterSave.Settings) == JsonSerializer.Serialize(expectedSettings),
                "Save applies the full validated draft and keeps asset order");

            rows[1].Text = "btc";
            Click(Get<Button>(window, "SaveSettingsButton"));
            Check(Get<TextBlock>(window, "AssetsErrorText").Text.Length > 0
                  && store.Load().Profile.Settings.Assets.SequenceEqual(["BTC", "ETHUSDT-PERP"]),
                "duplicate asset is shown inline and does not replace the saved order");
            rows[1].Text = "ETHUSDT-PERP";

            margin.Text = "0";
            Click(Get<Button>(window, "SaveSettingsButton"));
            Check(Get<TextBlock>(window, "MarginPercentErrorText").Text.Length > 0
                  && store.Load().Profile.Settings.MarginBalancePercent == 42
                  && margin.Text == "0",
                "invalid input shows its field error and preserves both storage and draft");
            Click(Get<Button>(window, "CancelSettingsButton"));
            Check(margin.Text == "42", "Cancel restores the last saved settings");

            var preventSleep = Get<CheckBox>(window, "PreventSleepCheckBox");
            preventSleep.IsChecked = true;
            Check(!controller.IsEnabled && store.Load().Profile.Settings.PreventSleep == false,
                "editing the sleep preference does not apply it before persistence");
            Click(Get<Button>(window, "SaveSettingsButton"));
            Check(controller.IsEnabled && store.Load().Profile.Settings.PreventSleep
                  && appliedRequests.Count == 0,
                "sleep preference is applied to the controller only after save and still makes no OS request while inactive");

            window.AllowApplicationClose();
            window.Close();
            controller.Dispose();

            var restarted = store.Load();
            var restartController = new WakeProtectionController(restarted.Profile.Settings.PreventSleep,
                value => { appliedRequests.Add(value); return true; });
            var restartedWindow = new MainWindow(store, restarted, restartController);
            try
            {
                Check(((ListBoxItem)Get<ListBox>(restartedWindow, "ModeList").SelectedItem).Tag as string
                      == "TrueNorth AI setups"
                      && restarted.Profile.SavedOperatingIntent
                      && Get<TextBlock>(restartedWindow, "OperatingIntentText").Text.Contains("не запущен", StringComparison.Ordinal),
                    "restart restores mode and intent while keeping actual monitoring off");
                Check(appliedRequests.Count == 0, "loading the saved sleep setting does not make a request while monitoring is off");
            restartedWindow.Width = 760;
            restartedWindow.Height = 560;
                restartedWindow.Show();
                Get<ListBox>(restartedWindow, "NavigationList").SelectedIndex = 1;
                restartedWindow.UpdateLayout();
            PumpDispatcher();
            var scrollViewer = Get<ScrollViewer>(restartedWindow, "MainScrollViewer");
            var minimumAssets = Get<ListBox>(restartedWindow, "AssetsList");
            var firstMinimumAsset = minimumAssets.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Minimum-width asset row was not generated.");
            var minimumAssetEditor = FindVisualChild<TextBox>(firstMinimumAsset)
                ?? throw new InvalidOperationException("Minimum-width asset editor was not generated.");
            RenderWindow(restartedWindow, Path.Combine(ArtifactsDirectory(), "settings-window-minimum-top.png"));
            Check(minimumAssetEditor.ActualWidth > 100, "long asset text remains editable at the minimum window width");
            scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight);
                restartedWindow.UpdateLayout();
                PumpDispatcher();
                RenderWindow(restartedWindow, Path.Combine(ArtifactsDirectory(), "settings-window-bottom.png"));
            Check(scrollViewer.ScrollableHeight > 0
                  && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1
                  && Get<Button>(restartedWindow, "SaveSettingsButton").ActualWidth > 0
                  && Get<Button>(restartedWindow, "CancelSettingsButton").ActualWidth > 0,
                "Save and Cancel remain laid out at the minimum window width");
            }
            finally
            {
                restartedWindow.AllowApplicationClose();
                restartedWindow.Close();
                restartController.Dispose();
            }

            var brokenPath = Path.Combine(root, "wpf-save-failure", "settings.json");
            var brokenBase = new SettingsStore(brokenPath);
            var brokenProfile = CompleteProfileWithPercent(50);
            brokenProfile.SelectedMode = "Test";
            brokenProfile.Settings.PreventSleep = false;
            Check(brokenBase.Save(brokenProfile).Succeeded, "mode-failure fixture is prepared");
            var brokenStore = new SettingsStore(brokenPath,
                commitOverride: (_, _, _) => throw new IOException("synthetic mode write failure"));
            var brokenBytes = File.ReadAllBytes(brokenPath);
            var brokenController = new WakeProtectionController(false, _ => true);
            var brokenWindow = new MainWindow(brokenStore, brokenStore.Load(), brokenController);
            try
            {
                brokenWindow.Show();
                Get<ListBox>(brokenWindow, "NavigationList").SelectedIndex = 1;
                Get<TextBox>(brokenWindow, "MarginPercentTextBox").Text = "70";
                Get<CheckBox>(brokenWindow, "PreventSleepCheckBox").IsChecked = true;
                Click(Get<Button>(brokenWindow, "SaveSettingsButton"));
                Check(File.ReadAllBytes(brokenPath).SequenceEqual(brokenBytes)
                      && brokenStore.Load().Profile.Settings.MarginBalancePercent == 50
                      && !brokenController.IsEnabled
                      && Get<TextBox>(brokenWindow, "MarginPercentTextBox").Text == "70",
                    "failed settings commit keeps the primary bytes, controller and draft unchanged");

                Get<ListBox>(brokenWindow, "NavigationList").SelectedIndex = 0;
                var brokenModes = Get<ListBox>(brokenWindow, "ModeList");
                brokenModes.SelectedIndex = 1;
                var overviewStatus = Get<TextBlock>(brokenWindow, "OverviewModeStatusText");
                Check(brokenModes.SelectedIndex == 0 && brokenStore.Load().Profile.SelectedMode == "Test"
                      && overviewStatus.Visibility == Visibility.Visible
                      && overviewStatus.Text.Contains("Не удалось", StringComparison.Ordinal),
                    "mode selection rolls back and shows its save error beside the overview selector");
            }
            finally
            {
                brokenWindow.AllowApplicationClose();
                brokenWindow.Close();
                brokenController.Dispose();
            }

            var lockedPath = Path.Combine(root, "wpf-locked", "settings.json");
            Directory.CreateDirectory(lockedPath);
            var lockedStore = new SettingsStore(lockedPath);
            var lockedLoad = lockedStore.Load();
            var validPath = Path.Combine(root, "wpf-unlocked-valid", "settings.json");
            var validWriter = new SettingsStore(validPath);
            var validProfile = CompleteProfileWithPercent(31);
            validProfile.SelectedMode = "Test";
            validProfile.Settings.PreventSleep = false;
            Check(lockedLoad.Status == SettingsFileStatus.Unavailable && validWriter.Save(validProfile).Succeeded,
                "read-only UI fallback and unlocked source profile are prepared");
            var validBytes = File.ReadAllBytes(validPath);
            Directory.Delete(lockedPath);
            File.WriteAllBytes(lockedPath, validBytes);
            var lockedController = new WakeProtectionController(false, _ => true);
            var lockedWindow = new MainWindow(lockedStore, lockedLoad, lockedController);
            try
            {
                lockedWindow.Show();
                var lockedModes = Get<ListBox>(lockedWindow, "ModeList");
                lockedModes.SelectedIndex = 1;
                var readOnlyStatus = Get<TextBlock>(lockedWindow, "OverviewModeStatusText");
                Check(lockedModes.SelectedIndex == 0 && readOnlyStatus.Visibility == Visibility.Visible
                      && readOnlyStatus.Text.Contains("Перезапустите приложение", StringComparison.Ordinal),
                    "mode changes from a startup fallback roll back with restart guidance");

                Get<ListBox>(lockedWindow, "NavigationList").SelectedIndex = 1;
                Get<TextBox>(lockedWindow, "MarginPercentTextBox").Text = "99";
                Get<CheckBox>(lockedWindow, "PreventSleepCheckBox").IsChecked = true;
                Click(Get<Button>(lockedWindow, "SaveSettingsButton"));
                Check(File.ReadAllBytes(lockedPath).SequenceEqual(validBytes)
                      && !lockedController.IsEnabled
                      && Get<TextBlock>(lockedWindow, "SettingsStatusText").Text.Contains("Перезапустите приложение", StringComparison.Ordinal),
                    "unlocking the file after fallback load still blocks stale UI saves and power changes");
            }
            finally
            {
                lockedWindow.AllowApplicationClose();
                lockedWindow.Close();
                lockedController.Dispose();
            }

            SimulationChecks.CheckMainWindow(Check, app);
            AiTestChecks.CheckMainWindow(Check, app, root);
            AccountReadChecks.CheckNativeWpf(Check, root);
            EntryEligibilityChecks.CheckNativeWpf(Check, root);
            RecoveryChecks.CheckNativeWpf(Check, root);
            ProcessRecoveryChecks.CheckNativeWpf(Check, root);
            NotificationChecks.CheckNativeWpf(Check, root);
            TradingLoopChecks.CheckMainWindow(Check, app, root, ArtifactsDirectory());
        }
        finally
        {
            if (window.IsVisible)
            {
                window.AllowApplicationClose();
                window.Close();
            }
            controller.Dispose();
            app.Shutdown();
        }
    }

    private static SettingsProfile CompleteProfile() => new()
    {
        Settings = new AppSettings
        {
            Assets = [" BTC ", "ETH", "SOL"],
            EntryType = "Limit",
            MarginBalancePercent = 12.5m,
            Leverage = 3.25m,
            PositionCheckMinutes = 45,
            LimitOrderCheckMinutes = 3,
            PartialFillCancelMinutes = 75,
            NewSetupRetryMinutes = 210,
            LimitReassessmentMinutes = 180,
            ReadRetryCount = 4,
            ReadRetryDelaySeconds = 7,
            RecoveryCheckSeconds = 45,
            EmergencyCloseRetryCount = 2,
            EmergencyCloseRetryDelaySeconds = 8,
            EmergencyCloseAfterNoticeSeconds = 25,
            NotificationsEnabled = false,
            OpeningNotifications = true,
            ClosingNotifications = false,
            OrderChangeNotifications = true,
            ErrorNotifications = false,
            RecoveryNotifications = true,
            FailureRemindersEnabled = false,
            FailureReminderMinutes = 90,
            PreventSleep = false
        },
        SelectedMode = "TrueNorth AI setups",
        SavedOperatingIntent = true
    };

    private static SettingsProfile CompleteProfileWithPercent(decimal percent)
    {
        var profile = CompleteProfile();
        profile.Settings.MarginBalancePercent = percent;
        return profile;
    }

    private static bool ProfilesEqual(SettingsProfile expected, SettingsProfile actual)
    {
        var left = SettingsValidator.Validate(expected);
        var right = SettingsValidator.Validate(actual);
        return left.IsValid && right.IsValid
            && JsonSerializer.Serialize(left.Profile) == JsonSerializer.Serialize(right.Profile);
    }

    private static T Get<T>(MainWindow window, string name) where T : FrameworkElement =>
        (T)(window.FindName(name) ?? throw new InvalidOperationException($"WPF control not found: {name}"));

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void RenderWindow(Window window, string path)
    {
        RenderElement(window, path);
    }

    private static void RenderElement(FrameworkElement element, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);
        Check(new FileInfo(path).Length > 0, $"native WPF render written: {Path.GetFileName(path)}");
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
                return match;
            if (FindVisualChild<T>(child) is { } nested)
                return nested;
        }
        return null;
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    internal static string ArtifactsDirectory()
    {
        var configuredPath = Environment.GetEnvironmentVariable("TRUENORTH_TRADEFLOW_VERIFY_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "plans", "002-persistent-settings.md")))
                return Path.Combine(directory.FullName, "artifacts", "002-persistent-settings");
        }
        return Path.Combine(AppContext.BaseDirectory, "verification-artifacts");
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException(description);
        _checks++;
        Console.WriteLine($"PASS {_checks}: {description}");
    }
}
