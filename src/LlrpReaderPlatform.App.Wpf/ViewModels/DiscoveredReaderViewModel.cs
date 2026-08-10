using LlrpReaderPlatform.Contracts.Discovery;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

/// <summary>mDNS 发现的 Reader 项（旧项目 DiscoveredReaderViewModel 的对齐）。</summary>
public sealed record DiscoveredReaderViewModel
{
    public DiscoveredReaderViewModel(DiscoveredReader reader)
    {
        Reader = reader;
    }

    public DiscoveredReader Reader { get; }
    public string DisplayName => Reader.DisplayName;
    public string Host => Reader.Host;
    public string IpAddress => Reader.IpAddress;
    public int Port => Reader.Port;
    public string DisplayEndpoint => string.Equals(Host, IpAddress, StringComparison.OrdinalIgnoreCase)
        ? $"{IpAddress}:{Port}"
        : $"{Host} ({IpAddress}:{Port})";
}
