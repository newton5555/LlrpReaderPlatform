using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using LlrpDevice.Virtual.Hosting;

namespace LlrpVirtualDevice.App.Wpf.Converters;

public sealed class StateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is VirtualLlrpDeviceHostState state)
        {
            return state switch
            {
                VirtualLlrpDeviceHostState.Running => new SolidColorBrush(Color.FromRgb(46, 160, 67)), // Green #2EA043
                VirtualLlrpDeviceHostState.Starting or VirtualLlrpDeviceHostState.Stopping => new SolidColorBrush(Color.FromRgb(210, 153, 34)), // Amber #D29922
                VirtualLlrpDeviceHostState.Faulted => new SolidColorBrush(Color.FromRgb(248, 81, 73)), // Red #F85149
                _ => new SolidColorBrush(Color.FromRgb(139, 148, 158)), // Gray #8B949E
            };
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StateToTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is VirtualLlrpDeviceHostState state)
        {
            return state switch
            {
                VirtualLlrpDeviceHostState.Running => "Running",
                VirtualLlrpDeviceHostState.Starting => "Starting...",
                VirtualLlrpDeviceHostState.Stopping => "Stopping...",
                VirtualLlrpDeviceHostState.Stopped => "Stopped",
                VirtualLlrpDeviceHostState.Faulted => "Faulted",
                _ => "Created",
            };
        }

        return "Unknown";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DirectionToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string dir)
        {
            return dir.Equals("Rx", StringComparison.OrdinalIgnoreCase)
                ? new SolidColorBrush(Color.FromRgb(31, 111, 235)) // Blue for Incoming Rx
                : new SolidColorBrush(Color.FromRgb(46, 160, 67));  // Green for Outgoing Tx
        }

        return new SolidColorBrush(Colors.DarkGray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int count)
        {
            return count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool notNull = value != null;
        if (parameter is string s && s.Equals("Inverse", StringComparison.OrdinalIgnoreCase))
        {
            notNull = !notNull;
        }

        return notNull ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

