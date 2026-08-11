using System.IO;
using LlrpReaderPlatform.App.Wpf.ViewModels;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Tagging;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.TestKit;
using Xunit;

namespace LlrpReaderPlatform.App.Wpf.Tests;

public sealed class TagMemoryViewModelTests
{
    private sealed class Harness
    {
        public Harness()
        {
            SessionFactory = new FakeSessionFactory();
            Manager = new ReaderManager(SessionFactory, new FakeProfileStore());
            Profile = new LlrpReaderPlatform.Contracts.Readers.ReaderProfile
            {
                Id = Guid.NewGuid(),
                Host = "10.0.0.9",
                Name = "Tag",
            };
        }

        public FakeSessionFactory SessionFactory { get; }
        public ReaderManager Manager { get; }
        public LlrpReaderPlatform.Contracts.Readers.ReaderProfile Profile { get; }

        /// <summary>入队 sessions（第一个作为 Probe，最后一个作为注册/盘存会话），执行 AddAsync。</summary>
        public FakeSession Register(params FakeSession[] sessions)
        {
            foreach (FakeSession s in sessions)
            {
                SessionFactory.Queue.Enqueue(s);
            }

            Manager.AddAsync(Profile, enableAfterAdding: false).GetAwaiter().GetResult();
            return sessions.Length > 0 ? sessions[^1] : new FakeSession();
        }
    }

    [Fact]
    public async Task Read_success_populates_datahex()
    {
        var h = new Harness();
        FakeSession session = new()
        {
            TagAccessResult = new TagAccessResult(Succeeded: true, DataHex: "0AB0"),
        };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001", SelectionBank = TagMemoryBank.Tid };

        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);

