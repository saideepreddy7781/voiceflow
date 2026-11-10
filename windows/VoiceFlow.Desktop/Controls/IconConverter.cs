using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace VoiceFlow.Desktop.Controls;

/// <summary>Looks up an icon geometry by resource key, for list items that carry an icon name.</summary>
public sealed class IconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key && Application.Current!.TryGetResource(key, null, out var geometry) ? geometry : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
