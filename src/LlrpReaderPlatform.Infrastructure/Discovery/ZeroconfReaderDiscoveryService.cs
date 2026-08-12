using LlrpReaderPlatform.Contracts.Discovery;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Zeroconf;

namespace LlrpReaderPlatform.Infrastructure.Discovery;

/// <summary>基于 Zeroconf 的 mDNS 发现（服务类型 _llrp._tcp.local.）。</summary>
public sealed class ZeroconfReaderDiscoveryService : IReaderDiscoveryService
{
    public const string LlrpServiceType = "_llrp._tcp.local.";

    private readonly ILogger<ZeroconfReaderDiscoveryService> logger;

    public ZeroconfReaderDiscoveryService(ILogger<ZeroconfReaderDiscoveryService>? logger = null)
    {
        this.logger = logger ?? NullLogger<ZeroconfReaderDiscoveryService>.Instance;
    }

    public async Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(
        TimeSpan scanDuration,
        CancellationToken cancellationToken = default)
    {
        var discovered = new List<DiscoveredReader>();
        try
        {
            logger.LogDebug(
                "Starting Zeroconf reader discovery for '{ServiceType}' (duration: {Duration}s)...",
                LlrpServiceType, scanDuration.TotalSeconds);

            IReadOnlyList<IZeroconfHost> results = await ZeroconfResolver.ResolveAsync(
                LlrpServiceType,
                scanTime: scanDuration,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            foreach (IZeroconfHost result in results)
            {
                KeyValuePair<string, IService> serviceKvp = result.Services
                    .FirstOrDefault(s => s.Key.Contains("_llrp._tcp", StringComparison.OrdinalIgnoreCase));
                IService? service = serviceKvp.Value;
                int port = service?.Port ?? 5084;

                var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (service?.Properties is not null)
                {
                    foreach (IDictionary<string, string> dict in service.Properties)
                    {
                        foreach (KeyValuePair<string, string> kvp in dict)
                        {
                            properties[kvp.Key] = kvp.Value;
                        }
                    }
                }

                string ip = result.IPAddress ?? string.Empty;
                string displayName = !string.IsNullOrWhiteSpace(result.DisplayName)
                    ? result.DisplayName
                    : !string.IsNullOrWhiteSpace(ip)
                        ? ip
                        : "Unknown Reader";
                string host = !string.IsNullOrWhiteSpace(result.DisplayName)
                    ? result.DisplayName
                    : !string.IsNullOrWhiteSpace(ip)
                        ? ip
                        : "localhost";
                string ipAddress = !string.IsNullOrWhiteSpace(ip) ? ip : host;

                logger.LogInformation("Discovered LLRP reader: {DisplayName} ({IpAddress}:{Port})",
                    displayName, ipAddress, port);

                discovered.Add(new DiscoveredReader(
                    DisplayName: displayName,
                    Host: host,
                    IpAddress: ipAddress,
                    Port: port > 0 ? port : 5084,
                    Properties: properties));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Zeroconf reader discovery encountered an exception");
            throw;
        }

        return DiscoveredReaderNormalization.Normalize(discovered);
    }
}
