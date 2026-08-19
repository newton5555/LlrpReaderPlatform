using LlrpDevice.Virtual.Hosting;
using LlrpVirtualDevice.App.Wpf.Models;

namespace LlrpVirtualDevice.App.Wpf.Services;

public interface IVirtualDeviceManagerService : IAsyncDisposable
{
    IReadOnlyList<VirtualDeviceInstanceConfig> GetAllConfigs();
    IVirtualDeviceHost? GetHost(string instanceId);
    Task<IVirtualDeviceHost> CreateOrUpdateHostAsync(VirtualDeviceInstanceConfig config, CancellationToken cancellationToken = default);
    Task StartHostAsync(string instanceId, CancellationToken cancellationToken = default);
    Task StopHostAsync(string instanceId, CancellationToken cancellationToken = default);
    Task RestartHostAsync(string instanceId, CancellationToken cancellationToken = default);
    Task DeleteHostAsync(string instanceId, CancellationToken cancellationToken = default);
    Task StartAllAsync(CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
    Task SaveConfigsAsync(CancellationToken cancellationToken = default);
    Task LoadConfigsAsync(CancellationToken cancellationToken = default);
}
