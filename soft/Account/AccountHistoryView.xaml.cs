using System.Windows;
using UserControl = System.Windows.Controls.UserControl;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using DatePicker = System.Windows.Controls.DatePicker;
using ItemsControl = System.Windows.Controls.ItemsControl;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;

namespace TrueNorth.TradeFlow.Account;

public partial class AccountHistoryView : UserControl
{
    private IReadOnlyList<AccountSnapshot>? _snapshots;
    private IReadOnlyList<ClosedTradeRow> _allRows = [];
    private bool _updatingFilters;

    public event EventHandler? ShowExampleRequested;
    public event EventHandler? ExitExampleRequested;
    public event EventHandler? BackToOverviewRequested;

    public AccountHistoryView() => InitializeComponent();

    internal bool IsShowingExample => ExamplePanel.Visibility == Visibility.Visible;
    internal Button ExampleButton => ShowExampleButton;
    internal ComboBox VenueFilter => HistoryVenueFilter;
    internal DatePicker FromFilter => ClosedFromFilter;
    internal DatePicker ToFilter => ClosedToFilter;
    internal Button ResetButton => ResetFiltersButton;
    internal Button BackButton => BackToOverviewButton;
    internal Button ExitButton => ExitExampleButton;
    internal ItemsControl Rows => TradeRows;
    internal string FilterSummary => FilterSummaryText.Text;
    internal string EmptyMessage => EmptyFilteredText.Text;
    internal bool NoFilteredRowsVisible => EmptyFilteredText.Visibility == Visibility.Visible;
    internal int TradeCount => TradeRows.Items.Count;

    internal void SetSnapshots(IReadOnlyList<AccountSnapshot>? snapshots)
    {
        _snapshots = snapshots;
        NoDataPanel.Visibility = snapshots is null ? Visibility.Visible : Visibility.Collapsed;
        ExamplePanel.Visibility = snapshots is null ? Visibility.Collapsed : Visibility.Visible;
        _allRows = snapshots is null ? [] : AccountPresentation.CreateHistoryRows(snapshots);

        _updatingFilters = true;
        try
        {
            HistoryVenueFilter.Items.Clear();
            HistoryVenueFilter.Items.Add(new ComboBoxItem { Content = "Все площадки", Tag = null });
            if (snapshots is not null)
            {
                foreach (var venue in snapshots.Select(snapshot => snapshot.Venue).Distinct(StringComparer.Ordinal))
                    HistoryVenueFilter.Items.Add(new ComboBoxItem { Content = venue, Tag = venue });
            }
            HistoryVenueFilter.SelectedIndex = snapshots is null ? -1 : 0;
            ClosedFromFilter.SelectedDate = null;
            ClosedToFilter.SelectedDate = null;
        }
        finally
        {
            _updatingFilters = false;
        }

        RefreshHistory();
    }

    private void ShowExample_Click(object sender, RoutedEventArgs e) => ShowExampleRequested?.Invoke(this, EventArgs.Empty);
    private void ExitExample_Click(object sender, RoutedEventArgs e) => ExitExampleRequested?.Invoke(this, EventArgs.Empty);
    private void BackToOverview_Click(object sender, RoutedEventArgs e) => BackToOverviewRequested?.Invoke(this, EventArgs.Empty);

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingFilters)
            RefreshHistory();
    }

    private void ResetFilters_Click(object sender, RoutedEventArgs e)
    {
        _updatingFilters = true;
        try
        {
            HistoryVenueFilter.SelectedIndex = 0;
            ClosedFromFilter.SelectedDate = null;
            ClosedToFilter.SelectedDate = null;
        }
        finally
        {
            _updatingFilters = false;
        }
        RefreshHistory();
    }

    private void RefreshHistory()
    {
        if (_snapshots is null)
        {
            TradeRows.ItemsSource = null;
            FilterSummaryText.Text = string.Empty;
            EmptyFilteredText.Text = string.Empty;
            EmptyFilteredText.Visibility = Visibility.Collapsed;
            return;
        }

        var venue = (HistoryVenueFilter.SelectedItem as ComboBoxItem)?.Tag as string;
        var selectedSnapshots = _snapshots
            .Where(snapshot => venue is null || snapshot.Venue == venue)
            .ToArray();
        var coverage = AccountPresentation.CreateHistoryCoverage(selectedSnapshots);
        var coverageSummary = coverage.Length == 0 ? string.Empty : $"Покрытие: {coverage}";
        var from = ClosedFromFilter.SelectedDate is { } fromDate
            ? DateOnly.FromDateTime(fromDate.Date)
            : (DateOnly?)null;
        var to = ClosedToFilter.SelectedDate is { } toDate
            ? DateOnly.FromDateTime(toDate.Date)
            : (DateOnly?)null;

        if (from is not null && to is not null && from > to)
        {
            TradeRows.ItemsSource = null;
            EmptyFilteredText.Text = string.Empty;
            EmptyFilteredText.Visibility = Visibility.Collapsed;
            FilterSummaryText.Text = JoinSummary(coverageSummary, "Начало периода закрытия позже его конца.");
            return;
        }

        var filtered = _allRows.Where(row =>
        {
            var closedDate = DateOnly.FromDateTime(row.ClosedAtValue.ToLocalTime().Date);
            return (venue is null || row.Venue == venue)
                && (from is null || closedDate >= from)
                && (to is null || closedDate <= to);
        }).ToArray();

        TradeRows.ItemsSource = filtered;
        EmptyFilteredText.Text = AccountPresentation.CreateHistoryNoRowsMessage(selectedSnapshots);
        EmptyFilteredText.Visibility = filtered.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        FilterSummaryText.Text = JoinSummary(coverageSummary,
            $"Показано сделок: {filtered.Length}. Валюты не складываются.");
    }

    private static string JoinSummary(params string[] parts) =>
        string.Join(Environment.NewLine, parts.Where(part => !string.IsNullOrWhiteSpace(part)));
}
