using LlrpReaderPlatform.Infrastructure.Discovery;

const int defaultScanSeconds = 3;
const string allServicesOption = "--all-services";

if (args.Any(static arg => string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase)))
{
    PrintUsage();
    return 0;
}

bool browseAllServices = args.Any(static arg =>
    string.Equals(arg, allServicesOption, StringComparison.OrdinalIgnoreCase));
if (!TryReadScanSeconds(args, out int scanSeconds, out string? parseError))
{
    Console.Error.WriteLine(parseError);
    PrintUsage();
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("正在取消扫描...");
};
Console.CancelKeyPress += cancelHandler;

try
{
    TimeSpan scanDuration = TimeSpan.FromSeconds(scanSeconds);
    if (browseAllServices)
    {
        await BrowseAllServicesAsync(scanDuration, cancellation.Token);
    }
    else
    {
        await DiscoverReadersAsync(scanDuration, cancellation.Token);
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"硬件测试失败: {ex.Message}");
    return 1;
}
finally
{
    Console.CancelKeyPress -= cancelHandler;
}

static async Task DiscoverReadersAsync(TimeSpan scanDuration, CancellationToken cancellationToken)
{
    Console.WriteLine($"扫描 {ZeroconfReaderDiscoveryService.LlrpServiceType}，持续 {scanDuration.TotalSeconds:0.#} 秒...");

    var discovery = new ZeroconfReaderDiscoveryService();
    IReadOnlyList<LlrpReaderPlatform.Contracts.Discovery.DiscoveredReader> readers =
        await discovery.DiscoverAsync(scanDuration, cancellationToken);

    Console.WriteLine($"发现 {readers.Count} 个 LLRP Reader:");
    foreach (var reader in readers)
    {
        Console.WriteLine($"- {reader.DisplayName} | {reader.IpAddress}:{reader.Port}");
        foreach (var property in reader.Properties)
        {
            Console.WriteLine($"  {property.Key}={property.Value}");
        }
    }
}

static async Task BrowseAllServicesAsync(TimeSpan scanDuration, CancellationToken cancellationToken)
{
    Console.WriteLine($"枚举局域网 mDNS 服务类型，持续 {scanDuration.TotalSeconds:0.#} 秒...");

    ILookup<string, string> serviceTypes = await Zeroconf.ZeroconfResolver.BrowseDomainsAsync(
        scanTime: scanDuration,
        cancellationToken: cancellationToken);

    Console.WriteLine($"发现 {serviceTypes.Count} 个 mDNS 服务类型:");
    foreach (IGrouping<string, string> serviceType in serviceTypes)
    {
        Console.WriteLine($"- {serviceType.Key}");
        foreach (string host in serviceType)
        {
            Console.WriteLine($"  主机: {host}");
        }
    }
}

static bool TryReadScanSeconds(string[] args, out int scanSeconds, out string? error)
{
    scanSeconds = defaultScanSeconds;
    error = null;

    for (int index = 0; index < args.Length; index++)
    {
        if (!string.Equals(args[index], "--scan-seconds", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (index + 1 >= args.Length ||
            !int.TryParse(args[++index], out scanSeconds) ||
            scanSeconds is < 1 or > 60)
        {
            error = "--scan-seconds 必须是 1 到 60 之间的整数。";
            return false;
        }
    }

    return true;
}

static void PrintUsage()
{
    Console.WriteLine("LlrpReaderPlatform 硬件测试命令行");
    Console.WriteLine();
    Console.WriteLine("默认：发现 _llrp._tcp.local. Reader");
    Console.WriteLine("用法：");
    Console.WriteLine("  dotnet run --project tests/LlrpReaderPlatform.Hardware.Tests -- [选项]");
    Console.WriteLine();
    Console.WriteLine("选项：");
    Console.WriteLine("  --scan-seconds <1-60>  扫描时长，默认 3 秒");
    Console.WriteLine("  --all-services         查询 _services._dns-sd._udp.local. 并打印服务类型");
    Console.WriteLine("  --help                 显示帮助");
}
