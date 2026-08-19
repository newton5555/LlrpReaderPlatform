using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using LlrpDevice.Virtual.Hosting;
using LlrpVirtualDevice.App.Wpf.Models;
using Microsoft.Extensions.Logging;

namespace LlrpVirtualDevice.App.Wpf.Services;

public sealed class VirtualDeviceManagerService : IVirtualDeviceManagerService
{
    private readonly ILogger<VirtualDeviceManagerService> _logger;
    private readonly ConcurrentDictionary<string, (VirtualDeviceInstanceConfig Config, IVirtualDeviceHost? Host)> _instances = new();
    private readonly string _configFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public VirtualDeviceManagerService(ILogger<VirtualDeviceManagerService> logger)
    {
        _logger = logger;
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string dir = Path.Combine(appData, "LlrpVirtualDeviceStudio");
        Directory.CreateDirectory(dir);
        _configFilePath = Path.Combine(dir, "virtual-devices.json");
    }

    public IReadOnlyList<VirtualDeviceInstanceConfig> GetAllConfigs()
    {
        return _instances.Values.Select(v => v.Config).ToList();
    }

    public IVirtualDeviceHost? GetHost(string instanceId)
    {
        return _instances.TryGetValue(instanceId, out var entry) ? entry.Host : null;
    }

    public async Task<IVirtualDeviceHost> CreateOrUpdateHostAsync(
        VirtualDeviceInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_instances.TryGetValue(config.Id, out var existing) && existing.Host != null)
        {
            try
            {
                await DisposeHostAsync(existing.Host, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error while stopping previous host for instance {Id}", config.Id);
            }
        }

        var host = CreateHostFromConfig(config);
        _instances[config.Id] = (config, host);
        await SaveConfigsAsync(cancellationToken).ConfigureAwait(false);
        return host;
    }

