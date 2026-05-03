using System;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;

public sealed partial class ConnectionViewModel : ObservableObject
{
    [ObservableProperty]
    private string _command = "dotnet";

    [ObservableProperty]
    private string _arguments = "run --project src/Agent.Server -c Release";

    [ObservableProperty]
    private string _workingDirectory = Environment.CurrentDirectory;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private bool _isBusy;

    public event Action<ProcessStartInfo>? ConnectRequested;

    // Test-friendly entry point (avoids needing to raise the event from tests).
    public void RequestConnect(ProcessStartInfo psi) => ConnectRequested?.Invoke(psi);

    [RelayCommand]
    private void Connect()
    {
        Error = null;

        if (string.IsNullOrWhiteSpace(Command))
        {
            Error = "Command is required";
            return;
        }

        if (string.IsNullOrWhiteSpace(WorkingDirectory))
        {
            Error = "Working directory is required";
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = Command,
            Arguments = Arguments,
            WorkingDirectory = WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        ConnectRequested?.Invoke(psi);
    }
}
