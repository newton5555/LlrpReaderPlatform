using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LlrpReaderPlatform.App.Wpf.Converters;

public sealed class HexColorToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            return ColorConverter.ConvertFromString(value as string ?? "#5EEAD4") is Color color
                ? color
                : Colors.MediumTurquoise;
        }
        catch (FormatException)
        {
            return Colors.MediumTurquoise;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Color color)
        {
            return Binding.DoNothing;
        }

        return color.A == byte.MaxValue
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }
}
