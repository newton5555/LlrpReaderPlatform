using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Xunit;
using Tagging = LlrpReaderPlatform.Contracts.Tagging;

namespace LlrpReaderPlatform.Infrastructure.Tests;

public sealed class SqliteReaderProfileStoreTests
{
    [Fact]
    public async Task Save_get_update_and_delete_profile_round_trip()
    {
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection($"Data Source=file:llrp-platform-test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            connection.Open();
            var options = new DbContextOptionsBuilder<PlatformDbContext>()
                .UseSqlite(connection)
                .Options;
            var factory = new TestContextFactory(options);
            var store = new SqliteReaderProfileStore(factory);
            var id = Guid.NewGuid();
            var profile = new ReaderProfile
            {
                Id = id,
                Name = "R420",
                Host = "[FE80::10]",
                Port = 5084,
                LlrpVersion = LlrpProtocolVersionOption.Force101,
                IsEnabled = true,
            };

            await store.SaveAsync(profile);
            ReaderProfile? loaded = await store.GetAsync(id);
            ReaderProfile normalized = profile with { Host = "fe80::10" };
            Assert.Equal(normalized, loaded);

            ReaderProfile updated = normalized with { Name = "Updated", IsEnabled = false };
            await store.SaveAsync(updated);
            Assert.Equal(updated, await store.GetAsync(id));
            Assert.Single(await store.GetAllAsync());

            await store.DeleteAsync(id);
            Assert.Null(await store.GetAsync(id));
        }
        finally
        {
            connection?.Dispose();
        }
    }

