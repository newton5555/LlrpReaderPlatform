using LlrpReaderPlatform.Contracts.Lifecycle;
using LlrpReaderPlatform.Contracts.Persistence;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Contracts.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.Json;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 能力驱动的设置服务实现。Query 生成编辑器数据，Validate 校验，Apply 复核能力版本、
/// 校验并通过 ReaderManager 的短连接租约下发真实 SDK 设置。
/// </summary>
public sealed class SettingsService : IReaderSettingsService
{
    private readonly IReaderManager readerManager;
    private readonly ISettingsCompiler compiler;
    private readonly IReaderSettingsRuntime? runtime;
    private readonly IReaderSettingsPresetStore? presetStore;
    private readonly ILogger<SettingsService> logger;
    private readonly ConcurrentDictionary<Guid, EffectiveSettingsLayout> lastLiveLayouts = new();

    public SettingsService(
        IReaderManager readerManager,
        ISettingsCompiler compiler,
        IReaderSettingsRuntime? runtime = null,
        IReaderSettingsPresetStore? presetStore = null,
        ILogger<SettingsService>? logger = null)
    {
        this.readerManager = readerManager;
        this.compiler = compiler;
        this.runtime = runtime ?? readerManager as IReaderSettingsRuntime;
        this.presetStore = presetStore;
        this.logger = logger ?? NullLogger<SettingsService>.Instance;
    }

    public async Task<SettingsEditorModel> QueryAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(readerId);
        logger.LogDebug(
            "Querying settings for {Id}: state={State}, stale={Stale}, revision={Revision}, extensions={Extensions}.",
            readerId,
            snapshot.State,
            snapshot.IsStale,
            snapshot.CapabilityRevision,
            string.Join(",", snapshot.ActiveExtensionIds));
        if (IsNoCapability(snapshot))
        {
            ReaderActivationResult activation = await readerManager.ActivateAsync(readerId, ct).ConfigureAwait(false);
            snapshot = readerManager.GetSnapshot(readerId);
            if (activation.Succeeded && !IsNoCapability(snapshot))
            {
                // 激活成功后继续走真实 Query，下面的缓存分支只处理设备仍不可达的情况。
            }
            else
            {
                SettingsEditorModel? cached = await TryBuildCachedModelAsync(
                    snapshot,
                    new InvalidOperationException(activation.Error ?? "Reader capability is not available; using the cached settings preset."),
                    ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }

                EffectiveSettingsLayout readOnly = BuildNoCapabilityLayout(readerId);
                return new SettingsEditorModel(
                    readOnly,
                    new SettingsSnapshot { ReaderId = readerId, CapabilityRevision = 0, Values = new Dictionary<string, object?>() });
            }
        }

