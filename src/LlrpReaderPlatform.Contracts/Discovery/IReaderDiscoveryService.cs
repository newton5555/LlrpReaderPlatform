namespace LlrpReaderPlatform.Contracts.Discovery;

/// <summary>通过网络服务发现得到的 Reader 端点。</summary>
public sealed record DiscoveredReader(
    string DisplayName,
    string Host,
    string IpAddress,
    int Port,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>Reader 网络发现契约；具体协议由 Infrastructure 实现。</summary>
public interface IReaderDiscoveryService
{
    Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default);
}
