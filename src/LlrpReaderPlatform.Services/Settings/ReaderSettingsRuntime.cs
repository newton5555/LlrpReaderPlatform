using LlrpSdk;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// SettingsService 使用的运行时桥接。SDK 类型只停留在 Services，ReaderManager 负责
/// 在同一 Reader Gate 内建立短连接、读写设置并释放连接。
/// </summary>
public interface IReaderSettingsRuntime
{
    Task<ReaderSettingsRuntimeSnapshot> QueryAsync(Guid readerId, CancellationToken ct = default);

    /// <summary>在短连接租约内读取 SDK 默认设置。</summary>
    Task<ReaderSettingsRuntimeSnapshot> GetDefaultsAsync(Guid readerId, CancellationToken ct = default);

    /// <summary>
    /// 在同一连接租约中重新读取基线、调用编译器生成最终设置并下发，避免
    /// Query 与 Apply 之间出现跨操作的设置竞态。
    /// </summary>
    Task ApplyAsync(
        Guid readerId,
        Func<ReaderSettingsRuntimeSnapshot, ReaderSettings> compile,
        CancellationToken ct = default);
}

public sealed record ReaderSettingsRuntimeSnapshot(
    ReaderSettingsSnapshot Settings,
    ReaderCapabilities? Capabilities);

/// <summary>短设置操作与长 Inventory 租约冲突时使用的明确异常。</summary>
public sealed class ReaderBusyException(string message) : InvalidOperationException(message);
