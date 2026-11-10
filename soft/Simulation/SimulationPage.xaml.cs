using System.Windows;
using System.Windows.Controls;

namespace TrueNorth.TradeFlow;

public partial class SimulationPage : System.Windows.Controls.UserControl
{
    private readonly SimulationSession _session;
    private bool _updatingScenario;

    public SimulationPage()
    {
        InitializeComponent();
        _session = new SimulationSession(SimulationScenario.All[0].Id);
        ScenarioSelector.ItemsSource = SimulationScenario.All;
        ScenarioSelector.DisplayMemberPath = nameof(SimulationScenario.Name);
        ScenarioSelector.SelectedValuePath = nameof(SimulationScenario.Id);
        _updatingScenario = true;
        ScenarioSelector.SelectedValue = _session.Snapshot.ScenarioId;
        _updatingScenario = false;
        Render(_session.Snapshot);
    }

    internal event EventHandler? StartRequested;

    internal void Start(SimulationNotificationPreferences preferences) =>
        Render(_session.Start(preferences));

    private void ScenarioSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingScenario || ScenarioSelector.SelectedItem is not SimulationScenario scenario)
            return;

        Render(_session.SelectScenario(scenario.Id));
    }

    private void Start_Click(object sender, RoutedEventArgs e) =>
        StartRequested?.Invoke(this, EventArgs.Empty);

    private void Next_Click(object sender, RoutedEventArgs e) => Render(_session.Next());

    private void Reset_Click(object sender, RoutedEventArgs e) => Render(_session.Reset());

    private void Render(SimulationSnapshot snapshot)
    {
        ScenarioDescriptionText.Text = snapshot.ScenarioDescription;
        StepCountText.Text = snapshot.Started
            ? $"Шаг {snapshot.StepNumber} из {snapshot.StepCount} · {snapshot.CurrentStep!.Time}"
            : "Сценарий не запущен";
        StepStatusText.Text = snapshot.CurrentStep?.Status ?? "Ожидание запуска";
        StepTitleText.Text = snapshot.CurrentStep?.Title ?? "Ожидание запуска";
        StepDescriptionText.Text = snapshot.CurrentStep?.Description
            ?? "Состояние появится после запуска выбранного сценария.";
        NextActionText.Text = snapshot.CurrentStep?.NextAction
            ?? "Нажмите «Начать», чтобы показать подготовленную последовательность.";

        var step = snapshot.CurrentStep;
        VenuesItems.ItemsSource = step?.Venues;
        CandidatesItems.ItemsSource = step?.Candidates;
        OrdersItems.ItemsSource = step?.Orders;
        OpenPositionsItems.ItemsSource = step?.OpenPositions;
        ClosedPositionsItems.ItemsSource = step?.ClosedPositions;
        EventsItems.ItemsSource = snapshot.Events;
        var previousNotifications = snapshot.CurrentNotification is null
            ? snapshot.Notifications
            : snapshot.Notifications.RemoveAt(snapshot.Notifications.Length - 1);
        NotificationsItems.ItemsSource = previousNotifications;
        VenuesEmptyText.Visibility = IsEmpty(step?.Venues.Length) ? Visibility.Visible : Visibility.Collapsed;
        CandidatesEmptyText.Visibility = IsEmpty(step?.Candidates.Length) ? Visibility.Visible : Visibility.Collapsed;
        OrdersEmptyText.Visibility = IsEmpty(step?.Orders.Length) ? Visibility.Visible : Visibility.Collapsed;
        OpenPositionsEmptyText.Visibility = IsEmpty(step?.OpenPositions.Length) ? Visibility.Visible : Visibility.Collapsed;
        ClosedPositionsEmptyText.Visibility = IsEmpty(step?.ClosedPositions.Length) ? Visibility.Visible : Visibility.Collapsed;
        EventsEmptyText.Visibility = snapshot.Events.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
        NotificationsEmptyText.Visibility = previousNotifications.IsEmpty ? Visibility.Visible : Visibility.Collapsed;

        NotificationEmptyText.Visibility = snapshot.CurrentNotification is null
            ? Visibility.Visible
            : Visibility.Collapsed;
        NotificationPreviewPanel.Visibility = snapshot.CurrentNotification is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (snapshot.CurrentNotification is { } notification)
        {
            NotificationCategoryText.Text = notification.Category;
            NotificationTimeText.Text = notification.Time;
            NotificationTitleText.Text = notification.Title;
            NotificationMessageText.Text = notification.Message;
        }

        StartButton.IsEnabled = !snapshot.Started;
        NextButton.IsEnabled = snapshot.HasNext;
        ResetButton.IsEnabled = snapshot.Started;
        StartButton.SetResourceReference(StyleProperty, snapshot.Started ? "SimulationSecondaryAction" : "PrimaryAction");
        NextButton.SetResourceReference(StyleProperty, snapshot.HasNext ? "PrimaryAction" : "SimulationSecondaryAction");
        ResetButton.SetResourceReference(StyleProperty, snapshot.Complete ? "PrimaryAction" : "SimulationSecondaryAction");
    }

    private static bool IsEmpty(int? count) => count.GetValueOrDefault() == 0;
}
