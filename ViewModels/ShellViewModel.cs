using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShockUI;
using ShockUI.Models.App;
using ShockUI.Services.App;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ShockUI.ViewModels;

public sealed partial class ShellViewModel : ViewModelBase
{
    private readonly INavigationService _navigationService;
    private ViewModelBase? _currentModule;

    // -----------------------------------------------------------------------
    // Navigation
    // -----------------------------------------------------------------------

    public ObservableCollection<NavigationItem> NavigationItems => _navigationService.Items;

    /// <summary>Top-level items — always visible (e.g. System Controller).</summary>
    public IEnumerable<NavigationItem> TopLevelItems
        => _navigationService.Items.Where(x => !x.IsEngineering);

    /// <summary>Engineering items — visible only when section is unlocked.</summary>
    public IEnumerable<NavigationItem> EngineeringItems
        => _navigationService.Items.Where(x => x.IsEngineering);

    public ViewModelBase? CurrentModule
    {
        get => _currentModule;
        private set => SetProperty(ref _currentModule, value);
    }

    // -----------------------------------------------------------------------
    // Engineering lock
    // -----------------------------------------------------------------------

    // Change this password to whatever is appropriate. 
    // Could be moved to AppSettings for configurability.
    private const string EngineeringPin = "SHOCK";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LockButtonText))]
    [NotifyPropertyChangedFor(nameof(EngineeringStatusText))]
    private bool isEngineeringUnlocked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordError))]
    private string passwordEntry = string.Empty;

    [ObservableProperty]
    private bool passwordError;

    /// <summary>
    /// When true the left sidebar collapses to a thin rail showing
    /// only the toggle chevron, freeing horizontal space for the
    /// active module's cards. Toggled via <see cref="ToggleSidebarCommand"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidebarWidth))]
    [NotifyPropertyChangedFor(nameof(SidebarToggleGlyph))]
    [NotifyPropertyChangedFor(nameof(SidebarToggleTooltip))]
    private bool isSidebarCollapsed = true;   // start minimised; user expands via the chevron

    public string LockButtonText => IsEngineeringUnlocked ? "Lock" : "Unlock";
    public string EngineeringStatusText => IsEngineeringUnlocked ? "Unlocked" : "Password required";

    /// <summary>Pixel width for the sidebar Border. 48 collapsed, 250 expanded.</summary>
    public double SidebarWidth => IsSidebarCollapsed ? 48.0 : 250.0;

    /// <summary>Chevron text for the toggle button — points the direction the click will move the sidebar.</summary>
    public string SidebarToggleGlyph => IsSidebarCollapsed ? "›" : "‹";

    public string SidebarToggleTooltip => IsSidebarCollapsed ? "Expand menu" : "Collapse menu";

    // -----------------------------------------------------------------------
    // Ctor
    // -----------------------------------------------------------------------

    public ShellViewModel(INavigationService navigationService)
    {
        _navigationService = navigationService;
        _currentModule = _navigationService.CurrentViewModel;
        _navigationService.CurrentViewModelChanged += OnCurrentViewModelChanged;
    }

    // -----------------------------------------------------------------------
    // Commands
    // -----------------------------------------------------------------------

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void Navigate(NavigationItem? item)
    {
        if (item is null) return;

        // Block navigation to engineering modules if locked
        if (item.IsEngineering && !IsEngineeringUnlocked)
            return;

        _navigationService.NavigateTo(item.ViewModel);
    }

    [RelayCommand]
    private void SubmitPassword()
    {
        if (PasswordEntry == EngineeringPin)
        {
            IsEngineeringUnlocked = true;
            AppState.Current.IsMaintenanceUnlocked = true;
            PasswordEntry = string.Empty;
            PasswordError = false;
        }
        else
        {
            PasswordError = true;
            PasswordEntry = string.Empty;
        }
    }

    [RelayCommand]
    private void LockEngineering()
    {
        IsEngineeringUnlocked = false;
        AppState.Current.IsMaintenanceUnlocked = false;
        PasswordEntry = string.Empty;
        PasswordError = false;

        // If currently showing an engineering module, navigate away
        if (_currentModule is not null)
        {
            var current = _navigationService.Items
                .FirstOrDefault(x => ReferenceEquals(x.ViewModel, _currentModule));

            if (current?.IsEngineering == true)
            {
                var topLevel = TopLevelItems.FirstOrDefault();
                if (topLevel is not null)
                    _navigationService.NavigateTo(topLevel.ViewModel);
            }
        }
    }

    private void OnCurrentViewModelChanged(ViewModelBase? viewModel)
    {
        CurrentModule = viewModel;
    }

    partial void OnPasswordEntryChanged(string value)
    {
        // Clear error state as soon as user starts typing again
        if (PasswordError && value.Length > 0)
            PasswordError = false;
    }
}