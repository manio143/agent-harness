using Avalonia;
using Avalonia.Controls;

namespace Agent.Acp.Client.AvaloniaApp.Views.Connection;

public partial class SessionPickerView : UserControl
{
    public SessionPickerView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // UX: focus the filter box so you can immediately type.
        var filter = this.FindControl<TextBox>("FilterBox");
        filter?.Focus();
    }
}
