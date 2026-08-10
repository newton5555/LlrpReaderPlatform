using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Services.Persistence;

public sealed class NullInventoryTagLog : IInventoryTagLog
{
    public Task<string?> StartAsync(InventoryRunRecord run, CancellationToken ct = default) =>
        Task.FromResult<string?>(null);

    public Task AppendAsync(InventoryRunRecord run, TagObservation tag, CancellationToken ct = default) =>
        Task.CompletedTask;

    public Task CompleteAsync(InventoryRunRecord run, CancellationToken ct = default) =>
        Task.CompletedTask;
}
