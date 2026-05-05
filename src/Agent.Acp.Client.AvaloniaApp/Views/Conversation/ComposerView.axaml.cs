using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Agent.Acp.Client.AvaloniaApp.Views.Conversation;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();

        // Use a tunneling handler so we can intercept Ctrl+Enter before the TextBox turns it into a newline.
        // This matches the real app behavior better than a bubble handler.
        var composer = this.FindControl<TextBox>("ComposerTextBox");
        composer?.AddHandler(InputElement.KeyDownEvent, OnComposerKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ViewModels.Conversation.ComposerViewModel vm)
            return;

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (vm.SendCommand.CanExecute(null))
                _ = vm.SendCommand.ExecuteAsync(CancellationToken.None);

            e.Handled = true;
        }
    }
}
