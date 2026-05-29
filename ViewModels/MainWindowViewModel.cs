namespace ShockUI.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public ShellViewModel Shell { get; }

    public MainWindowViewModel(ShellViewModel shell)
    {
        Shell = shell;
    }
}