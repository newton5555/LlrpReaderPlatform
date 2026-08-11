using System.Net;
using System.Net.Sockets;

namespace LlrpReaderPlatform.Contracts.Readers;

/// <summary>
/// Reader 端点的跨层归一化规则。Host 可来自 WPF、SQLite、发现服务或其它消费者，
/// 但传输层和端点去重必须使用同一份 IPv6 方括号规则。
/// </summary>
public static class ReaderEndpoint
{
    public static string NormalizeHost(string host)
    {
        ArgumentNullException.ThrowIfNull(host);

        string normalized = host.Trim();
        if (normalized.Length >= 2
            && normalized[0] == '['
            && normalized[^1] == ']')
        {
            normalized = normalized[1..^1];
        }

        return IPAddress.TryParse(normalized, out IPAddress? address)
            ? address.ToString()
            : normalized;
    }

    public static string Format(string host, int port)
    {
        string normalized = NormalizeHost(host);
        return IPAddress.TryParse(normalized, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{normalized}:{port}";
    }
}
