using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Agent.Acp.Client.AvaloniaApp.Services.Sessions;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ChatViewModel _chat;
    private readonly ComposerViewModel _composer;

    public ShellViewModel()
    {
        Connection = new ConnectionViewModel();

        // Runtime services
        var clipboard = new Agent.Acp.Client.AvaloniaApp.Services.Clipboard.AvaloniaClipboardService();

        _chat = new ChatViewModel(clipboard);
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

    public bool CanDisconnect => _process is not null;

    public bool CanReconnect => _lastStartInfo is not null;

    [ObservableProperty]
    private string? _status;

    private StdioAcpAgentProcess? _process;
    private AcpSessionUpdatePump? _pump;
    private string? _sessionId;
    private System.Diagnostics.ProcessStartInfo? _lastStartInfo;
    private SessionLogStore? _log;

    private Task SendPromptAsync(CancellationToken cancellationToken)
    {
        if (_process is null || string.IsNullOrWhiteSpace(_sessionId))
            throw new InvalidOperationException("Not connected");

        var text = _composer.Text;

        // Local echo: show what the user sent.
        _chat.ApplyUserPrompt(text);
        _log?.Append(new Domain.Conversation.ChatUserPrompt(text));

        return AcpClientBootstrap.PromptAsync(_process.Connection, _sessionId, text, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        Status = "Disconnecting...";

        if (_process is not null)
        {
            await _process.DisposeAsync();
            _process = null;
        }

        _pump = null;
        _sessionId = null;
        _log = null;

        CurrentScreen = Screen.Connection;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsChat));
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();

        Status = "Disconnected";
    }

    [RelayCommand]
    private void ReloadLastSession()
    {
        // Reload transcript from last persisted conversation log (best-effort).
        var cwd = _lastStartInfo?.WorkingDirectory;
        if (string.IsNullOrWhiteSpace(cwd))
        {
            Status = "No previous working directory";
            return;
        }

        var pointer = new ConversationPointerStore(cwd);
        var path = pointer.TryRead();
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            Status = "No saved conversation";
            return;
        }

        var store = new SessionLogStore(path);
        _chat.Replay(store.LoadAll());
        CurrentScreen = Screen.Chat;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsChat));
        Status = $"Reloaded conversation: {System.IO.Path.GetFileName(path)}";
    }

    [RelayCommand(CanExecute = nameof(CanReconnect))]
    private async Task ReconnectAsync()
    {
        var psi = _lastStartInfo;
        if (psi is null)
            return;

        Status = "Reconnecting...";

        // Make sure we fully disconnect first.
        if (_process is not null)
        {
            await _process.DisposeAsync();
            _process = null;
        }

        _pump = null;
        _sessionId = null;

        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();

        // Reload last conversation into transcript (so the user sees continuity).
        ReloadLastSession();

        _chat.ApplyLocalSystemMessage("— Restarting agent (client rebuild) —");
        _log?.Append(new Domain.Conversation.ChatLocalSystemMessage("— Restarting agent (client rebuild) —"));

        // Then start a fresh agent+session and continue appending to the same conversation log.
        await ConnectAsync(psi);

        Status = "Reconnected";
    }

    private async Task ConnectAsync(System.Diagnostics.ProcessStartInfo psi)
    {
        Status = "Starting agent...";

        try
        {
            _lastStartInfo = psi;
            var ct = CancellationToken.None;

            _process = await StdioAcpAgentProcess.StartAsync(psi, ct);
            _ = await AcpClientBootstrap.InitializeAsync(_process.Connection, ct);

            // Use the working directory as the ACP session cwd.
            var cwd = psi.WorkingDirectory;
            var session = await AcpClientBootstrap.NewSessionAsync(_process.Connection, cwd, ct);
            _sessionId = session.SessionId;

            // Local persistence so we can reload transcript after client rebuild/restart.
            // We use a *conversation* log (not per-session) so reconnect can continue appending.
            var pointer = new ConversationPointerStore(cwd);
            var existingConversation = pointer.TryRead();
            var logPath = existingConversation ?? System.IO.Path.Combine(
                cwd,
                ".acp-client",
                "conversations",
                $"conversation-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl");

            _log = new SessionLogStore(logPath);
            pointer.Write(logPath);

            _pump = new AcpSessionUpdatePump(_sessionId, _chat);
            _process.Connection.NotificationReceived += n =>
            {
                if (_pump.TryHandle(n, out var update) && update is not null)
                {
                    _log.Append(new Domain.Conversation.ChatSessionUpdate(_sessionId!, update));
                }
            };

            CurrentScreen = Screen.Chat;
            OnPropertyChanged(nameof(IsConnection));
            OnPropertyChanged(nameof(IsChat));
            Status = $"Connected (session: {_sessionId})";
            OnPropertyChanged(nameof(CanDisconnect));
            OnPropertyChanged(nameof(CanReconnect));
            DisconnectCommand.NotifyCanExecuteChanged();
            ReconnectCommand.NotifyCanExecuteChanged();
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

            OnPropertyChanged(nameof(CanDisconnect));
        }
    }

    public enum Screen
    {
        Connection,
        Chat,
    }
}
