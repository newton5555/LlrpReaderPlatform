using LlrpReaderPlatform.Contracts.Errors;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>把平台稳定错误码投影为 WPF 用户可读文本；UI 不解析服务层本地化错误字符串。</summary>
internal static class PlatformErrorDisplay
{
    public static string Failure(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is PlatformOperationException platformException
            ? Failure(operation, platformException.ErrorCode, platformException.Message)
            : Failure(operation, PlatformErrorCode.DeviceFailed, exception.Message);
    }

    public static string Failure(string operation, PlatformErrorCode code, string? detail)
    {
        string suffix = string.IsNullOrWhiteSpace(detail) ? "无详细信息。" : detail;
        return code == PlatformErrorCode.None
            ? $"{operation}失败: {suffix}"
            : $"{operation}失败（{Describe(code)}）：{suffix}";
    }

    private static string Describe(PlatformErrorCode code) => code switch
    {
        PlatformErrorCode.ReaderBusy => "Reader 忙碌",
        PlatformErrorCode.DeviceFailed => "设备错误",
        PlatformErrorCode.Unsupported => "设备不支持",
        PlatformErrorCode.StaleCapability => "能力已过期",
        PlatformErrorCode.InvalidSettings => "设置无效",
        PlatformErrorCode.NotFound => "未找到",
        PlatformErrorCode.PersistenceFailed => "本地保存失败",
        PlatformErrorCode.AlreadyExists => "已存在",
        PlatformErrorCode.RegistrationFailed => "平台注册失败",
        _ => "未知错误",
    };
}
