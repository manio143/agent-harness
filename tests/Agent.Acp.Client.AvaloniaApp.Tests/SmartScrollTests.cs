using Agent.Acp.Client.AvaloniaApp.Views.Conversation;
using Xunit;

namespace Agent.Acp.Client.AvaloniaApp.Tests;

public sealed class SmartScrollTests
{
    [Fact]
    public void ShouldAutoScroll_true_when_content_fits()
    {
        Assert.True(SmartScroll.ShouldAutoScroll(offsetY: 0, viewportHeight: 100, extentHeight: 80));
    }

    [Fact]
    public void ShouldAutoScroll_true_when_near_bottom()
    {
        // extent=1000, viewport=200 => maxOffset=800
        Assert.True(SmartScroll.ShouldAutoScroll(offsetY: 780, viewportHeight: 200, extentHeight: 1000, thresholdPx: 48));
    }

    [Fact]
    public void ShouldAutoScroll_false_when_user_scrolled_up()
    {
        Assert.False(SmartScroll.ShouldAutoScroll(offsetY: 600, viewportHeight: 200, extentHeight: 1000, thresholdPx: 48));
    }
}