        if (runtime is not null && compiler is ISdkSettingsCompiler sdkCompiler)
        {
            try
            {
                ReaderSettingsRuntimeSnapshot current = await runtime.QueryAsync(readerId, ct).ConfigureAwait(false);
                var model = ReconcileAfterRuntimeOperation(
                    readerId,
                    new SettingsEditorModel(
                    sdkCompiler.BuildLayout(snapshot, current),
                    sdkCompiler.BuildSnapshot(snapshot, current)));
                ReaderRuntimeSnapshot latest = readerManager.GetSnapshot(readerId);
                logger.LogDebug(
                    "Reader settings query returned for {Id}: state={State}, stale={Stale}, revision={Revision}, editable={Editable}, values={ValueCount}.",
                    readerId,
                    latest.State,
                    latest.IsStale,
                    latest.CapabilityRevision,
                    model.Layout.HasEditableSettings,
                    model.Snapshot.Values.Count);
                lastLiveLayouts[readerId] = model.Layout;
                await SaveSemanticPresetAsync(model, ct).ConfigureAwait(false);
                return model;
            }
            catch (ReaderBusyException)
            {
                throw;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Reader settings query failed for {Id}; attempting semantic preset fallback.", readerId);
                SettingsEditorModel? cached = await TryBuildCachedModelAsync(snapshot, ex, ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    return cached;
                }

                throw;
            }
        }

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot);
        SettingsSnapshot snapshotModel = compiler.BuildSnapshot(snapshot);
        return new SettingsEditorModel(layout, snapshotModel);
    }

    public async Task<SettingsEditorModel> GetDefaultsAsync(Guid readerId, CancellationToken ct = default)
    {
        ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(readerId);
        if (IsNoCapability(snapshot))
        {
            ReaderActivationResult activation = await readerManager.ActivateAsync(readerId, ct).ConfigureAwait(false);
            snapshot = readerManager.GetSnapshot(readerId);
            if (!activation.Succeeded || IsNoCapability(snapshot))
            {
                EffectiveSettingsLayout readOnly = BuildNoCapabilityLayout(readerId);
                return new SettingsEditorModel(
                    readOnly,
                    new SettingsSnapshot { ReaderId = readerId, CapabilityRevision = 0, Values = new Dictionary<string, object?>() });
            }
        }

        if (runtime is not null && compiler is ISdkSettingsCompiler sdkCompiler)
        {
            ReaderSettingsRuntimeSnapshot defaults = await runtime.GetDefaultsAsync(readerId, ct).ConfigureAwait(false);
            var model = ReconcileAfterRuntimeOperation(
                readerId,
                new SettingsEditorModel(
                sdkCompiler.BuildLayout(snapshot, defaults),
                sdkCompiler.BuildSnapshot(snapshot, defaults)));
            lastLiveLayouts[readerId] = model.Layout;
            return model;
        }

        EffectiveSettingsLayout layout = compiler.BuildLayout(snapshot);
        return new SettingsEditorModel(layout, compiler.BuildSnapshot(snapshot));
    }

    public SettingsValidationResult Validate(SettingsDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(draft.ReaderId);
        if (IsNoCapability(snapshot) || draft.CapabilityRevision != snapshot.CapabilityRevision)
        {
            return new SettingsValidationResult(
                false,
                "能力已过期或尚未获取，请刷新并重新连接 Reader 后再保存。");
        }

        EffectiveSettingsLayout layout = lastLiveLayouts.TryGetValue(draft.ReaderId, out EffectiveSettingsLayout? cachedLayout)
            && cachedLayout.CapabilityRevision == snapshot.CapabilityRevision
            && cachedLayout.HasEditableSettings
            ? cachedLayout
            : compiler.BuildLayout(snapshot);
        var issues = new List<SettingsEntryIssue>();
        AddUnknownEntryIssues(draft, layout, issues);
        foreach (SettingsEntry entry in layout.Entries)
        {
            if (entry.IsReadOnly)
            {
                continue;
            }

            if (!draft.Values.TryGetValue(entry.Key, out object? value) || value is null)
            {
                // Report fields are intentionally not exposed by the WPF settings page.
                // An omitted report value means "keep the current Reader value"; the SDK
                // compiler already applies that baseline when it builds the next settings.
                if (IsInventoryReportSetting(entry.Key))
                {
                    continue;
                }

                issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 未设置。"));
                continue;
            }

            ValidateValue(entry, value, issues);
        }

        if (issues.Count > 0)
        {
            return new SettingsValidationResult(false, "设置校验失败。", issues);
        }

        return new SettingsValidationResult(true);
    }

    public async Task<SettingsApplyResult> ApplyAsync(
        Guid readerId,
        SettingsDraft draft,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (draft.ReaderId != readerId)
        {
            return new SettingsApplyResult(false, "Draft 与目标 Reader 不一致。")
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }

        ReaderRuntimeSnapshot snapshot = readerManager.GetSnapshot(readerId);
        if (IsNoCapability(snapshot) || draft.CapabilityRevision != snapshot.CapabilityRevision)
        {
            return new SettingsApplyResult(false, "能力已过期或尚未获取，请刷新后重试。")
            { ErrorCode = PlatformErrorCode.StaleCapability };
        }

        SettingsValidationResult validation = Validate(draft);
        if (!validation.IsValid)
        {
            return new SettingsApplyResult(false, FormatValidationError(validation))
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }

        if (runtime is null || compiler is not ISdkSettingsCompiler sdkCompiler)
        {
            return new SettingsApplyResult(false, "设置运行时未注册，无法向 Reader 下发设置。")
            { ErrorCode = PlatformErrorCode.DeviceFailed };
        }

        ReaderSettingsRuntimeSnapshot current;
        EffectiveSettingsLayout layout;
        try
        {
            current = await runtime.QueryAsync(readerId, ct).ConfigureAwait(false);
            ReaderRuntimeSnapshot latestSnapshot = readerManager.GetSnapshot(readerId);
            if (IsNoCapability(latestSnapshot)
                || draft.CapabilityRevision != latestSnapshot.CapabilityRevision)
            {
                return new SettingsApplyResult(false, "能力已过期或在保存期间发生变化，请刷新后重试。")
                { ErrorCode = PlatformErrorCode.StaleCapability };
            }

            snapshot = latestSnapshot;
            layout = sdkCompiler.BuildLayout(snapshot, current);
            SettingsValidationResult currentValidation = ValidateDraft(draft, layout);
            if (!currentValidation.IsValid)
            {
                return new SettingsApplyResult(false, FormatValidationError(currentValidation))
                { ErrorCode = PlatformErrorCode.InvalidSettings };
            }
        }
        catch (ReaderBusyException ex)
        {
            return new SettingsApplyResult(false, ex.Message)
            { ErrorCode = PlatformErrorCode.ReaderBusy };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query settings before applying for {Id}.", readerId);
            return new SettingsApplyResult(false, ex.Message)
            { ErrorCode = PlatformErrorCode.DeviceFailed };
        }

        CompiledSettings compiled;
        try
        {
            compiled = compiler.Compile(draft, layout);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compile settings for {Id}.", readerId);
            return new SettingsApplyResult(false, $"设置编译失败: {ex.Message}")
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }
        logger.LogInformation(
            "Compiled settings for reader {Id}: antenna={Antenna}, session={Session}, txPowerIndex={TxPowerIndex}, rxSensitivityIndex={RxSensitivityIndex}.",
            readerId, compiled.AntennaId, compiled.Session, compiled.TxPowerIndex, compiled.RxSensitivityIndex);

        try
        {
            await runtime.ApplyAsync(
                readerId,
                latest =>
                {
                    try
                    {
                        ReaderRuntimeSnapshot applySnapshot = readerManager.GetSnapshot(readerId);
                        if (IsNoCapability(applySnapshot)
                            || draft.CapabilityRevision != applySnapshot.CapabilityRevision)
                        {
                            throw new SettingsCapabilityChangedException(
                                "Reader 能力在设置下发期间发生变化，请刷新后重试。");
                        }

                        return sdkCompiler.CompileSdk(draft, layout, latest, applySnapshot);
                    }
                    catch (Exception ex) when (
                        ex is not SettingsCapabilityChangedException
                        && (ex is ArgumentException or FormatException or OverflowException or InvalidOperationException))
                    {
                        throw new SettingsCompilationException(ex.Message, ex);
                    }
                },
                ct).ConfigureAwait(false);
            if (presetStore is not null)
            {
                try
                {
                    await presetStore.SaveAsync(new ReaderSettingsPreset
                    {
                        ReaderId = readerId,
                        SchemaVersion = ReaderSettingsPreset.CurrentSchemaVersion,
                        SettingsJson = BuildSemanticPresetJson(draft.Values, layout),
                        UpdatedAtUtc = DateTimeOffset.UtcNow,
                    }, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Reader 已经完成 Apply；本地缓存取消不能把设备成功误报成失败。
                    logger.LogWarning(
                        "Settings applied for {Id}, but saving the local preset was cancelled.",
                        readerId);
                }
                catch (Exception ex)
                {
                    // SQLite preset 只服务于离线回退，不是设备 Apply 的事务参与者。
                    logger.LogWarning(ex, "Settings applied for {Id}, but saving the local preset failed.", readerId);
                }
            }
            return new SettingsApplyResult(true);
        }
        catch (ReaderBusyException ex)
        {
            return new SettingsApplyResult(false, ex.Message)
            { ErrorCode = PlatformErrorCode.ReaderBusy };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (SettingsCapabilityChangedException ex)
        {
            return new SettingsApplyResult(false, ex.Message)
            { ErrorCode = PlatformErrorCode.StaleCapability };
        }
        catch (SettingsCompilationException ex)
        {
            return new SettingsApplyResult(false, $"设置编译失败: {ex.Message}")
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }
        catch (FormatException ex)
        {
            return new SettingsApplyResult(false, $"设置编译失败: {ex.Message}")
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }
        catch (OverflowException ex)
        {
            return new SettingsApplyResult(false, $"设置编译失败: {ex.Message}")
            { ErrorCode = PlatformErrorCode.InvalidSettings };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply settings for {Id}.", readerId);
            return new SettingsApplyResult(false, ex.Message)
            { ErrorCode = PlatformErrorCode.DeviceFailed };
        }
    }

    private SettingsValidationResult ValidateDraft(SettingsDraft draft, EffectiveSettingsLayout layout)
    {
        var issues = new List<SettingsEntryIssue>();
        AddUnknownEntryIssues(draft, layout, issues);
        foreach (SettingsEntry entry in layout.Entries)
        {
            if (entry.IsReadOnly)
            {
                continue;
            }

            if (!draft.Values.TryGetValue(entry.Key, out object? value) || value is null)
            {
                // Report fields are intentionally not exposed by the WPF settings page.
                // ApplyAsync validates against the freshly queried live layout, so it must
                // use the same omission rule as the preflight validation above.
                if (IsInventoryReportSetting(entry.Key))
                {
                    continue;
                }

                issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 未设置。"));
                continue;
            }

            ValidateValue(entry, value, issues);
        }

        return issues.Count == 0
            ? new SettingsValidationResult(true)
            : new SettingsValidationResult(false, "设置校验失败。", issues);
    }

    private static bool IsInventoryReportSetting(string key) =>
        key == SettingsKeys.ReportEvery
        || key.StartsWith("report-", StringComparison.Ordinal);

    private static void AddUnknownEntryIssues(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        List<SettingsEntryIssue> issues)
    {
        HashSet<string> knownKeys = layout.Entries
            .Select(static entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string key in draft.Values.Keys.Where(key => !knownKeys.Contains(key)))
        {
            issues.Add(new SettingsEntryIssue(key, "该设置项不在当前 Reader 能力布局中。"));
        }
    }

    private static void ValidateValue(SettingsEntry entry, object value, List<SettingsEntryIssue> issues)
    {
        // 类型检查和数值规范化。Validate 是公开契约边界，不能因为外部消费者
        // 传入了可转换但格式错误的字符串而把 FormatException 直接抛出。
        if (!TryNormalizeValue(entry.ValueType, value, out object? normalized))
        {
            issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 值类型不正确。"));
            return;
        }

        if (entry.Key == SettingsKeys.AntennaIds
            && string.IsNullOrWhiteSpace(Convert.ToString(normalized, CultureInfo.InvariantCulture)))
        {
            issues.Add(new SettingsEntryIssue(
                entry.Key,
                "天线选择不能为空，请使用 ALL 选择全部天线。"));
            return;
        }

        // Choice 的值是单个原始值；Collection 的值是以逗号分隔的语义字符串，
        // 不能把整串字符串直接与单个 option（通常是 int/ushort）比较。
        if (entry.EditorKind == EditorKind.Collection)
        {
            ValidateCollectionValue(entry, normalized!, issues);
        }
        else if (entry.Options.Count > 0 && !entry.Options.Any(o => Equals(o.Value, normalized)))
        {
            issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 不在可选范围内。"));
        }

        // 数值范围检查。
        if (entry.Range is not null && normalized is IConvertible conv)
        {
            decimal d = Convert.ToDecimal(conv, CultureInfo.InvariantCulture);
            if (d < entry.Range.Min || d > entry.Range.Max)
            {
                issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 超出允许范围。"));
            }
        }
    }

    private static void ValidateCollectionValue(SettingsEntry entry, object value, List<SettingsEntryIssue> issues)
    {
        if (entry.Options.Count == 0)
        {
            return;
        }

        string[] values = value.ToString()?
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? [];
        HashSet<string> options = entry.Options
            .Select(static option => Convert.ToString(option.Value, System.Globalization.CultureInfo.InvariantCulture))
            .Where(static option => option is not null)
            .Select(static option => option!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (values.Any(valueText => !options.Contains(valueText)))
        {
            issues.Add(new SettingsEntryIssue(entry.Key, $"{entry.Title} 包含不支持的选项。"));
        }
    }

    private static bool TryNormalizeValue(Type type, object value, out object? normalized)
    {
        if (type == typeof(object) || type.IsInstanceOfType(value))
        {
            normalized = value;
            return true;
        }

        try
        {
            normalized = type switch
            {
                _ when type == typeof(ushort) => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                _ when type == typeof(int) => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                _ when type == typeof(decimal) => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
                _ => null,
            };
            return normalized is not null;
        }
        catch (FormatException)
        {
            normalized = null;
            return false;
        }
        catch (InvalidCastException)
        {
            normalized = null;
            return false;
        }
        catch (OverflowException)
        {
            normalized = null;
            return false;
        }
    }

    private static string FormatValidationError(SettingsValidationResult validation)
    {
        if (validation.Issues.Count == 0)
        {
            return validation.Message ?? "设置校验失败。";
        }

        return string.Join(" ", validation.Issues.Select(static issue => $"{issue.Key}: {issue.Message}"));
    }

    private static bool IsNoCapability(ReaderRuntimeSnapshot snapshot) =>
        snapshot.IsStale || snapshot.CapabilityRevision == 0;

    private sealed class SettingsCapabilityChangedException(string message)
        : InvalidOperationException(message);

    private SettingsEditorModel ReconcileAfterRuntimeOperation(
        Guid readerId,
        SettingsEditorModel model)
    {
        ReaderRuntimeSnapshot latest = readerManager.GetSnapshot(readerId);
        if (!latest.IsStale && latest.State is not ReaderState.Faulted)
        {
            return model;
        }

        logger.LogWarning(
            "Reader settings query completed with a non-live runtime snapshot for {Id}: state={State}, stale={Stale}, error={Error}.",
            readerId,
            latest.State,
            latest.IsStale,
            latest.Error);

        string reason = string.IsNullOrWhiteSpace(latest.Error)
            ? "Reader 连接未可靠释放；当前设置只读，请重新连接后刷新。"
            : $"Reader 连接未可靠释放：{latest.Error} 请重新连接后刷新。";
        EffectiveSettingsLayout readOnlyLayout = new()
        {
            ReaderId = model.Layout.ReaderId,
            CapabilityRevision = model.Layout.CapabilityRevision,
            FeatureCatalog = model.Layout.FeatureCatalog,
            Entries = model.Layout.Entries
                .Select(entry => entry with
                {
                    ReadOnlyReason = entry.ReadOnlyReason ?? reason,
                })
                .ToArray(),
        };
        return model with { Layout = readOnlyLayout };
    }

    private async Task<SettingsEditorModel?> TryBuildCachedModelAsync(
        ReaderRuntimeSnapshot snapshot,
        Exception queryException,
        CancellationToken ct)
    {
        if (presetStore is null)
        {
            return null;
        }

        try
        {
            ReaderSettingsPreset? preset = await presetStore.GetAsync(snapshot.ReaderId, ct).ConfigureAwait(false);
            if (preset is null || preset.SchemaVersion != ReaderSettingsPreset.CurrentSchemaVersion)
            {
                // 能力表索引语义变更后，旧版本缓存不能直接当作新平台语义 Draft 使用。
                // 新库允许清空重建，因此这里宁可回到实时 Query/能力未就绪，也不误写旧值。
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(preset.SettingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!document.RootElement.TryGetProperty("values", out JsonElement valuesRoot)
                || valuesRoot.ValueKind != JsonValueKind.Object)
            {
                // 新平台语义 Preset 必须使用结构化 values 根节点；旧 SDK/旧原型的
                // 扁平 JSON 不属于新库兼容范围，数据库损坏或格式过旧时回到占位页。
                return null;
            }

            IReadOnlyList<SettingsEntry> cachedEntries = ReadCachedLayout(document.RootElement);

            EffectiveSettingsLayout baseLayout = compiler.BuildLayout(snapshot);
            IReadOnlyList<SettingsEntry> sourceEntries = cachedEntries.Count > 0
                ? cachedEntries
                : baseLayout.Entries;
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            var entries = new List<SettingsEntry>(sourceEntries.Count);
            foreach (SettingsEntry entry in sourceEntries)
            {
                if (!valuesRoot.TryGetProperty(entry.Key, out JsonElement jsonValue)
                    || !TryConvertCachedValue(jsonValue, entry.ValueType, out object? value))
                {
                    entries.Add(entry);
                    continue;
                }

                values[entry.Key] = value;
                entries.Add(entry with { CurrentValue = value });
            }

            logger.LogWarning(queryException,
                "Reader settings query failed for {Id}; showing the last semantic SQLite preset as read-only fallback.",
                snapshot.ReaderId);
            var layout = new EffectiveSettingsLayout
            {
                ReaderId = snapshot.ReaderId,
                CapabilityRevision = snapshot.CapabilityRevision,
                FeatureCatalog = snapshot.FeatureCatalog,
                Entries = entries.Select(static entry => entry with
                {
                    ReadOnlyReason = entry.ReadOnlyReason ?? "设备当前不可达；以下为本地缓存，只读显示。",
                }).ToArray(),
            };
            return new SettingsEditorModel(layout, new SettingsSnapshot
            {
                ReaderId = snapshot.ReaderId,
                CapabilityRevision = snapshot.CapabilityRevision,
                Values = values,
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No usable cached settings preset exists for {Id}.", snapshot.ReaderId);
            return null;
        }
    }

    private static bool TryConvertCachedValue(JsonElement element, Type targetType, out object? value)
    {
        try
        {
            if (targetType == typeof(bool) && element.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                value = element.GetBoolean();
                return true;
            }

            if (targetType == typeof(string) && element.ValueKind == JsonValueKind.String)
            {
                value = element.GetString();
                return true;
            }

            if (targetType == typeof(int) && element.TryGetInt32(out int intValue))
            {
                value = intValue;
                return true;
            }

            if (targetType == typeof(ushort) && element.TryGetInt32(out int ushortValue)
                && ushortValue is >= ushort.MinValue and <= ushort.MaxValue)
            {
                value = (ushort)ushortValue;
                return true;
            }

            if (targetType == typeof(decimal) && element.TryGetDecimal(out decimal decimalValue))
            {
                value = decimalValue;
                return true;
            }
        }
        catch (FormatException)
        {
            // Fall through to false; a bad cache must never block live settings.
        }

        value = null;
        return false;
    }

    private async Task SaveSemanticPresetAsync(SettingsEditorModel model, CancellationToken ct)
    {
        if (presetStore is null || !model.Layout.HasEditableSettings)
        {
            return;
        }

        try
        {
            await presetStore.SaveAsync(new ReaderSettingsPreset
            {
                ReaderId = model.Layout.ReaderId,
                SchemaVersion = ReaderSettingsPreset.CurrentSchemaVersion,
                SettingsJson = BuildSemanticPresetJson(model.Snapshot.Values, model.Layout),
                UpdatedAtUtc = DateTimeOffset.UtcNow,
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache queried settings for {Id}.", model.Layout.ReaderId);
        }
    }

    private static string BuildSemanticPresetJson(
        IReadOnlyDictionary<string, object?> values,
        EffectiveSettingsLayout layout)
    {
        var document = new SemanticPresetDocument
        {
            Values = values.ToDictionary(
                static pair => pair.Key,
                static pair => SerializeCachedValue(pair.Value),
                StringComparer.Ordinal),
            Layout = layout.Entries.Select(ToCachedEntry).ToArray(),
        };
        return JsonSerializer.Serialize(document);
    }

    private static CachedSettingsEntry ToCachedEntry(SettingsEntry entry) => new()
    {
        Key = entry.Key,
        Title = entry.Title,
        EditorKind = entry.EditorKind,
        ValueType = GetTypeName(entry.ValueType),
        CurrentValue = SerializeNullableCachedValue(entry.CurrentValue),
        DefaultValue = SerializeNullableCachedValue(entry.DefaultValue),
        Options = entry.Options.Select(option => new CachedSettingsOption
        {
            ValueType = GetTypeName(option.Value?.GetType() ?? typeof(string)),
            Value = SerializeNullableCachedValue(option.Value),
            Display = option.Display,
        }).ToArray(),
        Range = entry.Range,
        VisibleWhen = entry.VisibleWhen,
        ReadOnlyReason = entry.ReadOnlyReason,
        Source = entry.Source,
    };

    private static IReadOnlyList<SettingsEntry> ReadCachedLayout(JsonElement document)
    {
        if (!document.TryGetProperty("layout", out JsonElement layout)
            || layout.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<SettingsEntry>();
        foreach (JsonElement element in layout.EnumerateArray())
        {
            try
            {
                CachedSettingsEntry? cached = element.Deserialize<CachedSettingsEntry>();
                if (cached is null || string.IsNullOrWhiteSpace(cached.Key))
                {
                    continue;
                }

                Type? valueType = ResolveCachedType(cached.ValueType);
                if (valueType is null || !Enum.IsDefined(cached.EditorKind))
                {
                    continue;
                }

                var options = new List<SettingsOption>(cached.Options.Count);
                foreach (CachedSettingsOption option in cached.Options)
                {
                    Type optionType = ResolveCachedType(option.ValueType) ?? typeof(string);
                    object? value = option.Value is JsonElement json
                        && TryConvertCachedValue(json, optionType, out object? converted)
                        ? converted
                        : null;
                    options.Add(new SettingsOption(value, option.Display));
                }

                object? current = cached.CurrentValue is JsonElement currentJson
                    && TryConvertCachedValue(currentJson, valueType, out object? currentValue)
                    ? currentValue
                    : null;
                object? defaultValue = cached.DefaultValue is JsonElement defaultJson
                    && TryConvertCachedValue(defaultJson, valueType, out object? convertedDefault)
                    ? convertedDefault
                    : null;

                entries.Add(new SettingsEntry
                {
                    Key = cached.Key,
                    Title = cached.Title ?? cached.Key,
                    EditorKind = cached.EditorKind,
                    ValueType = valueType,
                    CurrentValue = current,
                    DefaultValue = defaultValue,
                    Options = options,
                    Range = cached.Range,
                    VisibleWhen = cached.VisibleWhen,
                    ReadOnlyReason = cached.ReadOnlyReason,
                    Source = cached.Source,
                });
            }
            catch (JsonException)
            {
                // 一个损坏的缓存行不能让整个离线设置页失效。
            }
        }

        return entries;
    }

    private static JsonElement SerializeCachedValue(object? value) =>
        JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object));

    private static JsonElement? SerializeNullableCachedValue(object? value) =>
        value is null ? null : SerializeCachedValue(value);

    private static string GetTypeName(Type type) => type.FullName ?? type.Name;

    private static Type? ResolveCachedType(string? typeName) => typeName switch
    {
        "System.Boolean" => typeof(bool),
        "System.Byte" => typeof(byte),
        "System.Int32" => typeof(int),
        "System.Int64" => typeof(long),
        "System.UInt16" => typeof(ushort),
        "System.UInt32" => typeof(uint),
        "System.Decimal" => typeof(decimal),
        "System.Double" => typeof(double),
        "System.String" => typeof(string),
        _ => null,
    };

    private sealed class SemanticPresetDocument
    {
        [JsonPropertyName("values")]
        public Dictionary<string, JsonElement> Values { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("layout")]
        public IReadOnlyList<CachedSettingsEntry> Layout { get; init; } = [];
    }

    private sealed class CachedSettingsEntry
    {
        public string Key { get; init; } = string.Empty;
        public string? Title { get; init; }
        public EditorKind EditorKind { get; init; }
        public string ValueType { get; init; } = "System.String";
        public JsonElement? CurrentValue { get; init; }
        public JsonElement? DefaultValue { get; init; }
        public IReadOnlyList<CachedSettingsOption> Options { get; init; } = [];
        public SettingsRange? Range { get; init; }
        public string? VisibleWhen { get; init; }
        public string? ReadOnlyReason { get; init; }
        public SettingsSource Source { get; init; }
    }

    private sealed class CachedSettingsOption
    {
        public string ValueType { get; init; } = "System.String";
        public JsonElement? Value { get; init; }
        public string? Display { get; init; }
    }

    private static EffectiveSettingsLayout BuildNoCapabilityLayout(Guid readerId) => new()
    {
        ReaderId = readerId,
        CapabilityRevision = 0,
        Entries =
        [
            new SettingsEntry
            {
                Key = "capability-pending",
                Title = "能力未就绪",
                EditorKind = EditorKind.Text,
                ValueType = typeof(string),
                ReadOnlyReason = "需要连接 Reader 以获取能力后才能配置设置。",
            },
        ],
    };

    private sealed class SettingsCompilationException(string message, Exception innerException)
        : Exception(message, innerException);
}
