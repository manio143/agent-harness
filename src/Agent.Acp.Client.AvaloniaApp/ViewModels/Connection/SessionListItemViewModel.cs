using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Agent.Acp.Client.AvaloniaApp.ViewModels.Connection;

public sealed partial class SessionListItemViewModel : ObservableObject
{
    public SessionListItemViewModel(string sessionId, string? title, string? updatedAt)
    {
        SessionId = sessionId;
        Title = title;
        UpdatedAt = updatedAt;
    }

    public string SessionId { get; }

    public string? Title { get; }

    public string? UpdatedAt { get; }

    public string DisplayTitle
        => string.IsNullOrWhiteSpace(Title) ? "(untitled)" : Title!;

    public string DisplayUpdatedAt
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UpdatedAt)) return "";
            if (DateTimeOffset.TryParse(UpdatedAt, out var dto))
                return dto.ToString("yyyy-MM-dd HH:mm 'UTC'");
            return UpdatedAt!;
        }
    }
}
