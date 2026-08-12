using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Discovery;
using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class ViewModelDisposalTests
{
    [Fact]
    public async Task Changing_reader_context_cancels_old_settings_query_and_clears_rows()
    {
        var settings = new BlockingSettingsService();
        var vm = new ReaderSettingsViewModel(settings);
        Guid firstReaderId = Guid.NewGuid();
        Guid secondReaderId = Guid.NewGuid();

        vm.SetReaderContext(CreateReader(firstReaderId, "Reader A"));
        Task firstLoad = vm.LoadCommand.ExecuteAsync(firstReaderId);
        await settings.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.SetReaderContext(CreateReader(secondReaderId, "Reader B"));
        await firstLoad.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(settings.CancellationObserved);
        Assert.Equal(secondReaderId, vm.ReaderId);
        Assert.Empty(vm.Rows);
        Assert.Contains("切换", vm.Status);
    }

    [Fact]
    public async Task Explicit_settings_refresh_replaces_previous_query()
    {
        var settings = new ReplacingSettingsService();
        var vm = new ReaderSettingsViewModel(settings);
        Guid readerId = Guid.NewGuid();

        vm.SetReaderContext(CreateReader(readerId, "Reader"));
        Task firstLoad = vm.LoadCommand.ExecuteAsync(readerId);
        await settings.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Task secondLoad = vm.LoadCommand.ExecuteAsync(readerId);
        await Task.WhenAll(firstLoad, secondLoad).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.True(settings.FirstCancellationObserved);
        Assert.Equal(2, settings.QueryCount);
        Assert.Single(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.Equal("设置已加载（可编辑）", vm.Status);
    }

    [Fact]
    public async Task Main_dispose_cancels_settings_and_tag_list_page_operations()
    {
        var settings = new BlockingSettingsService();
        var tagLists = new BlockingTagListStore();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        var vm = CreateMainViewModel(
            manager,
            settings,
            new EmptyDiscovery(),
            new InMemoryAppSettingsStore(),
            tagLists,
            new InMemoryInventoryRunStore());

        Task settingsLoad = vm.Settings.LoadCommand.ExecuteAsync(Guid.NewGuid());
        await settings.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task tagListLoad = vm.TagLists.LoadCommand.ExecuteAsync(null);
        await tagLists.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.Dispose();
        vm.Dispose();

        await Task.WhenAll(settingsLoad, tagListLoad).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(settings.CancellationObserved);
        Assert.True(tagLists.CancellationObserved);
    }

    [Fact]
    public async Task Navigating_away_cancels_tag_list_page_operation()
    {
        var settings = new BlockingSettingsService();
        var tagLists = new BlockingTagListStore();
        await using var manager = new ReaderManager(new FakeSessionFactory(), new FakeProfileStore());
        using var vm = CreateMainViewModel(
            manager,
            settings,
            new EmptyDiscovery(),
            new InMemoryAppSettingsStore(),
            tagLists,
            new InMemoryInventoryRunStore());

        vm.NavigateCommand.Execute("TagLists");
        await tagLists.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.NavigateCommand.Execute("About");
        for (int i = 0; i < 40 && (!tagLists.CancellationObserved || vm.TagLists.IsBusy); i++)
        {
            await Task.Delay(10);
        }

        Assert.Same(vm.About, vm.CurrentPage);
        Assert.True(tagLists.CancellationObserved);
        Assert.False(vm.TagLists.IsBusy);
    }

    [Fact]
    public async Task Navigating_away_cancels_shell_settings_load()
    {
        var settings = new BlockingSettingsService();
        var factory = new FakeSessionFactory();
        var profile = new ReaderProfile
        {
            Id = Guid.NewGuid(),
            Host = "192.0.2.61",
            Name = "Shell Reader",
        };
        factory.Queue.Enqueue(new FakeSession()); // Probe during registration.
        await using var manager = new ReaderManager(factory, new FakeProfileStore());
        await manager.AddAsync(profile, enableAfterAdding: false);
        using var vm = CreateMainViewModel(
            manager,
            settings,
            new EmptyDiscovery(),
            new InMemoryAppSettingsStore(),
            new InMemoryTagListStore(),
            new InMemoryInventoryRunStore());

        vm.Refresh();
        ReaderItemViewModel item = Assert.Single(vm.Readers);
        Task openSettings = vm.OpenReaderSettingsCommand.ExecuteAsync(item);
        await settings.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.NavigateCommand.Execute("About");
        await openSettings.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Same(vm.About, vm.CurrentPage);
        Assert.True(settings.CancellationObserved);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Disposed_tag_list_page_swallows_a_late_store_failure()
    {
        var store = new LateFailureTagListStore();
        using var vm = new TagListsViewModel(store);

        Task load = vm.LoadCommand.ExecuteAsync(null);
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.Dispose();
        store.Release.TrySetResult(true);

        await load.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static ReaderItemViewModel CreateReader(Guid id, string name) =>
        new(new ReaderRuntimeSnapshot
        {
            ReaderId = id,
            Profile = new ReaderProfile { Id = id, Name = name, Host = "192.0.2.1" },
            State = ReaderState.Disconnected,
        });

    private static MainViewModel CreateMainViewModel(
        IReaderManager readerManager,
        IReaderSettingsService settings,
        IReaderDiscoveryService discovery,
        IAppSettingsStore appSettings,
        ITagListStore tagLists,
        IInventoryRunStore inventoryRuns)
    {
        IInventoryService inventory = (IInventoryService)readerManager;
        var diagnostics = new DiagnosticsViewModel(inventory);
        return new MainViewModel(
            readerManager,
            discovery,
            new ReaderSettingsViewModel(settings, diagnostics, readerManager),
            new InventoryViewModel(inventory, tagLists, readerManager),
            new TagMemoryViewModel(inventory),
            diagnostics,
            new AboutViewModel(),
            new AppSettingsViewModel(appSettings),
            new TagListsViewModel(tagLists),
            new InventoryRunsViewModel(inventoryRuns, inventory),
            new AddDataSourceViewModel(readerManager, discovery));
    }

    private sealed class EmptyDiscovery : IReaderDiscoveryService
    {
        public Task<IReadOnlyList<DiscoveredReader>> DiscoverAsync(
            TimeSpan scanDuration,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DiscoveredReader>>([]);
    }

    private sealed class BlockingSettingsService : IReaderSettingsService
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<SettingsEditorModel> QueryAsync(Guid readerId, CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The blocking query should be cancelled.");
        }

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid readerId, CancellationToken ct = default) =>
            QueryAsync(readerId, ct);

        public SettingsValidationResult Validate(SettingsDraft draft) =>
            throw new NotSupportedException();

        public Task<SettingsApplyResult> ApplyAsync(
            Guid readerId,
            SettingsDraft draft,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReplacingSettingsService : IReaderSettingsService
    {
        private int queryCount;

        public TaskCompletionSource<bool> FirstStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FirstCancellationObserved { get; private set; }
        public int QueryCount => Volatile.Read(ref queryCount);

        public async Task<SettingsEditorModel> QueryAsync(Guid readerId, CancellationToken ct = default)
        {
            int query = Interlocked.Increment(ref queryCount);
            if (query == 1)
            {
                FirstStarted.TrySetResult(true);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    FirstCancellationObserved = true;
                    throw;
                }
            }

            return new SettingsEditorModel(
                new EffectiveSettingsLayout
                {
                    ReaderId = readerId,
                    CapabilityRevision = 1,
                    Entries =
                    [
                        new SettingsEntry
                        {
                            Key = "session",
                            Title = "Session",
                            EditorKind = EditorKind.Integer,
                            ValueType = typeof(int),
                            CurrentValue = 0,
                        },
                    ],
                },
                new SettingsSnapshot
                {
                    ReaderId = readerId,
                    CapabilityRevision = 1,
                    Values = new Dictionary<string, object?> { ["session"] = 0 },
                });
        }

        public Task<SettingsEditorModel> GetDefaultsAsync(Guid readerId, CancellationToken ct = default) =>
            QueryAsync(readerId, ct);

        public SettingsValidationResult Validate(SettingsDraft draft) => new(true);

        public Task<SettingsApplyResult> ApplyAsync(
            Guid readerId,
            SettingsDraft draft,
            CancellationToken ct = default) =>
            Task.FromResult(new SettingsApplyResult(true));
    }

    private sealed class BlockingTagListStore : ITagListStore
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CancellationObserved { get; private set; }

        public async Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            return [];
        }

        public Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.FromResult<TagListDefinition?>(null);

        public Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class LateFailureTagListStore : ITagListStore
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<IReadOnlyList<TagListDefinition>> GetAllAsync(CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Release.Task;
            throw new IOException("late store failure");
        }

        public Task<TagListDefinition?> GetAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.FromResult<TagListDefinition?>(null);

        public Task SaveAsync(TagListDefinition tagList, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(Guid tagListId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
