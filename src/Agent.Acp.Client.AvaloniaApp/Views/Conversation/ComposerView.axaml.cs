using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;

namespace Agent.Acp.Client.AvaloniaApp.Views.Conversation;

public partial class ComposerView : UserControl
{
    public ComposerView()
    {
        InitializeComponent();
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
