using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Tagging;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class InventoryRunsViewModelTests
{
    [Fact]
    public async Task A_late_query_for_the_previous_reader_cannot_overwrite_the_current_reader()
    {
        Guid firstReaderId = Guid.NewGuid();
        Guid secondReaderId = Guid.NewGuid();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new DelayedInventoryRunStore(
            firstReaderId,
            secondReaderId,
            firstStarted,
            releaseFirst,
            secondLoaded);
        using var vm = new InventoryRunsViewModel(store);

        vm.SelectReader(firstReaderId);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.SelectReader(secondReaderId);
        releaseFirst.TrySetResult();
        await secondLoaded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (int i = 0; i < 20 && vm.IsBusy; i++)
        {
            await Task.Delay(10);
        }

        InventoryRunRowViewModel row = Assert.Single(vm.Runs);
        Assert.Equal(secondReaderId, row.Record.ReaderId);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task A_completed_inventory_lifecycle_automatically_refreshes_the_selected_reader()
    {
        Guid readerId = Guid.NewGuid();
        var firstLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new ReloadingInventoryRunStore(readerId, firstLoaded, secondLoaded);
        var inventory = new LifecycleInventoryService();
        using var vm = new InventoryRunsViewModel(store, inventory);

        vm.SelectReader(readerId);
        await firstLoaded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        inventory.PublishStopped(readerId);
        await secondLoaded.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (int i = 0; i < 20 && vm.IsBusy; i++)
        {
            await Task.Delay(10);
        }

        InventoryRunRowViewModel row = Assert.Single(vm.Runs);
        Assert.Equal("Gpi", row.StopReason);
        Assert.False(vm.IsBusy);
    }

    private sealed class DelayedInventoryRunStore(
        Guid firstReaderId,
        Guid secondReaderId,
        TaskCompletionSource firstStarted,
        TaskCompletionSource releaseFirst,
        TaskCompletionSource secondLoaded) : IInventoryRunStore
    {
        public async Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(
            Guid readerId,
            CancellationToken ct = default)
        {
            if (readerId == firstReaderId)
            {
                firstStarted.TrySetResult();
                await releaseFirst.Task;
                return [CreateRun(firstReaderId)];
            }

            if (readerId == secondReaderId)
            {
                secondLoaded.TrySetResult();
                return [CreateRun(secondReaderId)];
            }

            return [];
        }

        public Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default) => Task.CompletedTask;

        private static InventoryRunRecord CreateRun(Guid readerId) => new()
        {
            Id = Guid.NewGuid(),
            ReaderId = readerId,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    private sealed class ReloadingInventoryRunStore(
        Guid readerId,
        TaskCompletionSource firstLoaded,
        TaskCompletionSource secondLoaded) : IInventoryRunStore
    {
        private int readCount;

        public Task<IReadOnlyList<InventoryRunRecord>> GetForReaderAsync(
            Guid requestedReaderId,
            CancellationToken ct = default)
        {
            Assert.Equal(readerId, requestedReaderId);
            int count = Interlocked.Increment(ref readCount);
            if (count == 1)
            {
                firstLoaded.TrySetResult();
            }
            else
            {
                secondLoaded.TrySetResult();
            }

            return Task.FromResult<IReadOnlyList<InventoryRunRecord>>(
                [new InventoryRunRecord
                {
                    Id = Guid.NewGuid(),
                    ReaderId = readerId,
                    StartedAtUtc = DateTimeOffset.UtcNow,
                    StopReason = count == 1 ? "Running" : "Gpi",
                }]);
        }

        public Task SaveAsync(InventoryRunRecord run, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class LifecycleInventoryService : IInventoryService
    {
        public long DroppedTagReportCount => 0;

        public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged;

        event EventHandler<TagObservedEventArgs>? IInventoryService.TagObserved
        {
            add { }
            remove { }
        }

        event EventHandler<GpiObservedEventArgs>? IInventoryService.GpiChanged
        {
            add { }
            remove { }
        }

        public Task<StartInventoryResult> StartInventoryAsync(
            Guid readerId,
            InventorySpec spec,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default) => throw new NotSupportedException();

        public IReadOnlyList<TagObservation> GetTags(Guid readerId) => [];

        public void ClearTags(Guid readerId) { }

        public Task<IReadOnlyList<GpiPortStatus>> GetGpiStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GpoPortStatus>> GetGpoStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GpioStatusSnapshot> GetGpioStatusAsync(
            Guid readerId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TagAccessResult> ReadTagMemoryAsync(
            Guid readerId,
            TagReadRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TagAccessResult> WriteTagMemoryAsync(
            Guid readerId,
            TagWriteRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetGpoAsync(
            Guid readerId,
            GpioCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();

        public void PublishStopped(Guid readerId) => LifecycleChanged?.Invoke(
            this,
            new InventoryLifecycleChangedEventArgs(
                readerId,
                InventoryLifecycleState.Stopped,
                InventoryStopReason.Gpi));
    }
}
