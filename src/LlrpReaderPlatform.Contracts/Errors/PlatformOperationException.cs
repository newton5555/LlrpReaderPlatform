namespace LlrpReaderPlatform.Contracts.Errors;

/// <summary>
/// 跨消费者传递稳定平台错误分类的异常基类。
/// 具体服务可以派生自己的异常，但 UI 不需要引用服务实现程序集才能识别错误。
/// </summary>
public class PlatformOperationException(
    PlatformErrorCode errorCode,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public PlatformErrorCode ErrorCode { get; } = errorCode;
}
