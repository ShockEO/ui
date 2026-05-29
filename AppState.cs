using CommunityToolkit.Mvvm.ComponentModel;

namespace ShockUI;

/// <summary>
/// Global app-wide state shared across view models.
/// Singleton accessed via <see cref="Current"/>.
///
/// Currently tracks only the Maintenance unlock state, which gates access
/// to privileged UI (fault injection, engineering commands, etc.).
/// </summary>
public sealed partial class AppState : ObservableObject
{
    public static AppState Current { get; } = new();

    private AppState() { }

    /// <summary>
    /// True when the Maintenance section in the sidebar has been unlocked via password.
    /// UI elements that should only appear for maintenance engineers bind to this
    /// (typically combined with IsSimulationMode for fault-injection panels).
    /// </summary>
    [ObservableProperty] private bool isMaintenanceUnlocked;
}