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
    public async Task Read_without_reader_prompts_before_service_call()
    {
        var h = new Harness();
        var vm = new TagMemoryViewModel(h.Manager) { Epc = "3001" };

        await vm.ReadCommand.ExecuteAsync(null);

        Assert.Equal("请先从左侧选择 Reader。", vm.Result);
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
}
