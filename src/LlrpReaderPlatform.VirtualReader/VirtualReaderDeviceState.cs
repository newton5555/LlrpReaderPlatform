using System.Collections.Concurrent;
using LlrpSdk;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 跨连接会话保留的 Reader 设备状态。真实 Reader 的配置、GPI/GPO 和标签内存不会
/// 因为一次短连接或 Session 重建而清空；虚拟 Reader 也保持同样的语义。
/// </summary>
internal sealed class VirtualReaderDeviceState
{
    public ConcurrentDictionary<string, VirtualReaderSession.VirtualTagMemoryState> TagMemories { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<ushort, bool> GpiStates { get; } = [];

    public Dictionary<ushort, bool> GpoStates { get; } = [];

    public ReaderSettingsSnapshot? SettingsSnapshot { get; set; }

    public bool Initialized { get; set; }

    public object SyncRoot { get; } = new();
}
