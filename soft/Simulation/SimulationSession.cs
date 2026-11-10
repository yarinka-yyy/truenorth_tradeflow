using System.Collections.Immutable;

namespace TrueNorth.TradeFlow;

internal sealed record SimulationNotificationPreferences(
    bool Enabled,
    bool Opening,
    bool Closing,
    bool OrderChanges,
    bool Errors,
    bool Recovery,
    bool Reminders)
{
    internal static SimulationNotificationPreferences Capture(AppSettings settings) => new(
        settings.NotificationsEnabled,
        settings.OpeningNotifications,
        settings.ClosingNotifications,
        settings.OrderChangeNotifications,
        settings.ErrorNotifications,
        settings.RecoveryNotifications,
        settings.FailureRemindersEnabled);

    internal bool Allows(SimulationEvent entry)
    {
        if (!Enabled || entry.Category is null || (entry.IsReminder && !Reminders))
            return false;

        return entry.Category.Value switch
        {
            SimulationNoticeCategory.Opening => Opening,
            SimulationNoticeCategory.Closing => Closing,
            SimulationNoticeCategory.OrderChange => OrderChanges,
            SimulationNoticeCategory.Error => Errors,
            SimulationNoticeCategory.Recovery => Recovery,
            _ => false
        };
    }
}

internal sealed record SimulationSnapshot(
    string ScenarioId,
    string ScenarioName,
    string ScenarioDescription,
    bool Started,
    bool Complete,
    bool HasNext,
    int StepNumber,
    int StepCount,
    SimulationStep? CurrentStep,
    ImmutableArray<SimulationEvent> Events,
    ImmutableArray<SimulationNotification> Notifications,
    SimulationNotification? CurrentNotification);

internal sealed class SimulationSession
{
    private SimulationScenario _scenario;
    private ImmutableArray<SimulationEvent> _events = ImmutableArray<SimulationEvent>.Empty;
    private ImmutableArray<SimulationNotification> _notifications = ImmutableArray<SimulationNotification>.Empty;
    private SimulationNotificationPreferences? _preferences;
    private SimulationNotification? _currentNotification;
    private int _stepIndex = -1;
    private bool _started;

    internal SimulationSession(string scenarioId) => _scenario = SimulationScenario.Find(scenarioId);

    internal SimulationSnapshot Snapshot => new(
        _scenario.Id,
        _scenario.Name,
        _scenario.Description,
        _started,
        _started && _stepIndex == _scenario.Steps.Length - 1,
        _started && _stepIndex < _scenario.Steps.Length - 1,
        _started ? _stepIndex + 1 : 0,
        _scenario.Steps.Length,
        _started ? _scenario.Steps[_stepIndex] : null,
        _events,
        _notifications,
        _currentNotification);

    internal SimulationSnapshot SelectScenario(string scenarioId)
    {
        _scenario = SimulationScenario.Find(scenarioId);
        Reset();
        return Snapshot;
    }

    internal SimulationSnapshot Start(SimulationNotificationPreferences preferences)
    {
        if (_started)
            return Snapshot;

        _preferences = preferences with { };
        _started = true;
        _stepIndex = 0;
        ApplyCurrentStep();
        return Snapshot;
    }

    internal SimulationSnapshot Next()
    {
        if (!Snapshot.HasNext)
            return Snapshot;

        _stepIndex++;
        ApplyCurrentStep();
        return Snapshot;
    }

    internal SimulationSnapshot Reset()
    {
        _started = false;
        _stepIndex = -1;
        _preferences = null;
        _events = ImmutableArray<SimulationEvent>.Empty;
        _notifications = ImmutableArray<SimulationNotification>.Empty;
        _currentNotification = null;
        return Snapshot;
    }

    private void ApplyCurrentStep()
    {
        var entry = _scenario.Steps[_stepIndex].Event;
        _events = _events.Add(entry);
        _currentNotification = null;
        if (_preferences?.Allows(entry) != true)
            return;

        var notification = new SimulationNotification(
            entry.Time,
            CategoryName(entry.Category!.Value),
            entry.Title,
            entry.Description);
        _notifications = _notifications.Add(notification);
        _currentNotification = notification;
    }

    private static string CategoryName(SimulationNoticeCategory category) => category switch
    {
        SimulationNoticeCategory.Opening => "Открытие",
        SimulationNoticeCategory.Closing => "Закрытие",
        SimulationNoticeCategory.OrderChange => "Изменение заявки",
        SimulationNoticeCategory.Error => "Ошибка",
        SimulationNoticeCategory.Recovery => "Восстановление",
        _ => throw new ArgumentOutOfRangeException(nameof(category))
    };
}
