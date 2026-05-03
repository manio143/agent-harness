using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    public MainWindowViewModel()
    {
        Connection = new ConnectionViewModel();
    }

    public ConnectionViewModel Connection { get; }
}
