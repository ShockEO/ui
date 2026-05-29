using ShockUI.ViewModels;

namespace ShockUI.Models.Camera;

public sealed class NavigationItem
{
    public string Title { get; }
    public ViewModelBase ViewModel { get; }

    public NavigationItem(string title, ViewModelBase viewModel)
    {
        Title = title;
        ViewModel = viewModel;
    }
}