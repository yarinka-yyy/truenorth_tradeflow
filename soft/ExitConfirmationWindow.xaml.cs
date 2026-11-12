using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using TrueNorth.TradeFlow.Trading;

namespace TrueNorth.TradeFlow;

internal enum ExitConfirmationAction
{
    None,
    Return,
    Leave,
    CancelEntries
}

public partial class ExitConfirmationWindow : Window
{
    internal ExitConfirmationWindow(LocalScenarioExitSnapshot snapshot)
    {
        InitializeComponent();
        UpdateSnapshot(snapshot);
    }

    internal ExitConfirmationAction Action { get; private set; }

    internal void UpdateSnapshot(LocalScenarioExitSnapshot snapshot)
    {
        ExitEntriesText.Text = snapshot.IsKnown
            ? snapshot.OpenEntries.Count == 0
                ? "Подтверждено открытых программных заявок: 0."
                : $"Подтверждено открытых программных заявок: {snapshot.OpenEntries.Count}."
            : "Число открытых программных заявок неизвестно.";
        ExitStatusText.Text = snapshot.Status;
        ExitResultsText.Text = string.Join(Environment.NewLine, snapshot.CancellationResults);
        ExitResultsText.Visibility = snapshot.CancellationResults.Count == 0
            ? Visibility.Collapsed : Visibility.Visible;
        CancelEntriesButton.Visibility = snapshot.IsKnown && snapshot.OpenEntries.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        LeaveButton.Content = snapshot.IsKnown && snapshot.OpenEntries.Count == 0
            ? "Выйти" : "Выйти, оставив заявки";
        AutomationProperties.SetName(LeaveButton, snapshot.IsKnown && snapshot.OpenEntries.Count == 0
            ? "Завершить работу" : "Оставить заявки и завершить работу");
    }

    private void Return_Click(object sender, RoutedEventArgs e) => Complete(ExitConfirmationAction.Return);

    private void Leave_Click(object sender, RoutedEventArgs e) => Complete(ExitConfirmationAction.Leave);

    private void CancelEntries_Click(object sender, RoutedEventArgs e) =>
        Complete(ExitConfirmationAction.CancelEntries);

    private void Complete(ExitConfirmationAction action)
    {
        Action = action;
        DialogResult = true;
    }
}
