using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Errors;
using LlrpReaderPlatform.Services.Lifecycle;
using LlrpReaderPlatform.Services.Persistence;
using LlrpReaderPlatform.Services.Settings;
using LlrpReaderPlatform.TestKit;
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

        draft.Values["tx-power-dbm"] = 99m; // 超出 0..30
        SettingsValidationResult result = h.Settings.Validate(draft);
        Assert.False(result.IsValid);

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

        // Filter mask 的格式错误应转换为 ApplyResult，而不是把编译异常抛到 UI。
        draft.Values[SettingsKeys.FilterEnabled(1)] = true;
        draft.Values[SettingsKeys.FilterMask(1)] = "GG";
        SettingsApplyResult invalid = await h.Settings.ApplyAsync(h.Profile.Id, draft);
        Assert.False(invalid.Succeeded);
        Assert.Contains("设置编译失败", invalid.Error);
        Assert.Equal(PlatformErrorCode.InvalidSettings, invalid.ErrorCode);
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
            SchemaVersion = 1,
            SettingsJson = "{\"session\":2,\"tx-power-dbm\":24.5}",
        });
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        SettingsEditorModel model = await settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.Equal(2, model.Snapshot.Values[SettingsKeys.Session]);
        Assert.Equal(24.5m, model.Snapshot.Values[SettingsKeys.TxPowerDbm]);
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
            SchemaVersion = 1,
            SettingsJson = "{\"session\":3,\"tx-power-dbm\":21.5}",
        });
        var settings = new SettingsService(h.Manager, h.Compiler, h.Manager, presets);

        SettingsEditorModel model = await settings.QueryAsync(h.Profile.Id);

        Assert.False(model.Layout.HasEditableSettings);
        Assert.Equal(3, model.Snapshot.Values[SettingsKeys.Session]);
        Assert.Equal(21.5m, model.Snapshot.Values[SettingsKeys.TxPowerDbm]);
    }
}
