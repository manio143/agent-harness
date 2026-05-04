using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace Agent.Acp.Client.AvaloniaApp.Views.Conversation;

public partial class ToolCallDetailView : UserControl
{
    public ToolCallDetailView()
    {
        InitializeComponent();

        // Make Escape-to-close work even if focus is inside nested controls.
        AddHandler(KeyDownEvent, OnKeyDown, handledEventsToo: true);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;

        // Flyouts are hosted in a Popup. Close it explicitly.
        var popup = this.FindAncestorOfType<Popup>();
        popup?.Close();

        e.Handled = true;
    }
}
