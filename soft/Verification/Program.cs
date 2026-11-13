using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TrueNorth.TradeFlow.Notifications;
using TrueNorth.TradeFlow.Recovery;
using TrueNorth.TradeFlow.Trading;

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
            CheckPlan012PersistentDataFixture(Check, fixtureRoot);
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
        Check(settings.CandidateSelectionMethod == "AssetOrder"
              && JsonSerializer.Deserialize<AppSettings>("{}")?.CandidateSelectionMethod == "AssetOrder",
            "profiles without the candidate selection field default to asset order");
        var oldProfilePath = Path.Combine(root, "candidate-selection-legacy", "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(oldProfilePath)!);
        var legacyOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        var legacyEnvelope = JsonSerializer.SerializeToNode(
            new SettingsEnvelope { Profile = CompleteProfile() }, legacyOptions)!;
        legacyEnvelope["profile"]!["settings"]!.AsObject().Remove("candidateSelectionMethod");
        File.WriteAllText(oldProfilePath, legacyEnvelope.ToJsonString(legacyOptions));
        var oldProfile = new SettingsStore(oldProfilePath).Load();
        Check(oldProfile.Status == SettingsFileStatus.Loaded
              && oldProfile.Profile.Settings.CandidateSelectionMethod == "AssetOrder",
            "a saved profile from before candidate ranking opens with the existing asset order");

        var rankingProfile = CompleteProfile();
        rankingProfile.Settings.CandidateSelectionMethod = "NetProfitAtTakeProfit";
        var rankingStore = new SettingsStore(Path.Combine(root, "candidate-selection-ranking", "settings.json"));
        var rankingSaved = rankingStore.Save(rankingProfile);
        var rankingReloaded = rankingStore.Load();
        Check(rankingSaved.Succeeded && rankingReloaded.Status == SettingsFileStatus.Loaded
              && rankingReloaded.Profile.Settings.CandidateSelectionMethod == "NetProfitAtTakeProfit",
            "the candidate selection setting survives the normal save and reload path");
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

        var badSelection = CompleteProfile();
        badSelection.Settings.CandidateSelectionMethod = "Unknown";
        Check(SettingsValidator.Validate(badSelection).Errors.ContainsKey(nameof(AppSettings.CandidateSelectionMethod)),
            "unknown candidate selection methods are rejected");

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
                var navigation = Get<ListBox>(restartedWindow, "NavigationList");
                restartedWindow.UpdateLayout();
                var scrollViewer = Get<ScrollViewer>(restartedWindow, "MainScrollViewer");
                PumpDispatcher();
                var modeText = Get<TextBlock>(restartedWindow, "CurrentModeText");
                var overviewIntent = Get<TextBlock>(restartedWindow, "OperatingIntentText");
                var sessionState = Get<TextBlock>(restartedWindow, "TrueNorthSessionStateText");
                var openSession = Get<Button>(restartedWindow, "OpenTrueNorthButton");
                var checkSession = Get<Button>(restartedWindow, "CheckTrueNorthSessionButton");
                scrollViewer.ScrollToVerticalOffset(0);
                Check(restartedWindow.ActualWidth is >= 759 and <= 761
                      && modeText.Text.Contains("TrueNorth AI setups", StringComparison.Ordinal)
                      && overviewIntent.Text.Contains("не запущен", StringComparison.Ordinal)
                      && sessionState.Text == "Статус входа неизвестен"
                      && openSession.IsVisible && checkSession.IsVisible
                      && IsInViewport(openSession, scrollViewer) && IsInViewport(checkSession, scrollViewer),
                    "reopened overview at 760x560 shows the saved mode, idle intent, unknown session and reachable actions");
                RenderWindow(restartedWindow, Path.Combine(ArtifactsDirectory(), "overview-reopened-760x560.png"));
                navigation.SelectedIndex = 1;
                restartedWindow.UpdateLayout();
                PumpDispatcher();
                var minimumAssets = Get<ListBox>(restartedWindow, "AssetsList");
                var firstMinimumAsset = minimumAssets.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem
                    ?? throw new InvalidOperationException("Minimum-width asset row was not generated.");
                var minimumAssetEditor = FindVisualChild<TextBox>(firstMinimumAsset)
                    ?? throw new InvalidOperationException("Minimum-width asset editor was not generated.");
                var reloadedAssets = ((System.Collections.ObjectModel.ObservableCollection<AssetDraft>)minimumAssets.ItemsSource!)
                    .Select(row => row.Text).ToArray();
                var reloadedEntryType = (Get<ComboBox>(restartedWindow, "EntryTypeComboBox").SelectedItem as ComboBoxItem)?.Tag as string;
                Check(reloadedAssets.SequenceEqual(["BTC", "ETHUSDT-PERP"])
                      && reloadedEntryType == "Market"
                      && Get<TextBox>(restartedWindow, "MarginPercentTextBox").Text == 42m.ToString(CultureInfo.CurrentCulture)
                      && Get<TextBox>(restartedWindow, "LeverageTextBox").Text == 3.25m.ToString(CultureInfo.CurrentCulture),
                    "reopened settings restore asset order, entry type, margin and leverage from the same profile");
                RenderWindow(restartedWindow, Path.Combine(ArtifactsDirectory(), "settings-window-minimum-top.png"));
                Check(minimumAssetEditor.ActualWidth > 100, "long asset text remains editable at the minimum window width");

                var saveButton = Get<Button>(restartedWindow, "SaveSettingsButton");
                var cancelButton = Get<Button>(restartedWindow, "CancelSettingsButton");
                var settingsStatus = Get<TextBlock>(restartedWindow, "SettingsStatusText");
                var saveFocusStep = -1;
                var cancelFocusStep = -1;
                Keyboard.Focus(minimumAssetEditor);
                for (var step = 0; step < 100 && cancelFocusStep < 0; step++)
                {
                    if (Keyboard.FocusedElement is not UIElement focused
                        || !focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                        break;
                    PumpDispatcher();
                    if (saveButton.IsKeyboardFocused && saveFocusStep < 0)
                        saveFocusStep = step;
                    if (cancelButton.IsKeyboardFocused)
                        cancelFocusStep = step;
                }
                Check(saveFocusStep >= 0 && cancelFocusStep > saveFocusStep
                      && cancelButton.IsKeyboardFocused && cancelButton.FocusVisualStyle is not null
                      && IsInViewport(saveButton, scrollViewer) && IsInViewport(cancelButton, scrollViewer)
                      && settingsStatus.Text.Contains("Настройки загружены", StringComparison.Ordinal)
                      && IsInViewport(settingsStatus, scrollViewer),
                    $"Tab from a settings field reaches Save then Cancel after scrolling at 760x560 "
                    + $"(save={saveFocusStep}, cancel={cancelFocusStep}, offset={scrollViewer.VerticalOffset:0.0}/{scrollViewer.ScrollableHeight:0.0})");
                RenderWindow(restartedWindow, Path.Combine(ArtifactsDirectory(), "settings-window-bottom.png"));

                scrollViewer.ScrollToVerticalOffset(scrollViewer.ScrollableHeight);
                restartedWindow.UpdateLayout();
                PumpDispatcher();
                Check(restartedWindow.ActualHeight is >= 559 and <= 561
                      && scrollViewer.ScrollableHeight > 0
                      && scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 1
                      && saveButton.ActualWidth > 0
                      && cancelButton.ActualWidth > 0,
                    "Save and Cancel remain laid out at the exact minimum window size");
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
            CheckCorruptSettingsUi(Check, root);
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

    private static void CheckCorruptSettingsUi(Action<bool, string> check, string root)
    {
        var path = Path.Combine(root, "wpf", "settings.json");
        var store = new SettingsStore(path);
        var backup = new SettingsStore(path + ".bak").Load();
        check(backup.Status == SettingsFileStatus.Loaded,
            "corrupt-settings UI fixture retains a readable last-known-good backup");

        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("{broken");
        File.WriteAllBytes(path, corruptBytes);
        var corruptLoad = store.Load();
        check(corruptLoad.Status == SettingsFileStatus.Corrupt && corruptLoad.CanRecover,
            "corrupt-settings UI fixture reports recoverable damaged input");

        using var controller = new WakeProtectionController(false, _ => true);
        var window = new MainWindow(store, corruptLoad, controller)
        {
            Width = 760,
            Height = 560
        };
        var scaledContent = window.Content as FrameworkElement
            ?? throw new InvalidOperationException("Corrupt-settings WPF root content was not generated.");
        try
        {
            window.Show();
            PumpDispatcher();
            var dpi = VisualTreeHelper.GetDpi(window);
            check(window.ActualWidth is >= 759 and <= 761 && window.ActualHeight is >= 559 and <= 561,
                $"corruption fixture renders the fixed 760x560 window (actual={window.ActualWidth:0.0}x{window.ActualHeight:0.0})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-corrupt-760x560.png"));

            Get<ListBox>(window, "NavigationList").SelectedIndex = 1;
            window.UpdateLayout();
            PumpDispatcher();
            var scroll = Get<ScrollViewer>(window, "MainScrollViewer");
            var status = Get<TextBlock>(window, "SettingsStatusText");
            var save = Get<Button>(window, "SaveSettingsButton");
            var cancel = Get<Button>(window, "CancelSettingsButton");
            var recover = Get<Button>(window, "RecoverSettingsButton");
            check(status.Text.Contains("Файл настроек повреждён", StringComparison.Ordinal)
                  && status.ActualHeight > 0 && recover.Visibility == Visibility.Visible
                  && recover.IsEnabled && AutomationProperties.GetName(recover).Length > 0,
                "settings UI explains corruption and exposes the named recovery action");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-corrupt-760x560.png"));

            var entryType = Get<ComboBox>(window, "EntryTypeComboBox");
            var saveStep = -1;
            var cancelStep = -1;
            var recoverStep = -1;
            Keyboard.Focus(entryType);
            for (var step = 0; step < 100 && recoverStep < 0; step++)
            {
                if (Keyboard.FocusedElement is not UIElement focused
                    || !focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                    break;
                PumpDispatcher();
                if (save.IsKeyboardFocused && saveStep < 0)
                    saveStep = step;
                if (cancel.IsKeyboardFocused && cancelStep < 0)
                    cancelStep = step;
                if (recover.IsKeyboardFocused)
                    recoverStep = step;
            }
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher();
            var statusBounds = GetViewportBounds(status, scroll);
            var recoverBounds = GetViewportBounds(recover, scroll);
            var viewportBounds = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
            check(saveStep >= 0 && cancelStep > saveStep && recoverStep > cancelStep,
                $"760x560 Tab path reaches Save, Cancel, then recovery "
                + $"(save={saveStep}, cancel={cancelStep}, recover={recoverStep})");
            check(recover.IsKeyboardFocused && recover.FocusVisualStyle is not null,
                $"760x560 fixture keeps visible keyboard focus on the recovery action "
                + $"(focused={recover.IsKeyboardFocused}, focus-style={recover.FocusVisualStyle is not null})");
            check(IsInViewport(status, scroll),
                $"760x560 fixture keeps the corruption status in the scroll viewport "
                + $"(status={statusBounds}, viewport={viewportBounds}, offset={scroll.VerticalOffset:0.0}/{scroll.ScrollableHeight:0.0})");
            check(IsInViewport(recover, scroll),
                $"760x560 fixture keeps the focused recovery action in the scroll viewport "
                + $"(recovery={recoverBounds}, viewport={viewportBounds}, offset={scroll.VerticalOffset:0.0}/{scroll.ScrollableHeight:0.0})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-corrupt-tab-recovery-760x560.png"));

            scaledContent.LayoutTransform = new ScaleTransform(1.25, 1.25);
            window.Width = 950;
            window.Height = 700;
            window.UpdateLayout();
            PumpDispatcher();
            Console.WriteLine($"WPF_FIXTURE_DPI={dpi.PixelsPerInchX:0}x{dpi.PixelsPerInchY:0} "
                + $"({dpi.DpiScaleX:0.00}x{dpi.DpiScaleY:0.00}); synthetic content LayoutTransform=1.25x, window=950x700");
            check(window.ActualWidth is >= 949 and <= 951 && window.ActualHeight is >= 699 and <= 701,
                $"125% synthetic fixture renders its 760x560 logical content at 950x700 "
                + $"(actual={window.ActualWidth:0.0}x{window.ActualHeight:0.0})");
            Get<ListBox>(window, "NavigationList").SelectedIndex = 0;
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "overview-corrupt-125pct-fixture-950x700.png"));
            Get<ListBox>(window, "NavigationList").SelectedIndex = 1;
            window.UpdateLayout();
            PumpDispatcher();
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-corrupt-125pct-fixture-950x700.png"));

            scroll.ScrollToVerticalOffset(0);
            window.UpdateLayout();
            PumpDispatcher();
            Keyboard.Focus(entryType);
            saveStep = -1;
            cancelStep = -1;
            recoverStep = -1;
            for (var step = 0; step < 100 && recoverStep < 0; step++)
            {
                if (Keyboard.FocusedElement is not UIElement focused
                    || !focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)))
                    break;
                PumpDispatcher();
                if (save.IsKeyboardFocused && saveStep < 0)
                    saveStep = step;
                if (cancel.IsKeyboardFocused && cancelStep < 0)
                    cancelStep = step;
                if (recover.IsKeyboardFocused)
                    recoverStep = step;
            }
            scroll.ScrollToVerticalOffset(scroll.ScrollableHeight);
            window.UpdateLayout();
            PumpDispatcher();
            statusBounds = GetViewportBounds(status, scroll);
            recoverBounds = GetViewportBounds(recover, scroll);
            viewportBounds = new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight);
            check(saveStep >= 0 && cancelStep > saveStep && recoverStep > cancelStep,
                $"125% synthetic fixture Tab path reaches Save, Cancel, then recovery "
                + $"(save={saveStep}, cancel={cancelStep}, recover={recoverStep})");
            check(recover.IsKeyboardFocused && recover.FocusVisualStyle is not null,
                $"125% synthetic fixture keeps visible keyboard focus on the recovery action "
                + $"(focused={recover.IsKeyboardFocused}, focus-style={recover.FocusVisualStyle is not null})");
            check(IsInViewport(status, scroll),
                $"125% synthetic fixture keeps the corruption status in the scroll viewport "
                + $"(status={statusBounds}, viewport={viewportBounds}, offset={scroll.VerticalOffset:0.0}/{scroll.ScrollableHeight:0.0})");
            check(IsInViewport(recover, scroll),
                $"125% synthetic fixture keeps the focused recovery action in the scroll viewport "
                + $"(recovery={recoverBounds}, viewport={viewportBounds}, offset={scroll.VerticalOffset:0.0}/{scroll.ScrollableHeight:0.0})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-corrupt-tab-recovery-125pct-fixture-950x700.png"));

            Click(recover);
            PumpDispatcher();
            var recovered = store.Load();
            var archives = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt.*");
            check(status.Text.Contains("Последняя исправная копия восстановлена", StringComparison.Ordinal)
                  && recover.Visibility == Visibility.Collapsed
                  && ProfilesEqual(backup.Profile, recovered.Profile)
                  && archives.Length == 1 && File.ReadAllBytes(archives[0]).SequenceEqual(corruptBytes),
                $"UI recovery restores the verified backup and archives the corrupt bytes unchanged "
                + $"(status={recovered.Status}, archives={archives.Length}, source={path})");
            RenderWindow(window, Path.Combine(ArtifactsDirectory(), "settings-recovered-125pct-fixture-950x700.png"));
        }
        finally
        {
            window.AllowApplicationClose();
            window.Close();
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

    private static void CheckPlan012PersistentDataFixture(Action<bool, string> check, string root)
    {
        var profileRoot = Path.Combine(root, "plan-012-profile-copy");
        var settingsPath = Path.Combine(profileRoot, "settings.json");
        Directory.CreateDirectory(Path.Combine(profileRoot, "TestReports"));
        Directory.CreateDirectory(Path.Combine(profileRoot, "Browser", "Edge"));
        File.WriteAllText(Path.Combine(profileRoot, "TestReports", "fixture.txt"), "synthetic test report");
        File.WriteAllText(Path.Combine(profileRoot, "Browser", "Edge", "fixture.txt"), "synthetic browser profile marker");

        var settings = new SettingsStore(settingsPath);
        var firstSave = settings.Save(CompleteProfile());
        var secondSave = settings.Save(CompleteProfileWithPercent(13m));
        var journal = new RecoveryJournalStore(Path.Combine(profileRoot, "recovery-state.json"));
        var journalFirstSave = journal.Save(new RecoveryJournalState());
        var journalSecondSave = journal.Save(new RecoveryJournalState());
        var restartAttempt = new ProcessRestartBudgetStore(Path.Combine(profileRoot, "process-recovery-state.json"))
            .TryBeginAttempt(ProcessRecoveryKind.Application);
        var credentials = new TelegramCredentialStore(Path.Combine(profileRoot, "telegram-credentials.dat"));
        var credentialsSaved = credentials.SavePair("123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef", "123456789", enabled: false);
        using var scenario = new LocalTradingScenario(LocalTradingScenario.DefaultDirectory(profileRoot),
            CompleteProfile(), automaticScheduling: false);

        var filesBefore = HashDirectory(profileRoot);
        var prepared = scenario.PrepareForPlannedUpdateAsync().GetAwaiter().GetResult();
        var filesAfter = HashDirectory(profileRoot);
        var credentialsReloaded = new TelegramCredentialStore(Path.Combine(profileRoot, "telegram-credentials.dat"))
            .TryLoad(out var savedCredentials, out var enabled);
        check(firstSave.Succeeded && secondSave.Succeeded && File.Exists(settingsPath + ".bak")
              && journalFirstSave.Saved && journalSecondSave.Saved && File.Exists(journal.PreviousVersionPath)
              && restartAttempt.Kind == ProcessRestartDecisionKind.Started
              && credentialsSaved && credentialsReloaded && savedCredentials is not null && !enabled
              && prepared.Succeeded && filesBefore.SequenceEqual(filesAfter),
            $"offline update preparation preserves settings and backup, recovery journals, process state, DPAPI credentials, Edge marker, TestReports, and local-trading-scenario ({filesBefore.Count} files)");
    }

    private static IReadOnlyList<(string Path, string Hash)> HashDirectory(string root) =>
        Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => (Path.GetRelativePath(root, path), Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))))
            .OrderBy(file => file.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(file => (file.Item1, file.Item2))
            .ToArray();

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

    private static bool IsInViewport(FrameworkElement element, ScrollViewer scroll)
    {
        var bounds = GetViewportBounds(element, scroll);
        return new Rect(0, 0, scroll.ViewportWidth, scroll.ViewportHeight)
            .Contains(bounds);
    }

    private static Rect GetViewportBounds(FrameworkElement element, ScrollViewer scroll) =>
        element.TransformToAncestor(scroll).TransformBounds(
            new Rect(new Point(0, 0), new Size(element.ActualWidth, element.ActualHeight)));

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
