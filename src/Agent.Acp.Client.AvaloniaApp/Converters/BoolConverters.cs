using System;
using Avalonia.Data.Converters;

namespace Agent.Acp.Client.AvaloniaApp.Converters;

public static class BoolConverters
{
    public static readonly IValueConverter Not = new FuncValueConverter<bool, bool>(v => !v);
}
