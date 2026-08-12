using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.Contracts.Discovery;

/// <summary>
/// 发现结果的跨消费者归一化规则。基础设施、WPF 和未来的其它消费者都应使用同一份结果。
/// </summary>
public static class DiscoveredReaderNormalization
{
    public static IReadOnlyList<DiscoveredReader> Normalize(
        IReadOnlyList<DiscoveredReader> found)
    {
        ArgumentNullException.ThrowIfNull(found);

        var normalized = new List<DiscoveredReader>(found.Count);
        var endpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoveredReader reader in found)
        {
            string host = ReaderEndpoint.NormalizeHost(reader.Host);
            string ipAddress = ReaderEndpoint.NormalizeHost(reader.IpAddress);
            if (host.Length == 0 && ipAddress.Length == 0)
            {
                continue;
            }

            if (host.Length == 0)
            {
                host = ipAddress;
            }

            if (ipAddress.Length == 0)
            {
                ipAddress = host;
            }

            int port = reader.Port is >= 1 and <= 65535 ? reader.Port : 5084;
            if (!endpoints.Add(ReaderEndpoint.Format(ipAddress, port)))
            {
                continue;
            }

            string displayName = string.IsNullOrWhiteSpace(reader.DisplayName)
                ? host
                : reader.DisplayName.Trim();
            normalized.Add(reader with
            {
                DisplayName = displayName,
                Host = host,
                IpAddress = ipAddress,
                Port = port,
            });
        }

        return normalized;
    }
}
