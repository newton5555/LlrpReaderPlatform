using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LlrpReaderPlatform.App.Wpf.Converters;

/// <summary>把持久化的 #RRGGBB/#AARRGGBB 颜色转换为可冻结的 WPF Brush。</summary>
public sealed class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return Brushes.Transparent;
        }

        try
        {
            object? converted = ColorConverter.ConvertFromString(text.Trim());
            if (converted is not Color color)
            {
                return Brushes.Transparent;
            }

            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            return Brushes.Transparent;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Color previews are display-only.");
}
