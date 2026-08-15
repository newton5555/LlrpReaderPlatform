using System.Collections.Concurrent;

namespace LlrpReaderPlatform.VirtualReader;

/// <summary>
/// 进程内虚拟 Reader 场景目录。它只在开发/测试组合根使用，真实 Reader 不依赖它。
/// </summary>
public sealed class VirtualReaderCatalog
{
    private readonly ConcurrentDictionary<Guid, VirtualInventoryDataset> datasets = new();
    private readonly ConcurrentDictionary<Guid, VirtualReaderDeviceState> deviceStates = new();

    public void Register(VirtualInventoryDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        Guid readerId = dataset.Scenario.ReaderId;
        datasets[readerId] = dataset;
        deviceStates.TryRemove(readerId, out _);
    }

    public bool Remove(Guid readerId)
    {
        deviceStates.TryRemove(readerId, out _);
        return datasets.TryRemove(readerId, out _);
    }

    public bool TryGet(Guid readerId, out VirtualInventoryDataset? dataset) =>
        datasets.TryGetValue(readerId, out dataset);

    public VirtualInventoryDataset GetRequired(Guid readerId) =>
        datasets.TryGetValue(readerId, out VirtualInventoryDataset? dataset)
            ? dataset
            : throw new InvalidOperationException(
                $"No virtual Reader scenario is registered for ReaderProfile '{readerId}'.");

    internal VirtualReaderDeviceState GetOrCreateState(Guid readerId) =>
        deviceStates.GetOrAdd(readerId, static (id, source) =>
        {
            if (!source.datasets.TryGetValue(id, out _))
            {
                throw new InvalidOperationException(
                    $"No virtual Reader scenario is registered for ReaderProfile '{id}'.");
            }

            return new VirtualReaderDeviceState();
        }, this);

    public IReadOnlyList<VirtualInventoryDataset> GetAll() => datasets.Values.ToArray();
}
