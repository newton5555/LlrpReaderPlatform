namespace LlrpReaderPlatform.Contracts.Readers;

/// <summary>LLRP 协议版本选择策略（厂商无关的连接参数）。</summary>
public enum LlrpProtocolVersionOption
{
    /// <summary>自动探测：优先 1.1，若 Reader 拒绝则回退 1.0.1（推荐）。</summary>
    Auto = 0,

    /// <summary>强制 LLRP 1.0.1。</summary>
    Force101 = 1,

    /// <summary>强制 LLRP 1.1，协商失败则连接失败。</summary>
    Force11 = 2,
}

/// <summary>连接后由 Reader 实际协商出的标准 LLRP 协议版本。</summary>
public enum LlrpProtocolVersion
{
    Version101 = 1,
    Version11 = 2,
}

/// <summary>
/// Reader 数据源的静态配置。厂商无关：不含任何厂商扩展开关，
/// 厂商能力由 <c>IReaderExtensionModule</c> 在探测/连接阶段识别并启用。
/// </summary>
public sealed record ReaderProfile
{
    public required Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Reader";
    public required string Host { get; init; }
    public int Port { get; init; } = 5084;
    public LlrpProtocolVersionOption LlrpVersion { get; init; } = LlrpProtocolVersionOption.Auto;

    /// <summary>用户是否期望启用此 Reader（持久化的用户意图，与运行时连接状态分离）。</summary>
    public bool IsEnabled { get; init; } = true;

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host, nameof(Host));
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), "LLRP 端口必须在 1~65535 之间。");
        }
    }
}
