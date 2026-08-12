using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.TestKit;
using SdkReaderSettings = LlrpSdk.ReaderSettings;
using SdkReaderSettingsSnapshot = LlrpSdk.ReaderSettingsSnapshot;
using Xunit;

namespace LlrpReaderPlatform.Services.Tests.Settings;

public sealed class SettingsServiceTests
{
    private sealed class Harness
    {
        public Harness()
        {
            SessionFactory = new FakeSessionFactory();
            Manager = new ReaderManager(SessionFactory, new FakeProfileStore());
            Compiler = new StandardSettingsCompiler();
            Settings = new SettingsService(Manager, Compiler);
            Profile = new ReaderProfile { Id = Guid.NewGuid(), Host = "192.0.2.2" };
            SessionFactory.Queue.Enqueue(new FakeSession()); // probe
            RegisterSession = new FakeSession();
            SessionFactory.Queue.Enqueue(RegisterSession); // register
        }

        public FakeSessionFactory SessionFactory { get; }
        public ReaderManager Manager { get; }
        public StandardSettingsCompiler Compiler { get; }
        public SettingsService Settings { get; }
        public ReaderProfile Profile { get; }
        public FakeSession RegisterSession { get; }
    }

    [Fact]
    public async Task QueryAsync_returns_readonly_when_activation_fails()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        h.RegisterSession.ConnectThrows = new IOException("offline");

        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.True(model.Layout.Entries.All(static e => e.IsReadOnly));
    }

    [Fact]
    public async Task QueryAsync_retries_activation_when_capability_is_missing()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);

        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);

        Assert.True(model.Layout.HasEditableSettings);
        Assert.True(model.Snapshot.CapabilityRevision > 0);

        await h.Manager.DeactivateAsync(h.Profile.Id);

        SettingsEditorModel recovered = await h.Settings.QueryAsync(h.Profile.Id);

        Assert.True(recovered.Layout.HasEditableSettings);
        Assert.True(recovered.Snapshot.CapabilityRevision > 0);
    }

    [Fact]
    public async Task QueryAsync_cancellation_propagates_instead_of_using_offline_fallback()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        using var cancellation = new CancellationTokenSource();
        h.RegisterSession.BeforeQuerySettings = cancellation.Cancel;
        h.RegisterSession.SettingsQueryThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Settings.QueryAsync(h.Profile.Id, cancellation.Token));

        var fallbackHarness = new Harness();
        await fallbackHarness.Manager.AddAsync(fallbackHarness.Profile, enableAfterAdding: false);
        await fallbackHarness.Manager.ActivateAsync(fallbackHarness.Profile.Id);
        fallbackHarness.RegisterSession.SettingsQueryThrows = new IOException("offline");
        using var fallbackCancellation = new CancellationTokenSource();
        var cancellingPresetStore = new CancellingSettingsPresetStore(fallbackCancellation);
        var fallbackSettings = new SettingsService(
            fallbackHarness.Manager,
            fallbackHarness.Compiler,
            fallbackHarness.Manager,
            cancellingPresetStore);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => fallbackSettings.QueryAsync(fallbackHarness.Profile.Id, fallbackCancellation.Token));
    }

    [Fact]
    public async Task QueryAsync_marks_model_readonly_when_short_operation_disconnect_fails()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        h.RegisterSession.DisconnectThrows = new IOException("disconnect failed");

        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.All(model.Layout.Entries, static entry => Assert.True(entry.IsReadOnly));
        Assert.Contains(
            model.Layout.Entries,
            static entry => entry.ReadOnlyReason?.Contains("重新连接", StringComparison.Ordinal) == true);
        Assert.True(h.Manager.GetSnapshot(h.Profile.Id).IsStale);
    }

    [Fact]
    public async Task QueryAsync_caches_the_full_tab_one_layout_for_offline_reopen()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        var presets = new InMemorySettingsPresetStore();
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        SettingsEditorModel live = await settings.QueryAsync(h.Profile.Id);
        h.RegisterSession.SettingsQueryThrows = new IOException("offline");

        SettingsEditorModel offline = await settings.QueryAsync(h.Profile.Id);

        Assert.True(live.Layout.Entries.Count > 20);
        Assert.Equal(live.Layout.Entries.Count, offline.Layout.Entries.Count);
        Assert.Contains(offline.Layout.Entries, static entry => entry.Key == SettingsKeys.FilterEnabled(1));
        Assert.Contains(offline.Layout.Entries, static entry => entry.Key == SettingsKeys.StartGpiEnabled);
        Assert.Contains(offline.Layout.Entries, static entry => entry.Key == SettingsKeys.ReportRssi);
        Assert.All(offline.Layout.Entries, static entry => Assert.True(entry.IsReadOnly));
    }

    [Fact]
    public async Task GetDefaultsAsync_projects_sdk_defaults_through_the_same_layout()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);

        LlrpSdk.ReaderSettingsDefaults defaults = LlrpSdk.ReaderSettingsDefaults.CreateGeneric() with
        {
            Settings = new LlrpSdk.ReaderSettings
            {
                Inventory = new LlrpSdk.InventorySettings
                {
                    Session = 3,
                    TagPopulationEstimate = 99,
                    ReportEveryNTags = 7,
                },
            },
        };
        h.RegisterSession.SettingsDefaults = defaults;

        SettingsEditorModel model = await h.Settings.GetDefaultsAsync(h.Profile.Id);

        Assert.Equal(3, model.Snapshot.Values[SettingsKeys.Session]);
        Assert.Equal(99, model.Snapshot.Values[SettingsKeys.TagPopulation]);
        Assert.Equal(7, model.Snapshot.Values[SettingsKeys.ReportEvery]);
        Assert.False(h.RegisterSession.IsConnected);
    }

    [Fact]
    public async Task Validate_rejects_expired_capability()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        long revision = (await h.Settings.QueryAsync(h.Profile.Id)).Snapshot.CapabilityRevision;

        var stale = new SettingsDraft { ReaderId = h.Profile.Id, CapabilityRevision = revision + 1 };

        SettingsValidationResult result = h.Settings.Validate(stale);
        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Validate_rejects_out_of_range_value()
    {
        var h = new Harness();
        h.RegisterSession.SetCapabilities(maxNumberOfAntennas: 1);
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);

        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }

        draft.Values[SettingsKeys.TxPowerIndex] = 65536; // 超出 ushort index 范围
        SettingsValidationResult result = h.Settings.Validate(draft);
        Assert.False(result.IsValid);

        draft.Values[SettingsKeys.TxPowerIndex] = "not-a-number";
        result = h.Settings.Validate(draft);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Key == SettingsKeys.TxPowerIndex);

        draft.Values[SettingsKeys.AntennaIds] = string.Empty;
        result = h.Settings.Validate(draft);
        Assert.False(result.IsValid);
        Assert.Contains(result.Issues, issue => issue.Key == SettingsKeys.AntennaIds);

        // Query 后 Validate 必须使用完整的实时布局，不能只校验最初的三项核心设置。
        draft.Values[SettingsKeys.ReportRssi] = "not-a-boolean";
        result = h.Settings.Validate(draft);
        Assert.False(result.IsValid);

        draft.Values["unsupported-vendor-setting"] = true;
        result = h.Settings.Validate(draft);
        Assert.Contains(result.Issues, issue => issue.Key == "unsupported-vendor-setting");
    }

    [Fact]
    public async Task ApplyAsync_rejects_expired_capability()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        long revision = (await h.Settings.QueryAsync(h.Profile.Id)).Snapshot.CapabilityRevision;

        var stale = new SettingsDraft { ReaderId = h.Profile.Id, CapabilityRevision = revision + 1 };

        SettingsApplyResult result = await h.Settings.ApplyAsync(h.Profile.Id, stale);
        Assert.False(result.Succeeded);
        Assert.Equal(PlatformErrorCode.StaleCapability, result.ErrorCode);
    }

    [Fact]
    public async Task ApplyAsync_rechecks_capability_after_preflight_query()
    {
        Guid readerId = Guid.NewGuid();
        var manager = new CapabilityChangingReaderManager(readerId);
        var runtime = new CapabilityChangingSettingsRuntime();
        var service = new SettingsService(
            manager,
            new EmptySdkSettingsCompiler(),
            runtime);
        var draft = new SettingsDraft
        {
            ReaderId = readerId,
            CapabilityRevision = 1,
        };

        SettingsApplyResult result = await service.ApplyAsync(readerId, draft);

        Assert.False(result.Succeeded);
        Assert.Equal(PlatformErrorCode.StaleCapability, result.ErrorCode);
        Assert.Equal(0, runtime.ApplyCount);
    }

    [Fact]
    public async Task ApplyAsync_succeeds_with_valid_draft()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);

        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }

        SettingsApplyResult result = await h.Settings.ApplyAsync(h.Profile.Id, draft);
        Assert.True(result.Succeeded, result.Error);

        var failingPresetStore = new ThrowingSettingsPresetStore();
        var settingsWithFailingPreset = new SettingsService(
            h.Manager,
            h.Compiler,
            h.Manager,
            failingPresetStore);
        await settingsWithFailingPreset.QueryAsync(h.Profile.Id);
        int saveCountBeforeApply = failingPresetStore.SaveCount;
        SettingsApplyResult deviceSuccessWithCacheFailure = await settingsWithFailingPreset.ApplyAsync(
            h.Profile.Id,
            draft);
        Assert.True(deviceSuccessWithCacheFailure.Succeeded, deviceSuccessWithCacheFailure.Error);
        Assert.Equal(saveCountBeforeApply + 1, failingPresetStore.SaveCount);

        // Filter mask 的格式错误应转换为 ApplyResult，而不是把编译异常抛到 UI。
        draft.Values[SettingsKeys.FilterEnabled(1)] = true;
        draft.Values[SettingsKeys.FilterMask(1)] = "GG";
        SettingsApplyResult invalid = await h.Settings.ApplyAsync(h.Profile.Id, draft);
        Assert.False(invalid.Succeeded);
        Assert.Contains("设置编译失败", invalid.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, invalid.ErrorCode);
    }

    private sealed class ThrowingSettingsPresetStore : IReaderSettingsPresetStore
    {
        public int SaveCount { get; private set; }

        public Task<ReaderSettingsPreset?> GetAsync(Guid readerId, CancellationToken ct = default) =>
            Task.FromResult<ReaderSettingsPreset?>(null);

        public Task SaveAsync(ReaderSettingsPreset preset, CancellationToken ct = default)
        {
            SaveCount++;
            throw new IOException("local preset unavailable");
        }

        public Task DeleteAsync(Guid readerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CapabilityChangingReaderManager : IReaderManager
    {
        private readonly ReaderRuntimeSnapshot snapshot;
        private int snapshotCalls;

        public CapabilityChangingReaderManager(Guid readerId)
        {
            snapshot = new ReaderRuntimeSnapshot
            {
                ReaderId = readerId,
                Profile = new ReaderProfile
                {
                    Id = readerId,
                    Host = "192.0.2.50",
                },
                State = ReaderState.Disconnected,
                IsStale = false,
                CapabilityRevision = 1,
            };
        }

        public IReadOnlyList<ReaderRuntimeSnapshot> Readers => [snapshot];

        public event EventHandler<ReaderStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ReaderRuntimeSnapshot GetSnapshot(Guid readerId)
        {
            Assert.Equal(snapshot.ReaderId, readerId);
            int call = Interlocked.Increment(ref snapshotCalls);
            return call >= 3
                ? snapshot with { CapabilityRevision = 2 }
                : snapshot;
        }

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<ReaderAddResult> AddAsync(
            ReaderProfile profile,
            bool enableAfterAdding,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ReaderProbeResult> ProbeAsync(
            ReaderProfile profile,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid readerId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task SetEnabledAsync(Guid readerId, bool enabled, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ReaderActivationResult> ActivateAsync(Guid readerId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeactivateAsync(Guid readerId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapabilityChangingSettingsRuntime : IReaderSettingsRuntime
    {
        private static readonly ReaderSettingsRuntimeSnapshot RuntimeSnapshot =
            new(new SdkReaderSettingsSnapshot(new SdkReaderSettings(), ManagedRoSpec: null), null);

        public int ApplyCount { get; private set; }

        public Task<ReaderSettingsRuntimeSnapshot> QueryAsync(
            Guid readerId,
            CancellationToken ct = default) =>
            Task.FromResult(RuntimeSnapshot);

        public Task<ReaderSettingsRuntimeSnapshot> GetDefaultsAsync(
            Guid readerId,
            CancellationToken ct = default) =>
            Task.FromResult(RuntimeSnapshot);

        public Task ApplyAsync(
            Guid readerId,
            Func<ReaderSettingsRuntimeSnapshot, SdkReaderSettings> compile,
            CancellationToken ct = default)
        {
            ApplyCount++;
            _ = compile(RuntimeSnapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class EmptySdkSettingsCompiler : ISettingsCompiler, ISdkSettingsCompiler
    {
        public EffectiveSettingsLayout BuildLayout(ReaderRuntimeSnapshot snapshot) =>
            CreateLayout(snapshot);

        public SettingsSnapshot BuildSnapshot(ReaderRuntimeSnapshot snapshot) =>
            new()
            {
                ReaderId = snapshot.ReaderId,
                CapabilityRevision = snapshot.CapabilityRevision,
                Values = new Dictionary<string, object?>(),
            };

        public CompiledSettings Compile(SettingsDraft draft, EffectiveSettingsLayout layout) =>
            new();

        public EffectiveSettingsLayout BuildLayout(
            ReaderRuntimeSnapshot snapshot,
            ReaderSettingsRuntimeSnapshot runtime) =>
            CreateLayout(snapshot);

        public SettingsSnapshot BuildSnapshot(
            ReaderRuntimeSnapshot snapshot,
            ReaderSettingsRuntimeSnapshot runtime) =>
            BuildSnapshot(snapshot);

        public SdkReaderSettings CompileSdk(
            SettingsDraft draft,
            EffectiveSettingsLayout layout,
            ReaderSettingsRuntimeSnapshot runtime,
            ReaderRuntimeSnapshot reader) =>
            new();

        private static EffectiveSettingsLayout CreateLayout(ReaderRuntimeSnapshot snapshot) =>
            new()
            {
                ReaderId = snapshot.ReaderId,
                CapabilityRevision = snapshot.CapabilityRevision,
                Entries = [],
            };
    }

    private sealed class CancellingSettingsPresetStore(CancellationTokenSource cancellation)
        : IReaderSettingsPresetStore
    {
        public Task<ReaderSettingsPreset?> GetAsync(Guid readerId, CancellationToken ct = default)
        {
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<ReaderSettingsPreset?>(null);
        }

        public Task SaveAsync(ReaderSettingsPreset preset, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid readerId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task ApplyAsync_queries_and_applies_settings_through_the_same_session()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);
        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }

        SettingsApplyResult result = await h.Settings.ApplyAsync(h.Profile.Id, draft);

        Assert.True(result.Succeeded, result.Error);
        Assert.True(h.RegisterSession.SettingsQueryCount >= 3);
        Assert.Equal(1, h.RegisterSession.SettingsApplyCount);
        Assert.NotNull(h.RegisterSession.LastAppliedSettings);
    }

    [Fact]
    public async Task ApplyAsync_does_not_cache_preset_when_device_reread_fails()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        var presets = new InMemorySettingsPresetStore();
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);
        SettingsEditorModel model = await settings.QueryAsync(h.Profile.Id);
        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }
        await presets.DeleteAsync(h.Profile.Id);

        // Query #1 is the editor load, #2 is SettingsService's preflight query,
        // #3 is ReaderManager's same-session compile baseline, and #4 is the
        // post-Apply readback that decides whether the operation is verified.
        h.RegisterSession.SettingsQueryExceptionFactory = count => count == 4
            ? new IOException("device readback failed")
            : null;

        SettingsApplyResult result = await settings.ApplyAsync(h.Profile.Id, draft);

        Assert.False(result.Succeeded);
        Assert.Contains("device readback failed", result.Error);
        Assert.Equal(1, h.RegisterSession.SettingsApplyCount);
        Assert.Null(await presets.GetAsync(h.Profile.Id));
    }

    [Fact]
    public async Task ApplyAsync_query_cancellation_propagates_instead_of_returning_failure()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);
        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }

        using var cancellation = new CancellationTokenSource();
        h.RegisterSession.BeforeQuerySettings = cancellation.Cancel;
        h.RegisterSession.SettingsQueryThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Settings.ApplyAsync(h.Profile.Id, draft, cancellation.Token));
    }

    [Fact]
    public async Task ApplyAsync_apply_cancellation_propagates_instead_of_returning_failure()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        SettingsEditorModel model = await h.Settings.QueryAsync(h.Profile.Id);
        var draft = new SettingsDraft
        {
            ReaderId = h.Profile.Id,
            CapabilityRevision = model.Snapshot.CapabilityRevision,
        };
        foreach ((string key, object? value) in model.Snapshot.Values)
        {
            draft.Values[key] = value;
        }

        using var cancellation = new CancellationTokenSource();
        h.RegisterSession.BeforeApplySettings = cancellation.Cancel;
        h.RegisterSession.SettingsApplyThrows = new OperationCanceledException();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => h.Settings.ApplyAsync(h.Profile.Id, draft, cancellation.Token));
    }

    [Fact]
    public async Task QueryAsync_uses_semantic_preset_as_readonly_fallback_when_reader_is_offline()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        h.RegisterSession.SettingsQueryThrows = new IOException("offline");
        var presets = new InMemorySettingsPresetStore();
        await presets.SaveAsync(new ReaderSettingsPreset
        {
            ReaderId = h.Profile.Id,
            SchemaVersion = ReaderSettingsPreset.CurrentSchemaVersion,
            SettingsJson = "{\"values\":{\"session\":2,\"tx-power-index\":24}}",
        });
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        SettingsEditorModel model = await settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.Equal(2, model.Snapshot.Values[SettingsKeys.Session]);
        Assert.Equal(24, Convert.ToInt32(model.Snapshot.Values[SettingsKeys.TxPowerIndex]));
    }

    [Fact]
    public async Task QueryAsync_uses_semantic_preset_when_reader_has_no_capability()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        h.RegisterSession.ConnectThrows = new IOException("offline");
        var presets = new InMemorySettingsPresetStore();
        await presets.SaveAsync(new ReaderSettingsPreset
        {
            ReaderId = h.Profile.Id,
            SchemaVersion = ReaderSettingsPreset.CurrentSchemaVersion,
            SettingsJson = "{\"values\":{\"session\":3,\"tx-power-index\":21}}",
        });
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        SettingsEditorModel model = await settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.Equal(3, model.Snapshot.Values[SettingsKeys.Session]);
        Assert.Equal(21, Convert.ToInt32(model.Snapshot.Values[SettingsKeys.TxPowerIndex]));
    }

    [Fact]
    public async Task QueryAsync_does_not_treat_flat_legacy_json_as_a_new_platform_preset()
    {
        var h = new Harness();
        await h.Manager.AddAsync(h.Profile, enableAfterAdding: false);
        await h.Manager.ActivateAsync(h.Profile.Id);
        h.RegisterSession.SettingsQueryThrows = new IOException("offline");
        var presets = new InMemorySettingsPresetStore();
        await presets.SaveAsync(new ReaderSettingsPreset
        {
            ReaderId = h.Profile.Id,
            SchemaVersion = ReaderSettingsPreset.CurrentSchemaVersion,
            SettingsJson = "{\"session\":2,\"tx-power-index\":24}",
        });
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        await Assert.ThrowsAsync<IOException>(() => settings.QueryAsync(h.Profile.Id));
    }
}
