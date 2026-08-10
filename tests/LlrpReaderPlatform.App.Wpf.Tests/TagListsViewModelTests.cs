using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Services.Persistence;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class TagListsViewModelTests
{
    private sealed class BlockingTagListStore : ITagListStore
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Release.Task;
            return [];
        }

        public Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.FromResult<TagListDefinition?>(null);

        public Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task New_add_entry_and_save_round_trip_through_contract_store()
    {
        var vm = new TagListsViewModel(new InMemoryTagListStore());

        vm.NewCommand.Execute(null);
        vm.ListName = "Door tags";
        vm.EntryEpc = "30:08 33B2";
        vm.EntryDisplayName = "Door 1";
        vm.AddEntryCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);
        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Single(vm.Lists);
        Assert.Equal("Door tags", vm.Lists[0].Name);
        Assert.Single(vm.Entries);
        Assert.Equal("300833B2", vm.Entries[0].EpcHex);
    }

    [Fact]
    public async Task Save_rejects_invalid_or_duplicate_grid_edits()
    {
        var store = new InMemoryTagListStore();
        var vm = new TagListsViewModel(store);

        vm.NewCommand.Execute(null);
        vm.ListName = "Door tags";
        vm.EntryEpc = "3008";
        vm.AddEntryCommand.Execute(null);
        vm.Entries[0].EpcHex = "ZZZZ";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("十六进制", vm.Status);
        Assert.Empty(await store.GetAllAsync());

        vm.Entries[0].EpcHex = "300";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("必须是", vm.Status);
        Assert.Empty(await store.GetAllAsync());

        vm.Entries[0].EpcHex = "3008";
        var duplicate = new TagListEntryEditorItem(new LlrpReaderPlatform.Contracts.Persistence.TagListEntry
        {
            Id = Guid.NewGuid(),
            TagListId = Guid.Empty,
            EpcHex = "3008",
        });
        vm.Entries.Add(duplicate);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("重复", vm.Status);
        Assert.Empty(await store.GetAllAsync());
    }

    [Fact]
    public async Task Load_rejects_reentry_and_exposes_busy_state()
    {
        var store = new BlockingTagListStore();
        var vm = new TagListsViewModel(store);

        Task first = vm.LoadCommand.ExecuteAsync(null);
        await store.Started.Task;
        Assert.True(vm.IsBusy);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("Tag List 操作进行中，请稍候。", vm.Status);

        store.Release.TrySetResult(true);
        await first;
        Assert.False(vm.IsBusy);
    }
}
