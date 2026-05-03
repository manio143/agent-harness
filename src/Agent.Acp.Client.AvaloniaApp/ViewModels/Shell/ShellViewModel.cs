using System;
using System.Threading;
using System.Threading.Tasks;
using Agent.Acp.Client.AvaloniaApp.Services.Acp;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Agent.Acp.Schema;
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
        SessionPicker = new SessionPickerViewModel();

        // Runtime services
        var clipboard = new Agent.Acp.Client.AvaloniaApp.Services.Clipboard.AvaloniaClipboardService();

        _chat = new ChatViewModel(clipboard);
        _composer = new ComposerViewModel(send: SendPromptAsync);

        CurrentScreen = Screen.Connection;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsSessionPicker));
        OnPropertyChanged(nameof(IsChat));

        Connection.ConnectRequested += async psi => await ConnectAsync(psi);
        SessionPicker.OpenRequested += async sessionId => await OpenSessionAsync(sessionId);
        SessionPicker.RefreshRequested += async () => await RefreshSessionsAsync();
        SessionPicker.CancelRequested += async () => await CancelSessionPickerAsync();
    }

    public ConnectionViewModel Connection { get; }

    public SessionPickerViewModel SessionPicker { get; }

    public ChatViewModel Chat => _chat;

    public ComposerViewModel Composer => _composer;

    [ObservableProperty]
    private Screen _currentScreen;

    public bool IsConnection => CurrentScreen == Screen.Connection;
    public bool IsSessionPicker => CurrentScreen == Screen.SessionPicker;
    public bool IsChat => CurrentScreen == Screen.Chat;

    public bool CanDisconnect => _process is not null;

    [ObservableProperty]
    private string? _status;

    private StdioAcpAgentProcess? _process;
    private AcpSessionUpdatePump? _pump;
    private string? _sessionId;
    private Action<Agent.Acp.Protocol.JsonRpcNotification>? _notifHandler;

    private Task SendPromptAsync(CancellationToken cancellationToken)
    {
        if (_process is null || string.IsNullOrWhiteSpace(_sessionId))
            throw new InvalidOperationException("Not connected");

        var text = _composer.Text;

        // Local echo: show what the user sent.
        _chat.ApplyUserPrompt(text);
        return AcpClientBootstrap.PromptAsync(_process.Connection, _sessionId, text, cancellationToken);
    }

    [RelayCommand(CanExecute = nameof(CanDisconnect))]
    private async Task DisconnectAsync()
    {
        Status = "Disconnecting...";

        if (_process is not null)
        {
            if (_notifHandler is not null)
                _process.Connection.NotificationReceived -= _notifHandler;

            await _process.DisposeAsync();
            _process = null;
        }

        _notifHandler = null;
        _pump = null;
        _sessionId = null;

        CurrentScreen = Screen.Connection;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsSessionPicker));
        OnPropertyChanged(nameof(IsChat));
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();

        Status = "Disconnected";
    }

    private async Task ConnectAsync(System.Diagnostics.ProcessStartInfo psi)
    {
        Status = "Starting agent...";

        try
        {
            var ct = CancellationToken.None;

            // Ensure clean slate.
            if (_process is not null)
            {
                if (_notifHandler is not null)
                    _process.Connection.NotificationReceived -= _notifHandler;

                await _process.DisposeAsync();
                _process = null;
            }

            _notifHandler = null;
            _pump = null;
            _sessionId = null;

            _process = await StdioAcpAgentProcess.StartAsync(psi, ct);
            _ = await AcpClientBootstrap.InitializeAsync(_process.Connection, ct);

            // Use the working directory as the ACP session cwd.
            var cwd = psi.WorkingDirectory;

            // Reset transcript on connect; if we load an existing session, replay will rebuild it.
            _chat.Reset();

            if (!Connection.ContinueLastSession)
            {
                // Straight to a brand-new session.
                await OpenSessionAsync(sessionId: null);
                return;
            }

            await RefreshSessionsAsync(cwd);

            if (SessionPicker.Sessions.Count == 0)
            {
                // Nothing to pick — just start a new session.
                await OpenSessionAsync(sessionId: null);
                return;
            }

            CurrentScreen = Screen.SessionPicker;
            OnPropertyChanged(nameof(IsConnection));
            OnPropertyChanged(nameof(IsSessionPicker));
            OnPropertyChanged(nameof(IsChat));
            Status = "Select a session";
            OnPropertyChanged(nameof(CanDisconnect));
            DisconnectCommand.NotifyCanExecuteChanged();
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
            DisconnectCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RefreshSessionsAsync(string? cwdOverride = null)
    {
        if (_process is null)
            return;

        var ct = CancellationToken.None;
        var cwd = cwdOverride ?? Connection.WorkingDirectory;

        Status = "Fetching sessions...";

        var list = await AcpClientBootstrap.ListSessionsAsync(_process.Connection, cwd: cwd, cancellationToken: ct);

        SessionPicker.Sessions.Clear();
        foreach (var s in list.Sessions)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.SessionId))
                continue;

            SessionPicker.Sessions.Add(new SessionListItemViewModel(
                sessionId: s.SessionId,
                title: s.Title,
                updatedAt: s.UpdatedAt));
        }

        // Default select: most recently updatedAt (best-effort).
        SessionListItemViewModel? best = null;
        DateTimeOffset? bestUpdated = null;

        foreach (var item in SessionPicker.Sessions)
        {
            if (string.IsNullOrWhiteSpace(item.UpdatedAt))
                continue;

            if (!DateTimeOffset.TryParse(item.UpdatedAt, out var parsed))
                continue;

            if (best is null || bestUpdated is null || parsed > bestUpdated)
            {
                best = item;
                bestUpdated = parsed;
            }
        }

        SessionPicker.Selected = best;
        SessionPicker.StartNewSession = SessionPicker.Sessions.Count == 0;
    }

    private async Task CancelSessionPickerAsync()
    {
        // Cancel returns to Connect screen but keeps the agent running (so user can still Disconnect explicitly).
        CurrentScreen = Screen.Connection;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsSessionPicker));
        OnPropertyChanged(nameof(IsChat));
        Status = "Cancelled";
    }

    private async Task OpenSessionAsync(string? sessionId)
    {
        if (_process is null)
            throw new InvalidOperationException("Not connected");

        var ct = CancellationToken.None;
        var cwd = Connection.WorkingDirectory;

        Status = sessionId is null ? "Creating new session..." : $"Loading session: {sessionId}";

        if (sessionId is null)
        {
            var session = await AcpClientBootstrap.NewSessionAsync(_process.Connection, cwd, ct);
            _sessionId = session.SessionId;
        }
        else
        {
            _sessionId = sessionId;
        }

        if (_notifHandler is not null)
            _process.Connection.NotificationReceived -= _notifHandler;

        _pump = new AcpSessionUpdatePump(_sessionId, _chat);
        _notifHandler = n => _pump.TryHandle(n);
        _process.Connection.NotificationReceived += _notifHandler;

        if (sessionId is not null)
        {
            // ACP contract: replay via session/update happens before completing session/load.
            _ = await AcpClientBootstrap.LoadSessionAsync(_process.Connection, _sessionId, cwd, ct);
        }

        CurrentScreen = Screen.Chat;
        OnPropertyChanged(nameof(IsConnection));
        OnPropertyChanged(nameof(IsSessionPicker));
        OnPropertyChanged(nameof(IsChat));
        Status = $"Connected (session: {_sessionId})";
        OnPropertyChanged(nameof(CanDisconnect));
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    public enum Screen
    {
        Connection,
        SessionPicker,
        Chat,
    }
}
