namespace LlrpReaderPlatform.Contracts.Errors;

/// <summary>
/// UI 无关的平台错误分类。具体服务可以继续提供面向用户的 Error 文本，
/// 其它消费者使用此稳定分类做重试、禁用控件和提示策略。
/// </summary>
public enum PlatformErrorCode
{
    None = 0,
    ReaderBusy = 1,
    DeviceFailed = 2,
    Unsupported = 3,
    StaleCapability = 4,
    InvalidSettings = 5,
    NotFound = 6,
}