    [Fact]
    public async Task App_settings_store_round_trips_values()
    {
        using var connection = new SqliteConnection($"Data Source=file:llrp-platform-settings-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        connection.Open();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        var store = new SqliteAppSettingsStore(new TestContextFactory(options));

        await store.SetAsync("tag-logging-enabled", "True");
        Assert.Equal("True", await store.GetAsync("tag-logging-enabled"));
        await store.SetAsync("tag-logging-enabled", "False");
        Assert.Equal("False", await store.GetAsync("tag-logging-enabled"));
    }

    [Fact]
    public async Task Settings_preset_store_round_trips_versioned_inventory_semantics()
    {
        using var connection = new SqliteConnection($"Data Source=file:llrp-platform-preset-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        connection.Open();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        var store = new SqliteReaderSettingsPresetStore(new TestContextFactory(options));
        Guid readerId = Guid.NewGuid();
        DateTimeOffset firstUpdatedAt = DateTimeOffset.Parse("2026-08-10T08:00:00+00:00");
        var preset = new ReaderSettingsPreset
        {
            ReaderId = readerId,
            SchemaVersion = 1,
            SettingsJson = "{\"values\":{\"session\":2,\"report-every\":3}}",
            UpdatedAtUtc = firstUpdatedAt,
        };

        await store.SaveAsync(preset);
        Assert.Equal(preset, await store.GetAsync(readerId));

        var updated = preset with
        {
            SchemaVersion = 2,
            SettingsJson = "{\"values\":{\"session\":3,\"report-every\":5}}",
            UpdatedAtUtc = firstUpdatedAt.AddMinutes(1),
        };
        await store.SaveAsync(updated);
        Assert.Equal(updated, await store.GetAsync(readerId));

        await store.DeleteAsync(readerId);
        Assert.Null(await store.GetAsync(readerId));
    }

    [Fact]
    public async Task Tag_list_and_inventory_run_stores_round_trip()
    {
        using var connection = new SqliteConnection($"Data Source=file:llrp-platform-taglist-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        connection.Open();
        var options = new DbContextOptionsBuilder<PlatformDbContext>().UseSqlite(connection).Options;
        var factory = new TestContextFactory(options);
        var tagStore = new SqliteTagListStore(factory);
        var runStore = new SqliteInventoryRunStore(factory);
        Guid listId = Guid.NewGuid();
        Guid readerId = Guid.NewGuid();

        // Two stores can be touched by startup/page initialization at the same time;
        // both first accesses must share one schema migration gate.
        await Task.WhenAll(
            tagStore.GetAllAsync(),
            runStore.GetForReaderAsync(readerId));

        await tagStore.SaveAsync(new TagListDefinition
        {
            Id = listId,
            Name = "Door tags",
            Entries =
            [
                new TagListEntry
                {
                    Id = Guid.NewGuid(),
                    TagListId = listId,
                    EpcHex = "300833B2DDD9014000000001",
                    DisplayName = "Box 1",
                },
            ],
        });
        TagListDefinition? loaded = await tagStore.GetAsync(listId);
        Assert.Equal("Door tags", loaded?.Name);
        Assert.Single(loaded?.Entries ?? []);

        var run = new InventoryRunRecord
        {
            Id = Guid.NewGuid(),
            ReaderId = readerId,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            EndedAtUtc = DateTimeOffset.UtcNow,
            StopReason = "Manual",
            TotalReadCount = 4,
            UniqueTagCount = 1,
        };
        await runStore.SaveAsync(run);
        InventoryRunRecord saved = Assert.Single(await runStore.GetForReaderAsync(readerId));
        Assert.Equal(run.TotalReadCount, saved.TotalReadCount);
    }

    [Fact]
    public async Task Json_lines_tag_log_writes_one_run_file_when_enabled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"llrp-tag-log-{Guid.NewGuid():N}");
        try
        {
            var settings = new FakeAppSettingsStore
            {
                ["tag-logging-enabled"] = "True",
                ["tag-log-directory"] = root,
            };
            var writer = new JsonLinesInventoryTagLog(settings);
            var run = new InventoryRunRecord
            {
                Id = Guid.NewGuid(),
                ReaderId = Guid.NewGuid(),
                StartedAtUtc = DateTimeOffset.UtcNow,
            };
            string? path = await writer.StartAsync(run);
            Assert.False(string.IsNullOrWhiteSpace(path));
            await writer.AppendAsync(run, new Tagging.TagObservation
            {
                Epc = "3008",
                ReadCount = 1,
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow,
            });
            await writer.CompleteAsync(run);

            string content = await File.ReadAllTextAsync(path!);
            Assert.Contains("3008", content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Json_lines_tag_log_skips_run_when_disabled()
    {
        string root = Path.Combine(Path.GetTempPath(), $"llrp-tag-log-disabled-{Guid.NewGuid():N}");
        try
        {
            var settings = new FakeAppSettingsStore
            {
                ["tag-logging-enabled"] = "False",
                ["tag-log-directory"] = root,
            };
            var writer = new JsonLinesInventoryTagLog(settings);
            var run = new InventoryRunRecord
            {
                Id = Guid.NewGuid(),
                ReaderId = Guid.NewGuid(),
                StartedAtUtc = DateTimeOffset.UtcNow,
            };

            string? path = await writer.StartAsync(run);

            Assert.Null(path);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Json_lines_tag_log_uses_default_directory_when_configured_directory_is_blank()
    {
        string defaultRoot = Path.Combine(Path.GetTempPath(), $"llrp-tag-log-default-{Guid.NewGuid():N}");
        Guid readerId = Guid.NewGuid();
        string readerDirectory = Path.Combine(defaultRoot, readerId.ToString("N"));
        try
        {
            var settings = new FakeAppSettingsStore
            {
                ["tag-logging-enabled"] = "True",
                ["tag-log-directory"] = "  ",
            };
            var writer = new JsonLinesInventoryTagLog(settings, defaultRoot);
            var run = new InventoryRunRecord
            {
                Id = Guid.NewGuid(),
                ReaderId = readerId,
                StartedAtUtc = DateTimeOffset.UtcNow,
            };

            string? path = await writer.StartAsync(run);
            Assert.NotNull(path);
            Assert.StartsWith(Path.GetFullPath(readerDirectory), Path.GetFullPath(path!), StringComparison.OrdinalIgnoreCase);
            await writer.CompleteAsync(run);
        }
        finally
        {
            if (Directory.Exists(readerDirectory))
            {
                Directory.Delete(readerDirectory, recursive: true);
            }

            if (Directory.Exists(defaultRoot))
            {
                Directory.Delete(defaultRoot, recursive: true);
            }
        }
    }

    private sealed class FakeAppSettingsStore : Dictionary<string, string>, IAppSettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(TryGetValue(key, out string? value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            this[key] = value;
            return Task.CompletedTask;
        }
    }

    private sealed class TestContextFactory(DbContextOptions<PlatformDbContext> options)
        : IDbContextFactory<PlatformDbContext>
    {
        public PlatformDbContext CreateDbContext() => new(options);

        public Task<PlatformDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PlatformDbContext(options));
    }
}
