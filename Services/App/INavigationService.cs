using System;
using System.Collections.ObjectModel;
using ShockUI.Models.App;
using ShockUI.ViewModels;

namespace ShockUI.Services.App;

public interface INavigationService
{
    ObservableCollection<NavigationItem> Items { get; }
    ViewModelBase? CurrentViewModel { get; }
    event Action<ViewModelBase?>? CurrentViewModelChanged;

    void RegisterModule(string label, ViewModelBase viewModel, bool isEngineering = false);
    void NavigateTo(ViewModelBase viewModel);
}
