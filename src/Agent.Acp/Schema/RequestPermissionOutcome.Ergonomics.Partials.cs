namespace Agent.Acp.Schema;

public partial class RequestPermissionOutcome
{
    public bool IsCancelled => Outcome == RequestPermissionOutcomeOutcome.Cancelled;
    public bool IsSelected => Outcome == RequestPermissionOutcomeOutcome.Selected;

    public string GetSelectedOptionIdOrThrow()
    {
        if (!IsSelected || string.IsNullOrWhiteSpace(OptionId))
            throw new InvalidOperationException("Permission outcome is not selected or optionId is missing.");

        return OptionId;
    }
}
