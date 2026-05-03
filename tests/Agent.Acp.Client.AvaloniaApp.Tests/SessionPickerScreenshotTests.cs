using Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;
using Agent.Acp.Client.AvaloniaApp.Views.Connection;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class SessionPickerScreenshotTests
{
    [AvaloniaFact]
    public void Renders_session_picker_screen()
    {
        var vm = new SessionPickerViewModel();
        vm.SetSessions(new[]
        {
            new SessionListItemViewModel(
                sessionId: "7b1d…",
                title: "ACP Client UX work",
                updatedAt: "2026-05-03T18:30:00Z"),
            new SessionListItemViewModel(
                sessionId: "c2a9…",
                title: null,
                updatedAt: "2026-05-02T11:15:00Z"),
        });
        vm.Selected = vm.Sessions[0];

        ScreenshotTestHarness.Save(new SessionPickerView { DataContext = vm }, width: 900, height: 560, fileName: "session-picker.png");
    }
}
