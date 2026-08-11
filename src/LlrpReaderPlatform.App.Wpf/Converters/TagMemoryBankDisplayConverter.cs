using System.Globalization;
using System.Windows.Data;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.App.Wpf.Converters;

/// <summary>
/// 保留平台枚举值，同时使用旧 Reader Studio 的 Tag Memory 显示文本。
/// </summary>
public sealed class TagMemoryBankDisplayConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            TagMemoryBank.Epc => "EPC",
            TagMemoryBank.Tid => "TID",
            TagMemoryBank.User => "User",
            TagMemoryBank.Reserved => "Reserved",
            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Tag Memory bank labels are display-only.");
}
