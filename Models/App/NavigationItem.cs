using ShockUI.ViewModels;

namespace ShockUI.Models.App;

public sealed class NavigationItem
{
    public required string Label { get; init; }
    public required ViewModelBase ViewModel { get; init; }

    /// <summary>
    /// When true this item is grouped under the password-protected Engineering section.
    /// When false it appears at the top level (always visible).
    /// </summary>
    public bool IsEngineering { get; init; }
}
