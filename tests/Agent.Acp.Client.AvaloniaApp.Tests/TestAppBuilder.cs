using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
