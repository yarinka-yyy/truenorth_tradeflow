using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using FrameworkElement = System.Windows.FrameworkElement;
using TrueNorth.TradeFlow.Risk;

namespace TrueNorth.TradeFlow.Account;

public partial class AccountDashboardView : UserControl
{
    private EntryEligibilityResult? _entryEligibilityResult;
    private AccountOrderLifecycleExample? _orderLifecycleExample;

    public event EventHandler? ShowExampleRequested;
    public event EventHandler? ExitExampleRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? ContinueWithOwnerParametersRequested;
    public event EventHandler? TransferToManualRequested;

    public AccountDashboardView() => InitializeComponent();

    internal bool IsShowingExample => ExamplePanel.Visibility == Visibility.Visible;
    internal Button ExampleButton => ShowExampleButton;
    internal Button OpenHistoryButton => HistoryButton;
    internal Button ExitButton => ExitExampleButton;
    internal Button ContinueOwnerParametersButton => ContinueOwnerParametersAction;
    internal Button TransferToManualButton => TransferToManualAction;
    internal TextBlock OrderLifecycleDecisionStatusElement => OrderLifecycleDecisionStatusText;
    internal FrameworkElement OrderLifecyclePanelElement => OrderLifecyclePanel;
    internal TextBlock OrderLifecycleOrderElement => OrderLifecycleOrderText;
    internal TextBlock OrderLifecycleQuantitiesElement => OrderLifecycleQuantitiesText;
    internal TextBlock OrderLifecycleDeadlineElement => OrderLifecycleDeadlineText;
    internal TextBlock OrderLifecycleProtectionElement => OrderLifecycleProtectionText;
    internal TextBlock OrderLifecycleOwnerChangeElement => OrderLifecycleOwnerChangeText;
    internal FrameworkElement EligibilityPanelElement => EntryEligibilityPanel;
    internal TextBlock EligibilityStatusElement => EntryEligibilityStatusText;
    internal TextBlock EligibilitySummaryElement => EntryEligibilitySummaryText;
    internal TextBlock EligibilityTimesElement => EntryEligibilityTimesText;
    internal TextBlock EligibilityVenueReasonsElement => EntryEligibilityVenueReasonsText;
    internal EntryEligibilityResult? EligibilityResult => _entryEligibilityResult;
    internal IReadOnlyList<AccountVenueCard>? DisplayedSnapshotCards => VenueCards.ItemsSource as IReadOnlyList<AccountVenueCard>;
    internal string EligibilityDetails => string.Join("\n", new[]
    {
        EntryEligibilityMarketText.Text,
        EntryEligibilitySizingText.Text,
        EntryEligibilityMarginText.Text,
        EntryEligibilityStressText.Text,
        EntryEligibilityCostsText.Text
    }.Where(value => !string.IsNullOrWhiteSpace(value)));

    internal void SetSnapshots(IReadOnlyList<AccountSnapshot>? snapshots)
    {
        var showingExample = snapshots is not null;
        NoDataPanel.Visibility = showingExample ? Visibility.Collapsed : Visibility.Visible;
        ExamplePanel.Visibility = showingExample ? Visibility.Visible : Visibility.Collapsed;
        VenueCards.ItemsSource = snapshots?.Select(AccountPresentation.CreateCard).ToArray();
        RefreshOrderLifecycleExample();
        RefreshEntryEligibility();
    }

    internal void SetEntryEligibilityResult(EntryEligibilityResult? result)
    {
        _entryEligibilityResult = result;
        RefreshEntryEligibility();
    }

    internal void SetOrderLifecycleExample(AccountOrderLifecycleExample? example)
    {
        _orderLifecycleExample = example;
        RefreshOrderLifecycleExample();
    }

