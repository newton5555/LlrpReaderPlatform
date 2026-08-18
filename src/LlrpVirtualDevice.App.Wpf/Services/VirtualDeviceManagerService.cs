using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;
using LlrpDevice.Abstractions;
using LlrpDevice.Server;
using LlrpDevice.Virtual;
using LlrpDevice.Virtual.Hosting;
using LlrpVirtualDevice.App.Wpf.Models;
using Microsoft.Extensions.Logging;

namespace LlrpVirtualDevice.App.Wpf.Services;

public sealed class VirtualDeviceManagerService : IVirtualDeviceManagerService
{
    private readonly ILogger<VirtualDeviceManagerService> _logger;
    private readonly ConcurrentDictionary<string, (VirtualDeviceInstanceConfig Config, IVirtualLlrpDeviceHost? Host)> _instances = new();
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

    public IVirtualLlrpDeviceHost? GetHost(string instanceId)
    {
        return _instances.TryGetValue(instanceId, out var entry) ? entry.Host : null;
    }

    public async Task<IVirtualLlrpDeviceHost> CreateOrUpdateHostAsync(
        VirtualDeviceInstanceConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (_instances.TryGetValue(config.Id, out var existing) && existing.Host != null)
        {
            try
            {
                await existing.Host.StopAsync(cancellationToken).ConfigureAwait(false);
                await existing.Host.DisposeAsync().ConfigureAwait(false);
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

        if (entry.Host == null)
        {
            entry.Host = CreateHostFromConfig(entry.Config);
            _instances[instanceId] = entry;
        }

        _logger.LogInformation("Starting virtual device host '{Name}' on port {Port}...", entry.Config.Name, entry.Config.Port);
        await entry.Host.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (_instances.TryGetValue(instanceId, out var entry) && entry.Host != null)
        {
            _logger.LogInformation("Stopping virtual device host '{Name}'...", entry.Config.Name);
            await entry.Host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task RestartHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (_instances.TryGetValue(instanceId, out var entry) && entry.Host != null)
        {
            _logger.LogInformation("Restarting virtual device host '{Name}'...", entry.Config.Name);
            await entry.Host.RestartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task DeleteHostAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        if (_instances.TryRemove(instanceId, out var entry))
        {
            if (entry.Host != null)
            {
                try
                {
                    await entry.Host.StopAsync(cancellationToken).ConfigureAwait(false);
                    await entry.Host.DisposeAsync().ConfigureAwait(false);
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

    private static IVirtualLlrpDeviceHost CreateHostFromConfig(VirtualDeviceInstanceConfig config)
    {
        IPAddress ip = IPAddress.TryParse(config.ListenAddress, out var parsedIp)
            ? parsedIp
            : IPAddress.Any;

        var serverOptions = new LlrpDeviceServerOptions
        {
            ListenAddress = ip,
            Port = config.Port,
        };

        var tags = new List<VirtualTagDefinition>();
        if (config.Tags.Count == 0)
        {
            tags.Add(new VirtualTagDefinition
            {
                ElectronicProductCode = Convert.FromHexString("E28011606000020485984444"),
                Tid = Convert.FromHexString("E280116020007001"),
                AntennaId = 1,
                PeakRssi = -42,
            });
        }
        else
        {
            foreach (var t in config.Tags)
            {
                byte[] epcBytes;
                try { epcBytes = Convert.FromHexString(t.EpcHex); } catch { epcBytes = [0xE2, 0x80, 0x11, 0x60]; }
                byte[] tidBytes;
                try { tidBytes = Convert.FromHexString(t.TidHex); } catch { tidBytes = []; }

                tags.Add(new VirtualTagDefinition
                {
                    ElectronicProductCode = epcBytes,
                    Tid = tidBytes,
                    AntennaId = t.AntennaId,
                    PeakRssi = t.PeakRssi,
                });
            }
        }

        uint manId = 0;
        uint modelId = 0;
        if (config.DeviceProfile.Contains("Impinj", StringComparison.OrdinalIgnoreCase))
        {
            manId = 25882; // Impinj
            modelId = 2001004; // Speedway R420
        }
        else if (config.DeviceProfile.Contains("Zebra", StringComparison.OrdinalIgnoreCase))
        {
            manId = 10610; // Zebra
            modelId = 9600;
        }

        var identity = new LlrpDeviceIdentity
        {
            ReaderId = (ulong)Math.Abs(config.Id.GetHashCode()),
            Name = config.Name,
            ManufacturerId = manId,
            ModelId = modelId,
            FirmwareVersion = $"virtual-{config.ProtocolVersion}",
        };

        ushort maxAntennas = Math.Max((ushort)1, config.MaxAntennas);
        var capabilities = VirtualDeviceOptions.CreateDefaultCapabilities(maxAntennas);

        var deviceOptions = new VirtualDeviceOptions
        {
            Identity = identity,
            Capabilities = capabilities,
            Configuration = new LlrpDeviceConfiguration
            {
                Antennas = VirtualDeviceOptions.CreateDefaultAntennaConfigurations(maxAntennas),
            },
            Tags = tags,
            RfSimulation = new VirtualRfSimulationOptions
            {
                Scenario = config.Scenario,
                DetectionProbability = Math.Clamp(config.DetectionProbability, 0.0, 1.0),
                RssiJitterDb = Math.Max(0, config.RssiJitterDb),
                PresenceCycleRounds = Math.Max(1, config.PresenceCycleRounds),
            },
        };

        var hostOptions = new VirtualLlrpDeviceHostOptions
        {
            Server = serverOptions,
            Device = deviceOptions,
        };

        return new VirtualLlrpDeviceHost(hostOptions);
    }
}
