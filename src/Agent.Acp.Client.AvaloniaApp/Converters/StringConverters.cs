using System;
using Avalonia.Data.Converters;

namespace Agent.Acp.Client.AvaloniaApp.Converters;

public static class StringConverters
{
    public static readonly IValueConverter IsNotNullOrEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrWhiteSpace(s));
}