        Assert.Equal("0AB0", vm.DataHex);
        Assert.Contains("成功", vm.Result);
        Assert.Equal(TagMemoryBank.Tid, session.LastTagReadRequest?.SelectionBank);
        Assert.Equal(
            new[] { TagMemoryBank.Epc, TagMemoryBank.Tid, TagMemoryBank.User, TagMemoryBank.Reserved },
            vm.MemoryBanks);
    }

    [Fact]
    public async Task Read_without_epc_prompts()
    {
        var h = new Harness();
        h.Register(new FakeSession(), new FakeSession());
        var vm = new TagMemoryViewModel(h.Manager) { Epc = string.Empty };

        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);

        Assert.Equal("请先填写 EPC。", vm.Result);
    }

    [Fact]
    public async Task Read_rejects_invalid_word_pointer_before_service_call()
    {
        var h = new Harness();
        FakeSession session = new();
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager)
        {
            Epc = "3001",
            WordPointer = "not-a-number",
        };

        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);

        Assert.Equal("Word pointer 必须是 0 到 65535 的整数。", vm.Result);
        Assert.Equal(0, session.ReadTagMemoryCount);
    }

    [Fact]
    public async Task Write_uses_data_length_instead_of_word_count()
    {
        var h = new Harness();
        FakeSession session = new();
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager)
        {
            Epc = "3001",
            DataHex = "0001",
            WordCount = "0",
        };

        await vm.WriteCommand.ExecuteAsync(h.Profile.Id);

        Assert.Equal("写入成功。", vm.Result);
        Assert.Equal(1, session.WriteTagMemoryCount);
    }

    [Fact]
    public async Task Read_without_reader_prompts_before_service_call()
    {
        var h = new Harness();
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("请先从左侧选择 Reader。", vm.Result);
        Assert.False(vm.IsReaderSelected);
    }

    [Fact]
    public async Task Write_success_sets_result()
    {
        var h = new Harness();
        FakeSession session = new() { TagAccessResult = new TagAccessResult(Succeeded: true) };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001", DataHex = "0102" };

        await vm.WriteCommand.ExecuteAsync(h.Profile.Id);

        Assert.Equal("写入成功。", vm.Result);
        Assert.Equal(TagMemoryBank.Epc, session.LastTagWriteRequest?.SelectionBank);
        Assert.Equal(TagMemoryBank.User, session.LastTagWriteRequest?.MemoryBank);
    }

    [Fact]
    public async Task Read_device_failure_is_projected_to_result()
    {
        var h = new Harness();
        FakeSession session = new() { TagAccessResult = new TagAccessResult(false, "tag not found") };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };

        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);

        Assert.Contains("tag not found", vm.Result);
        Assert.Contains("设备错误", vm.Result);
    }

    [Fact]
    public async Task Known_missing_tag_access_capability_disables_ui_operation()
    {
        var h = new Harness();
        h.Register(new FakeSession(), new FakeSession());
        var reader = new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = h.Profile.Id,
            Profile = h.Profile,
            State = ReaderState.Disconnected,
            CapabilityRevision = 1,
            IsStale = false,
            FeatureCatalog = new ReaderFeatureCatalog
            {
                SupportedFeatures = [ReaderFeatures.StandardInventory],
            },
        });
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };

        vm.SetReaderContext(reader);
        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);

        Assert.False(vm.IsTagAccessAvailable);
        Assert.Equal("当前 Reader 未声明标准 Tag Access 能力。", vm.Result);
    }

    [Fact]
    public async Task Read_rejects_reentry_and_exposes_busy_state()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSession session = new();
        session.BeforeReadTagMemoryAsync = async () =>
        {
            started.TrySetResult(true);
            await release.Task;
        };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };

        Task first = vm.ReadCommand.ExecuteAsync(h.Profile.Id);
        await started.Task;
        Assert.True(vm.IsBusy);

        await vm.ReadCommand.ExecuteAsync(h.Profile.Id);
        Assert.Equal("Tag 操作进行中，请稍候。", vm.Result);
        Assert.Equal(0, session.ReadTagMemoryCount);

        release.TrySetResult(true);
        await first;
        Assert.False(vm.IsBusy);
        Assert.Equal(1, session.ReadTagMemoryCount);
    }

    [Fact]
    public async Task Read_result_is_discarded_when_reader_context_changes()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSession session = new()
        {
            TagAccessResult = new TagAccessResult(Succeeded: true, DataHex: "0AB0"),
        };
        session.BeforeReadTagMemoryAsync = async () =>
        {
            started.TrySetResult(true);
            await release.Task;
        };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = h.Profile.Id,
            Profile = h.Profile,
            State = ReaderState.Disconnected,
        }));

        Task read = vm.ReadCommand.ExecuteAsync(h.Profile.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = Guid.NewGuid(),
            Profile = new ReaderProfile { Id = Guid.NewGuid(), Name = "Other", Host = "192.0.2.2" },
            State = ReaderState.Disconnected,
        }));
        release.TrySetResult(true);
        await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(vm.Result);
        Assert.Empty(vm.DataHex);
    }

    [Fact]
    public async Task Read_result_is_discarded_when_same_reader_capability_changes()
    {
        var h = new Harness();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSession session = new()
        {
            TagAccessResult = new TagAccessResult(Succeeded: true, DataHex: "0AB0"),
        };
        session.BeforeReadTagMemoryAsync = async () =>
        {
            started.TrySetResult(true);
            await release.Task;
        };
        h.Register(new FakeSession(), session);
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = h.Profile.Id,
            Profile = h.Profile,
            State = ReaderState.Connected,
            CapabilityRevision = 1,
            IsStale = false,
        }));

        Task read = vm.ReadCommand.ExecuteAsync(h.Profile.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        vm.SetReaderContext(new ReaderItemViewModel(new ReaderRuntimeSnapshot
        {
            ReaderId = h.Profile.Id,
            Profile = h.Profile,
            State = ReaderState.Connected,
            CapabilityRevision = 2,
            IsStale = false,
        }));
        release.TrySetResult(true);
        await read.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Null(vm.Result);
        Assert.Empty(vm.DataHex);
    }

    [Fact]
    public async Task Disposed_page_swallows_a_late_read_failure()
    {
        var service = new LateFailureInventoryService();
        using var vm = new TagMemoryViewModel(service)
        {
            Epc = "3001",
        };
        Guid readerId = Guid.NewGuid();

        Task read = vm.ReadCommand.ExecuteAsync(readerId);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        vm.Dispose();
        service.Release.TrySetResult(true);

        await read.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private sealed class LateFailureInventoryService : IInventoryService
    {
        public TaskCompletionSource<bool> Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public long DroppedTagReportCount => 0;

        public event EventHandler<TagObservedEventArgs>? TagObserved
        {
            add { }
            remove { }
        }

        public event EventHandler<InventoryLifecycleChangedEventArgs>? LifecycleChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<GpiObservedEventArgs>? GpiChanged
        {
            add { }
            remove { }
        }

        public async Task<TagAccessResult> ReadTagMemoryAsync(
            Guid readerId,
            TagReadRequest request,
            CancellationToken ct = default)
        {
            Started.TrySetResult(true);
            await Release.Task;
            throw new IOException("late tag access failure");
        }

        public Task<StartInventoryResult> StartInventoryAsync(
            Guid readerId,
            InventorySpec spec,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task StopInventoryAsync(Guid readerId, CancellationToken ct = default) =>
            throw new NotSupportedException();

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

        public Task<TagAccessResult> WriteTagMemoryAsync(
            Guid readerId,
            TagWriteRequest request,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task SetGpoAsync(
            Guid readerId,
            GpioCommand command,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
