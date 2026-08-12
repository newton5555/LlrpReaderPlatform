using LlrpReaderPlatform.Contracts.Persistence;

namespace LlrpReaderPlatform.TestKit;

public sealed class FakeInventoryRunStore : IInventoryRunStore
{
    private readonly List<InventoryRunRecord> runs = [];

    public IReadOnlyList<InventoryRunRecord> Runs => runs;

    public Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(Guid readerId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InventoryRunRecord>>(runs.Where(run => run.ReaderId == readerId).ToArray());

    public Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default)
    {
        int index = runs.FindIndex(existing => existing.Id == run.Id);
        if (index >= 0)
        {
            runs[index] = run;
        }
        else
        {
            runs.Add(run);
        }

        return Task.CompletedTask;
    }
}