    private void RefreshOrderLifecycleExample()
    {
        var example = _orderLifecycleExample;
        var visible = example is not null && IsShowingExample;
        OrderLifecyclePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || example is null)
            return;
        OrderLifecycleOrderText.Text = example.Order;
        OrderLifecycleQuantitiesText.Text = example.Quantities;
        OrderLifecycleDeadlineText.Text = example.FirstFillDeadline;
        OrderLifecycleProtectionText.Text = example.Protection;
        OrderLifecycleOwnerChangeText.Text = example.OwnerChange;
        OrderLifecycleDecisionStatusText.Text = example.DecisionResult;
        AutomationProperties.SetName(OrderLifecycleDecisionStatusText, example.DecisionResult);
        ContinueOwnerParametersAction.IsEnabled = example.CanChooseOwner;
        TransferToManualAction.IsEnabled = example.CanChooseOwner;
    }

    private void RefreshEntryEligibility()
    {
        var result = _entryEligibilityResult;
        var visible = result is not null && IsShowingExample;
        EntryEligibilityPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible || result is null)
        {
            EntryEligibilityStatusText.Text = string.Empty;
            EntryEligibilitySummaryText.Text = string.Empty;
            EntryEligibilityMarketText.Text = string.Empty;
            EntryEligibilitySizingText.Text = string.Empty;
            EntryEligibilityMarginText.Text = string.Empty;
            EntryEligibilityStressText.Text = string.Empty;
            EntryEligibilityCostsText.Text = string.Empty;
            EntryEligibilityTimesText.Text = string.Empty;
            EntryEligibilityVenueReasonsText.Text = string.Empty;
            return;
        }

        EntryEligibilityStatusText.Text = StatusText(result.Status);
        EntryEligibilityStatusText.Foreground = result.Status == EntryEligibilityStatus.ScenarioReady
            ? (System.Windows.Media.Brush)FindResource("AccentBrush")
            : (System.Windows.Media.Brush)FindResource("WarningBrush");
        EntryEligibilitySummaryText.Text = result.Summary;
        AutomationProperties.SetName(EntryEligibilityPanel,
            $"Отдельный синтетический сценарий. {EntryEligibilityStatusText.Text}. {result.Summary}");

        var selected = result.SelectedPool ?? result.Pools
            .OrderByDescending(pool => pool.FullBalance)
            .FirstOrDefault();
        if (selected is not null)
        {
            var quote = selected.QuoteCurrency ?? "валюта неизвестна";
            var asset = result.SetupAsset ?? "актив неизвестен";
            EntryEligibilityMarketText.Text =
                $"{selected.Venue} · {selected.Account} · {selected.CollateralPool} · {selected.Contract} → {result.SetupMarket}";
            EntryEligibilitySizingText.Text =
                $"{result.SetupEntryType} {FormatNumber(selected.EntryPrice)} {quote}/{asset} · "
                + $"{DirectionText(result.SetupDirection)} {FormatNumber(selected.Quantity)} {selected.QuantityUnit} "
                + $"({FormatNumber(selected.BaseQuantity)} {selected.BaseQuantityUnit}) · "
                + $"{FormatNumber(selected.Leverage)}× · номинал {FormatNumber(selected.Notional)} {quote}";
            EntryEligibilityMarginText.Text =
                $"Баланс {FormatNumber(selected.FullBalance)} · база размера {FormatNumber(selected.SizingBalance)} ({FormatNumber(selected.MarginBalancePercent)}%) · "
                + $"маржа: доступно {FormatNumber(selected.AvailableMargin)}, начальная {FormatNumber(selected.InitialMargin)}, требуется {FormatNumber(selected.RequiredMargin)} {quote}";
            EntryEligibilityStressText.Text =
                $"Стресс: equity {FormatNumber(selected.StressEquity)} · maintenance {FormatNumber(selected.Maintenance)} · резерв {FormatNumber(selected.StressReserve)} · "
                + $"запас тестового сценария {FormatNumber(selected.AdditionalHeadroom)} · остаток {FormatNumber(selected.ReserveAfterHeadroom)} {quote}";
            EntryEligibilityCostsText.Text =
                $"Комиссия нового входа (для маржи) {FormatNumber(selected.IncrementalCandidateEntryFee)} {quote} · "
                + $"комиссии входов в полном стрессе (всего) {FormatNumber(selected.TotalStressEntryFees)} {quote} · "
                + $"выход по рынку {FormatNumber(selected.ExitFees)} {quote} · неблагоприятный funding за 24 ч {FormatNumber(selected.AdverseFunding)} {quote}";
            var evidenceSources = new[]
            {
                selected.AccountSource,
                result.SetupSource,
                selected.EntryPriceSource,
                selected.MarketBindingSource,
                selected.RulesSource
            }.Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            EntryEligibilityTimesText.Text =
                $"UTC: оценка {FormatTime(result.EvaluatedAt)} · счёт {FormatEvidenceTime(selected.AccountObservedAt, result.EvaluatedAt)} · "
                + $"сетап {FormatEvidenceTime(result.SetupObservedAt, result.EvaluatedAt)} · цена {FormatEvidenceTime(selected.EntryPriceObservedAt, result.EvaluatedAt)} · "
                + $"рынок {FormatEvidenceTime(selected.MarketBindingObservedAt, result.EvaluatedAt)} · правила {FormatEvidenceTime(selected.RulesObservedAt, result.EvaluatedAt)}"
                + (evidenceSources.Length == 0 ? string.Empty : $" · источники: {string.Join(", ", evidenceSources)}");
        }

        EntryEligibilityVenueReasonsText.Text = string.Join("\n", result.Pools.Select(pool =>
            $"{pool.Venue}: {StatusText(pool.Status)}"
            + (pool.Reasons.Count == 0 ? string.Empty : $" · {string.Join(" ", pool.Reasons)}")));
    }

    private static string StatusText(EntryEligibilityStatus status) => status switch
    {
        EntryEligibilityStatus.ScenarioReady => "условия сценария выполнены",
        EntryEligibilityStatus.Waiting => "ожидание",
        _ => "недостаточно данных"
    };

    private static string DirectionText(PositionDirection? direction) => direction switch
    {
        PositionDirection.Long => "Long",
        PositionDirection.Short => "Short",
        _ => "направление неизвестно"
    };

    private static string FormatNumber(decimal? value) => value?.ToString("0.############################", CultureInfo.CurrentCulture) ?? "неизвестно";

    private static string FormatTime(DateTimeOffset? value) => value?.ToUniversalTime()
        .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "время неизвестно";

    private static string FormatEvidenceTime(DateTimeOffset? value, DateTimeOffset evaluatedAt)
    {
        if (value is null)
            return "неизвестно";
        var utc = value.Value.ToUniversalTime();
        return utc.Date == evaluatedAt.ToUniversalTime().Date
            ? utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private void ShowExample_Click(object sender, RoutedEventArgs e) => ShowExampleRequested?.Invoke(this, EventArgs.Empty);
    private void ExitExample_Click(object sender, RoutedEventArgs e) => ExitExampleRequested?.Invoke(this, EventArgs.Empty);
    private void History_Click(object sender, RoutedEventArgs e) => HistoryRequested?.Invoke(this, EventArgs.Empty);
    private void ContinueOwnerParameters_Click(object sender, RoutedEventArgs e) =>
        ContinueWithOwnerParametersRequested?.Invoke(this, EventArgs.Empty);
    private void TransferToManual_Click(object sender, RoutedEventArgs e) =>
        TransferToManualRequested?.Invoke(this, EventArgs.Empty);
}
