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

        public Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid tagListId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Add_edit_remove_and_save_tags_round_trip_through_single_storage_container()
    {
        var store = new InMemoryTagListStore();
        using var vm = new TagListsViewModel(store);

        vm.EntryEpc = "30:08 33B2";
        vm.EntryDisplayName = "Door 1";
        vm.EntryColor = "#ABCDEF";
        vm.AddEntryCommand.Execute(null);

        Assert.Equal(1, vm.EntryCount);
        vm.Entries[0].DisplayName = "Door 2";
        await vm.SaveCommand.ExecuteAsync(null);

        IReadOnlyList<TagListDefinition> stored = await store.GetAllAsync();
        TagListDefinition container = Assert.Single(stored);
        Assert.True(container.IsEnabled);
        TagListEntry entry = Assert.Single(container.Entries);
        Assert.Equal("300833B2", entry.EpcHex);
        Assert.Equal("Door 2", entry.DisplayName);
        Assert.Equal("#ABCDEF", entry.ColorHex);

        vm.RemoveEntryCommand.Execute(vm.Entries[0]);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(Assert.Single(await store.GetAllAsync()).Entries);
    }

    [Fact]
    public async Task Loading_legacy_lists_flattens_entries_and_save_converges_to_one_container()
    {
        var store = new InMemoryTagListStore();
        await store.SaveAsync(CreateLegacyList("Doors", "3001", "Door 1", "#112233"));
        await store.SaveAsync(CreateLegacyList("Assets", "3002", "Forklift", "#445566"));
        using var vm = new TagListsViewModel(store);

        await vm.LoadCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.EntryCount);
        await vm.SaveCommand.ExecuteAsync(null);

        TagListDefinition container = Assert.Single(await store.GetAllAsync());
        Assert.Equal(2, container.Entries.Count);
    }

    [Fact]
    public async Task Save_rejects_invalid_duplicate_or_unnamed_tags()
    {
        var store = new InMemoryTagListStore();
        using var vm = new TagListsViewModel(store);
        vm.Entries.Add(new TagListEntryEditorItem(new TagListEntry
        {
            Id = Guid.NewGuid(),
            TagListId = Guid.Empty,
            EpcHex = "ZZZZ",
            DisplayName = "Invalid",
            ColorHex = "#123456",
        }));

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Contains("十六进制", vm.Status);
        Assert.Empty(await store.GetAllAsync());

        vm.Entries[0].EpcHex = "3008";
        vm.Entries[0].DisplayName = string.Empty;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Contains("Tag Name", vm.Status);
    }

    [Fact]
    public async Task Load_rejects_reentry_and_exposes_busy_state()
    {
        var store = new BlockingTagListStore();
        using var vm = new TagListsViewModel(store);

        Task first = vm.LoadCommand.ExecuteAsync(null);
        await store.Started.Task;
        Assert.True(vm.IsBusy);

        await vm.LoadCommand.ExecuteAsync(null);
        Assert.Equal("标签操作进行中，请稍候。", vm.Status);

        store.Release.TrySetResult(true);
        await first;
        Assert.False(vm.IsBusy);
    }

    private static TagListDefinition CreateLegacyList(
        string name,
        string epc,
        string tagName,
        string color)
    {
        Guid id = Guid.NewGuid();
        return new TagListDefinition
        {
            Id = id,
            Name = name,
            ColorHex = color,
            Entries =
            [
                new TagListEntry
                {
                    Id = Guid.NewGuid(),
                    TagListId = id,
                    EpcHex = epc,
                    DisplayName = tagName,
                    ColorHex = color,
                },
            ],
        };
    }
}
