using Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        Shell = new ShellViewModel();
    }

    public ShellViewModel Shell { get; }
}