    public async Task StartHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(instanceId, out var entry))
        {
            throw new KeyNotFoundException($"Instance {instanceId} not found.");
        }

        if (entry.Host is { State: VirtualLlrpDeviceHostState.Running or VirtualLlrpDeviceHostState.Starting })
        {
            return;
        }

        if (entry.Host is not null)
        {
            await DisposeHostAsync(entry.Host, cancellationToken).ConfigureAwait(false);
        }

        IVirtualDeviceHost host = CreateHostFromConfig(entry.Config);
        _instances[instanceId] = (entry.Config, host);
        try
        {
            _logger.LogInformation("Starting virtual device host '{Name}' on port {Port}...", entry.Config.Name, entry.Config.Port);
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _instances[instanceId] = (entry.Config, null);
            }

            throw;
        }
    }

    public async Task StopHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!_instances.TryGetValue(instanceId, out var entry))
        {
            return;
        }

        if (entry.Host is null)
        {
            return;
        }

        _logger.LogInformation("Stopping virtual device host '{Name}'...", entry.Config.Name);
        try
        {
            await DisposeHostAsync(entry.Host, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A stopped Host owns the old VirtualLlrpDevice and its tag store. Keep the
            // configuration, but force the next start to build a fresh Host from it.
            _instances[instanceId] = (entry.Config, null);
        }
    }

    public async Task RestartHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (!_instances.ContainsKey(instanceId))
        {
            throw new KeyNotFoundException($"Instance {instanceId} not found.");
        }

        _logger.LogInformation("Restarting virtual device host instance {Id} from current configuration...", instanceId);
        await StopHostAsync(instanceId, cancellationToken).ConfigureAwait(false);
        await StartHostAsync(instanceId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (_instances.TryRemove(instanceId, out var entry))
        {
            if (entry.Host != null)
            {
                try
                {
                    await DisposeHostAsync(entry.Host, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error while stopping host {Id} during deletion", instanceId);
                }
            }
            await SaveConfigsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in _instances.Keys)
        {
            try
            {
                await StartHostAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start virtual device instance {Id}", key);
            }
        }
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        foreach (var key in _instances.Keys)
        {
            try
            {
                await StopHostAsync(key, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to stop virtual device instance {Id}", key);
            }
        }
    }

    public async Task LoadConfigsAsync(CancellationToken cancellationToken = default)
    {
        List<VirtualDeviceInstanceConfig>? list = null;
        if (File.Exists(_configFilePath))
        {
            try
            {
                string json = await File.ReadAllTextAsync(_configFilePath, cancellationToken).ConfigureAwait(false);
                list = JsonSerializer.Deserialize<List<VirtualDeviceInstanceConfig>>(json, JsonOptions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load virtual device configurations from {Path}", _configFilePath);
            }
        }

        if (list == null || list.Count == 0)
        {
            list = [VirtualDeviceInstanceConfig.CreateDefault(5084, "Virtual-Reader-1")];
        }

        _instances.Clear();
        foreach (var cfg in list)
        {
            var host = CreateHostFromConfig(cfg);
            _instances[cfg.Id] = (cfg, host);
        }

        await SaveConfigsAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveConfigsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var configs = _instances.Values.Select(v => v.Config).ToList();
            string json = JsonSerializer.Serialize(configs, JsonOptions);
            await File.WriteAllTextAsync(_configFilePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save virtual device configurations to {Path}", _configFilePath);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _instances.Values)
        {
            if (entry.Host != null)
            {
                try
                {
                    await entry.Host.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore dispose errors on shutdown
                }
            }
        }
        _instances.Clear();
    }

    private static async Task DisposeHostAsync(
        IVirtualDeviceHost host,
        CancellationToken cancellationToken)
    {
        try
        {
            if (host.State is VirtualLlrpDeviceHostState.Running or VirtualLlrpDeviceHostState.Starting)
            {
                await host.StopAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private IVirtualDeviceHost CreateHostFromConfig(VirtualDeviceInstanceConfig config)
    {
        string profileId = ResolveProfileId(config);
        VirtualDeviceProfileInfo profile = VirtualDeviceProfiles.Get(profileId);
        VirtualDeviceProtocolVersion protocolVersion = ParseProtocolVersion(config.ProtocolVersion);

        if (string.Equals(profileId, VirtualDeviceProfiles.ImpinjR420Id, StringComparison.OrdinalIgnoreCase) &&
            protocolVersion != VirtualDeviceProtocolVersion.Llrp101)
        {
            throw new InvalidDataException("Impinj-R420 capability profile only supports LLRP 1.0.1.");
        }

        if (config.MaxAntennas != profile.MaxNumberOfAntennas)
        {
            _logger.LogInformation(
                "Capability profile {ProfileId} fixes the antenna count at {AntennaCount}; replacing saved value {SavedAntennaCount}.",
                profileId,
                profile.MaxNumberOfAntennas,
                config.MaxAntennas);
            config.MaxAntennas = profile.MaxNumberOfAntennas;
        }

        IPAddress listenAddress = IPAddress.TryParse(config.ListenAddress, out var parsedAddress)
            ? parsedAddress
            : IPAddress.Loopback;

        var hostOptions = new VirtualDeviceHostOptions
        {
            ProfileId = profileId,
            Name = config.Name,
            ListenAddress = listenAddress,
            Port = config.Port,
            ProtocolVersion = protocolVersion,
            RelaxedRoSpecStateChecks = config.RelaxedRoSpecStateChecks,
            Inventory = CreateInventoryOptions(config),
            Simulation = new VirtualDeviceSimulationOptions
            {
                Scenario = config.Scenario,
                DetectionProbability = Math.Clamp(config.DetectionProbability, 0.0, 1.0),
                RssiJitterDb = Math.Max(0, config.RssiJitterDb),
                PresenceCycleRounds = Math.Max(1, config.PresenceCycleRounds),
            },
        };

        return VirtualLlrpDeviceHost.Create(hostOptions);
    }

    private string ResolveProfileId(VirtualDeviceInstanceConfig config)
    {
        string profileName = config.DeviceProfile?.Trim() ?? string.Empty;
        if (string.Equals(profileName, "Standard", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(profileName, "Standard101", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDeviceProfiles.Standard101Id;
        }

        if (string.Equals(profileName, "Impinj-R420", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDeviceProfiles.ImpinjR420Id;
        }

        if (string.Equals(profileName, "Zebra-FX9600", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Legacy profile {ProfileName} is no longer available in the SDK; migrating instance {InstanceId} to Standard.",
                profileName,
                config.Id);
            config.DeviceProfile = "Standard";
            return VirtualDeviceProfiles.Standard101Id;
        }

        if (VirtualDeviceProfiles.All.Any(profile =>
                string.Equals(profile.Id, profileName, StringComparison.OrdinalIgnoreCase)))
        {
            return profileName;
        }

        throw new InvalidDataException(
            $"Unknown virtual-device capability profile '{profileName}'.");
    }

    private static VirtualDeviceProtocolVersion ParseProtocolVersion(string? value)
    {
        string protocolVersion = value?.Trim() ?? string.Empty;
        if (string.Equals(protocolVersion, "1.0.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocolVersion, "Llrp101", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDeviceProtocolVersion.Llrp101;
        }

        if (string.Equals(protocolVersion, "1.1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocolVersion, "Llrp11", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDeviceProtocolVersion.Llrp11;
        }

        if (string.Equals(protocolVersion, "2.0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(protocolVersion, "Llrp20", StringComparison.OrdinalIgnoreCase))
        {
            return VirtualDeviceProtocolVersion.Llrp20;
        }

        throw new InvalidDataException(
            $"Unknown LLRP protocol version '{protocolVersion}'.");
    }

    private static VirtualInventoryOptions CreateInventoryOptions(VirtualDeviceInstanceConfig config)
    {
        IReadOnlyList<VirtualTagConfig> configuredTags = config.Tags ?? [];
        IReadOnlyList<VirtualInventoryTag> tags = configuredTags.Count == 0
            ? [new VirtualInventoryTag
            {
                ElectronicProductCode = Convert.FromHexString("E28011606000020485984444"),
                Tid = Convert.FromHexString("E280116020007001"),
                AntennaId = 1,
                PeakRssi = -42,
            }]
            : configuredTags.Select(CreateInventoryTag).ToArray();

        return new VirtualInventoryOptions
        {
            SourceId = config.Id,
            Tags = tags,
        };
    }

    private static VirtualInventoryTag CreateInventoryTag(VirtualTagConfig tag) => new()
    {
        ElectronicProductCode = ParseHexBytes(tag.EpcHex, [0xE2, 0x80, 0x11, 0x60]),
        Tid = ParseHexBytes(tag.TidHex, []),
        PeakRssi = tag.PeakRssi,
        AntennaId = tag.AntennaId,
        UserMemory = ParseUserMemory(tag.UserMemoryHex),
        AccessPassword = ParsePassword(tag.AccessPasswordHex),
        KillPassword = ParsePassword(tag.KillPasswordHex),
    };

    private static byte[] ParseHexBytes(string? value, byte[] fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return Convert.FromHexString(value);
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static IReadOnlyList<ushort> ParseUserMemory(string? value)
    {
        byte[] bytes = ParseHexBytes(value, []);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
        {
            return [];
        }

        return Enumerable
            .Range(0, bytes.Length / 2)
            .Select(index => BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index * 2, 2)))
            .ToArray();
    }

    private static uint ParsePassword(string? value) =>
        uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint password)
            ? password
            : 0;
}
