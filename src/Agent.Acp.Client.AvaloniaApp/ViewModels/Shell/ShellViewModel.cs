using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;
    private readonly ComposerViewModel _composer;

    public ShellViewModel()
    {
        Connection = new ConnectionViewModel();
        _chat = new ChatViewModel();
        _composer = new ComposerViewModel(send: SendPromptAsync);

        CurrentScreen = Screen.Connection;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsChat));

        Connection.ConnectRequested += async psi => await ConnectAsync(psi);
    }

    public ConnectionViewModel Connection { get; }

    public ChatViewModel Chat => _chat;

    public ComposerViewModel Composer => _composer;

    [ObservableProperty]
    private Screen _currentScreen;

    public bool IsConnection => CurrentScreen == Screen.Connection;
    public bool IsChat => CurrentScreen == Screen.Chat;

    [ObservableProperty]
    private string? _status;

    private StdioAcpAgentProcess? _process;
    private AcpSessionUpdatePump? _pump;
    private string? _sessionId;

    private Task SendPromptAsync(CancellationToken cancellationToken)
    {
        if (_process is null || string.IsNullOrWhiteSpace(_sessionId))
            throw new InvalidOperationException("Not connected");

        var text = _composer.Text;
        return AcpClientBootstrap.PromptAsync(_process.Connection, _sessionId, text, cancellationToken);
    }

    private async Task ConnectAsync(System.Diagnostics.ProcessStartInfo psi)
    {
        Status = "Starting agent...";

        try
        {
            var ct = CancellationToken.None;

            _process = await StdioAcpAgentProcess.StartAsync(psi, ct);
            _ = await AcpClientBootstrap.InitializeAsync(_process.Connection, ct);

            // Use the working directory as the ACP session cwd.
            var cwd = psi.WorkingDirectory;
            var session = await AcpClientBootstrap.NewSessionAsync(_process.Connection, cwd, ct);
            _sessionId = session.SessionId;

            _pump = new AcpSessionUpdatePump(_sessionId, _chat);
            _process.Connection.NotificationReceived += n => _pump.TryHandle(n);

            CurrentScreen = Screen.Chat;
            OnPropertyChanged(nameof(IsConnection));
            OnPropertyChanged(nameof(IsChat));
            Status = $"Connected (session: {_sessionId})";
        }
        catch (Exception ex)
        {
            Status = "Connect failed";
            Connection.Error = ex.Message;

            if (_process is not null)
            {
                await _process.DisposeAsync();
                _process = null;
            }
        }
    }

    public enum Screen
    {
        Connection,
        Chat,
    }
}
