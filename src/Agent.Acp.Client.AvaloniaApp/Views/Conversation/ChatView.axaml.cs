using System;
using System.Collections.Specialized;
using Agent.Acp.Client.AvaloniaApp.ViewModels.Conversation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Agent.Acp.Client.AvaloniaApp.Views.Conversation;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scroller;
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _scroller = this.FindControl<ScrollViewer>("TranscriptScroller");
        HookVm(DataContext as ChatViewModel);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        HookVm(null);
        _scroller = null;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        HookVm(DataContext as ChatViewModel);
    }

    private void HookVm(ChatViewModel? vm)
    {
        if (_vm is not null)
            _vm.Transcript.CollectionChanged -= OnTranscriptChanged;

        _vm = vm;

        if (_vm is not null)
            _vm.Transcript.CollectionChanged += OnTranscriptChanged;
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var scroller = _scroller;
        if (scroller is null)
            return;

        var shouldScroll = SmartScroll.ShouldAutoScroll(
            offsetY: scroller.Offset.Y,
            viewportHeight: scroller.Viewport.Height,
            extentHeight: scroller.Extent.Height,
            thresholdPx: 64);

        if (!shouldScroll)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            // ScrollViewer exposes scrolling methods; ScrollToEnd isn't universal in all versions.
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, scroller.Extent.Height));
        }, DispatcherPriority.Background);
    }
}
