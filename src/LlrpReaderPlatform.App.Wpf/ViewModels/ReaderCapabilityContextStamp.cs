using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>
/// UI 异步操作使用的 Reader 能力上下文指纹。
/// Reader 列表刷新会重建 ViewModel，但只要能力上下文没有变化，
/// 不应取消仍在进行的 Query/Tag Access；能力重新捕获或进入不可用状态时，
/// 则必须让晚到结果失效。
/// </summary>
internal readonly record struct ReaderCapabilityContextStamp(
    Guid? ReaderId,
    long CapabilityRevision,
    bool IsStale,
    bool CapabilitiesCurrent,
    ushort? GpiCount,
    ushort? GpoCount,
    bool TagAccessAvailable)
{
    public static ReaderCapabilityContextStamp From(ReaderItemViewModel? reader) =>
        From(reader?.Snapshot);

    public static ReaderCapabilityContextStamp From(ReaderRuntimeSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return new(null, 0, true, false, null, null, false);
        }

        return new(
            snapshot.ReaderId,
            snapshot.CapabilityRevision,
            snapshot.IsStale,
            IsCapabilitiesCurrent(snapshot),
            snapshot.GpiCount,
            snapshot.GpoCount,
            IsTagAccessAvailable(snapshot));
    }

    private static bool IsCapabilitiesCurrent(ReaderRuntimeSnapshot snapshot) =>
        !snapshot.IsStale
        && snapshot.State is not (
            ReaderState.Faulted
            or ReaderState.Connecting
            or ReaderState.Disconnecting
            or ReaderState.Stopping);

    private static bool IsTagAccessAvailable(ReaderRuntimeSnapshot snapshot) =>
        snapshot.IsStale
        || snapshot.CapabilityRevision == 0
        || snapshot.FeatureCatalog.SupportsOrUnknown(ReaderFeatures.StandardTagAccess);
}
