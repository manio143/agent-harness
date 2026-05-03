namespace Agent.Acp.Client.AvaloniaApp.Views.Conversation;

public static class SmartScroll
{
    /// <summary>
    /// Returns true if we should auto-scroll given current scroll state.
    /// "Smart" means we only auto-scroll when the user is already near the bottom.
    /// </summary>
    public static bool ShouldAutoScroll(double offsetY, double viewportHeight, double extentHeight, double thresholdPx = 48)
    {
        if (viewportHeight <= 0) return true;
        if (extentHeight <= viewportHeight) return true;

        var remaining = extentHeight - viewportHeight - offsetY;
        return remaining <= thresholdPx;
    }
}
