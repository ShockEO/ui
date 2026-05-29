using System;
using System.Collections.ObjectModel;
using System.Linq;
using ShockUI.Models.App;
using ShockUI.ViewModels;

namespace ShockUI.Services.App;

public sealed class NavigationService : INavigationService
{
    public ObservableCollection<NavigationItem> Items { get; } = [];

    public ViewModelBase? CurrentViewModel { get; private set; }

    public event Action<ViewModelBase?>? CurrentViewModelChanged;

    public void RegisterModule(string label, ViewModelBase viewModel, bool isEngineering = false)
    {
        if (Items.Any(x => ReferenceEquals(x.ViewModel, viewModel)))
            return;

        Items.Add(new NavigationItem
        {
            Label         = label,
            ViewModel     = viewModel,
            IsEngineering = isEngineering
        });
    }

    public void NavigateTo(ViewModelBase viewModel)
    {
        if (ReferenceEquals(CurrentViewModel, viewModel))
            return;

        CurrentViewModel = viewModel;
        CurrentViewModelChanged?.Invoke(CurrentViewModel);
    }
}
