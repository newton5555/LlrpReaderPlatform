using System.Globalization;
using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpReaderPlatform.Services.Extensions;
using LlrpSdk;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 标准 LLRP 设置编译器：根据 ReaderRuntimeSnapshot 的能力（天线列表）生成
/// 能力驱动的设置布局，提供快照与 Draft 编译。厂商扩展项由扩展模块另行贡献。
/// </summary>
public sealed class StandardSettingsCompiler : ISettingsCompiler, ISdkSettingsCompiler
{
    /// <summary>RF Mode 下拉“默认”哨兵值：表示不下发 C1G2RFControl（Mode=0 + Tari=0）。</summary>
    private const int RfModeDefaultSentinel = -1;

    private readonly IReadOnlyList<ISettingsExtensionContributor> extensions;

    public StandardSettingsCompiler(
        IEnumerable<ISettingsExtensionContributor>? extensions = null,
        IEnumerable<IReaderExtensionModule>? modules = null)
    {
        IEnumerable<ISettingsExtensionContributor> independent = extensions ?? [];
        IEnumerable<ISettingsExtensionContributor> moduleContributors = modules?
            .Select(static module => module.SettingsContributor)
            .OfType<ISettingsExtensionContributor>()
            ?? [];
        this.extensions = independent
            .Concat(moduleContributors)
            .GroupBy(static extension => extension.Id, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
    }

    private static readonly SettingsOption[] SessionOptions =
    [
        new(0, "S0"),
        new(1, "S1"),
        new(2, "S2"),
        new(3, "S3"),
    ];

    public EffectiveSettingsLayout BuildLayout(ReaderRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var entries = new List<SettingsEntry>();

        // 天线：由能力决定可选值；无天线信息时只读。
        if (snapshot.Antennas.Count > 0)
        {
            IReadOnlyList<SettingsOption> options = snapshot.Antennas
                .Select(a => new SettingsOption(a.AntennaId, a.Name))
                .ToArray();
            object? current = snapshot.Antennas[0].AntennaId;
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Antenna,
                Title = "Antenna",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(ushort),
                Options = options,
                CurrentValue = current,
                DefaultValue = current,
            });
        }
        else
        {
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Antenna,
                Title = "Antenna",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(ushort),
                ReadOnlyReason = "尚未获取天线能力，请先连接 Reader。",
            });
        }

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.Session,
            Title = "Session",
            EditorKind = EditorKind.Choice,
            ValueType = typeof(int),
            Options = SessionOptions,
            CurrentValue = 0,
            DefaultValue = 0,
        });

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.TxPowerIndex,
            Title = "Tx Power Index",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(ushort),
            Range = new SettingsRange(0, ushort.MaxValue),
            CurrentValue = (ushort)20,
            DefaultValue = (ushort)20,
        });

        return new EffectiveSettingsLayout
        {
            ReaderId = snapshot.ReaderId,
            CapabilityRevision = snapshot.CapabilityRevision,
            Entries = entries,
            FeatureCatalog = snapshot.FeatureCatalog,
        };
    }

    public SettingsSnapshot BuildSnapshot(ReaderRuntimeSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        EffectiveSettingsLayout layout = BuildLayout(snapshot);
        Dictionary<string, object?> values = layout.Entries
            .Where(static e => !e.IsReadOnly)
            .ToDictionary(static e => e.Key, static e => e.CurrentValue, StringComparer.Ordinal);
        return new SettingsSnapshot
        {
            ReaderId = snapshot.ReaderId,
            CapabilityRevision = snapshot.CapabilityRevision,
            Values = values,
        };
    }

    public CompiledSettings Compile(SettingsDraft draft, EffectiveSettingsLayout layout)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(layout);

        var compiled = new CompiledSettings();
        foreach (SettingsEntry entry in layout.Entries)
        {
            if (entry.IsReadOnly || !draft.Values.TryGetValue(entry.Key, out object? value) || value is null)
            {
                continue;
            }

            if (entry.Key == SettingsKeys.AntennaIds)
            {
                compiled.AntennaIds = ParseAntennaIds(FormatInvariant(value));
                continue;
            }

            if (entry.Key.StartsWith("filter-", StringComparison.Ordinal))
            {
                // Filter rows are compiled together in CompileSdk so invalid/disabled rows can
                // be validated against the device's actual InventorySettings baseline.
                continue;
            }

            switch (entry.Key)
            {
                case SettingsKeys.Antenna:
                    compiled.AntennaId = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.Session:
                    compiled.Session = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.TxPowerIndex:
                    compiled.TxPowerIndex = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.RxSensitivityIndex:
                    compiled.RxSensitivityIndex = Convert.ToUInt16(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.TagPopulation:
                    compiled.TagPopulation = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.ReportEvery:
                    compiled.ReportEvery = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.RfMode:
                    compiled.RfMode = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.Tari:
                    compiled.Tari = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                    break;
            }
        }

        return compiled;
    }

    public EffectiveSettingsLayout BuildLayout(
        ReaderRuntimeSnapshot snapshot,
        ReaderSettingsRuntimeSnapshot runtime)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(runtime);

        InventorySettings inventory = ResolveInventoryBaseline(runtime);
        ushort currentAntenna = inventory.AntennaIds.FirstOrDefault();
        if (currentAntenna == 0 && snapshot.Antennas.Count > 0)
        {
            currentAntenna = snapshot.Antennas[0].AntennaId;
        }

        var entries = new List<SettingsEntry>();
        if (snapshot.Antennas.Count > 0)
        {
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Antenna,
                Title = "Antenna",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(ushort),
                Options = snapshot.Antennas.Select(a => new SettingsOption(a.AntennaId, a.Name)).ToArray(),
                CurrentValue = currentAntenna,
                DefaultValue = currentAntenna,
            });
        }
        else
        {
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Antenna,
                Title = "Antenna",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(ushort),
                ReadOnlyReason = "尚未获取天线能力，请先连接 Reader。",
            });
        }

        int session = inventory.Session;
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.Session,
            Title = "Session",
            EditorKind = EditorKind.Choice,
            ValueType = typeof(int),
            Options = SessionOptions,
            CurrentValue = session,
            DefaultValue = session,
        });

        ushort? txPowerIndex = ResolveTxPowerIndex(inventory, currentAntenna);
        SettingsRange range = ResolveTxPowerIndexRange(runtime.Capabilities);
        IReadOnlyList<SettingsOption> txPowerOptions = BuildTxPowerOptions(runtime.Capabilities);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.TxPowerIndex,
            Title = "Tx Power Index",
            EditorKind = txPowerOptions.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
            ValueType = typeof(ushort),
            Options = txPowerOptions,
            Range = range,
            CurrentValue = txPowerIndex ?? ToUshortRangeValue(range.Min),
            DefaultValue = txPowerIndex ?? ToUshortRangeValue(range.Min),
        });

        ushort? rxSensitivityIndex = ResolveRxSensitivityIndex(inventory, currentAntenna);
        SettingsRange rxRange = ResolveRxSensitivityIndexRange(runtime.Capabilities);
        IReadOnlyList<SettingsOption> rxSensitivityOptions = BuildRxSensitivityOptions(runtime.Capabilities);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.RxSensitivityIndex,
            Title = "Rx Sensitivity Index",
            EditorKind = rxSensitivityOptions.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
            ValueType = typeof(ushort),
            Options = rxSensitivityOptions,
            Range = rxRange,
            CurrentValue = rxSensitivityIndex ?? ToUshortRangeValue(rxRange.Min),
            DefaultValue = rxSensitivityIndex ?? ToUshortRangeValue(rxRange.Min),
        });

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.TagPopulation,
            Title = "Tag Population",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = new SettingsRange(0, ushort.MaxValue),
            CurrentValue = (int)inventory.TagPopulationEstimate,
            DefaultValue = (int)inventory.TagPopulationEstimate,
        });

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.ReportEvery,
            Title = "Report Every N Tags",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = new SettingsRange(0, ushort.MaxValue),
            CurrentValue = (int)inventory.ReportEveryNTags,
            DefaultValue = (int)inventory.ReportEveryNTags,
        });

        // 展示值：设备 Mode=0 且 Tari=0（SDK“默认/不指定”）→ 哨兵 -1“默认”；否则用真实 ModeIdentifier。
        int currentRfModeDisplay = (inventory.ModeIndex == 0 && inventory.Tari == 0)
            ? RfModeDefaultSentinel
            : (int)inventory.ModeIndex;
        IReadOnlyList<SettingsOption> rfModes = BuildRfModeOptions(runtime.Capabilities, inventory.ModeIndex);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.RfMode,
            Title = "RF Mode",
            EditorKind = rfModes.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
            ValueType = typeof(int),
            Options = rfModes,
            Range = rfModes.Count > 0 ? null : new SettingsRange(RfModeDefaultSentinel, ushort.MaxValue),
            CurrentValue = currentRfModeDisplay,
            DefaultValue = currentRfModeDisplay,
            RfModeTariProfiles = BuildRfModeTariProfiles(runtime.Capabilities),
        });

        AddTariEntry(entries, runtime.Capabilities, inventory.ModeIndex, inventory.Tari);

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.AntennaIds,
            Title = "Inventory Antennas (comma separated)",
            EditorKind = EditorKind.Text,
            ValueType = typeof(string),
            CurrentValue = FormatAntennaIds(inventory.AntennaIds, snapshot.Antennas),
            DefaultValue = FormatAntennaIds(inventory.AntennaIds, snapshot.Antennas),
            ReadOnlyReason = snapshot.Antennas.Count == 0 ? "尚未获取天线能力。" : null,
        });

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.IndividualAntennaSettings,
            Title = "Individual antenna RF settings",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = inventory.AntennaConfigurations.Any(c => c.AntennaId != 0),
            DefaultValue = false,
        });

        foreach (ReaderAntennaInfo antenna in snapshot.Antennas)
        {
            ushort? antennaTxIndex = ResolveTxPowerIndex(inventory, antenna.AntennaId);
            ushort? antennaRxIndex = ResolveRxSensitivityIndex(inventory, antenna.AntennaId);
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.AntennaTxPowerIndex(antenna.AntennaId),
                Title = $"{antenna.Name ?? $"Antenna {antenna.AntennaId}"} Tx Power Index",
                EditorKind = txPowerOptions.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
                ValueType = typeof(ushort),
                Options = txPowerOptions,
                Range = range,
                CurrentValue = antennaTxIndex ?? txPowerIndex ?? ToUshortRangeValue(range.Min),
                DefaultValue = antennaTxIndex ?? txPowerIndex ?? ToUshortRangeValue(range.Min),
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.AntennaRxSensitivityIndex(antenna.AntennaId),
                Title = $"{antenna.Name ?? $"Antenna {antenna.AntennaId}"} Rx Sensitivity Index",
                EditorKind = rxSensitivityOptions.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
                ValueType = typeof(ushort),
                Options = rxSensitivityOptions,
                Range = rxRange,
                CurrentValue = antennaRxIndex ?? rxSensitivityIndex ?? ToUshortRangeValue(rxRange.Min),
                DefaultValue = antennaRxIndex ?? rxSensitivityIndex ?? ToUshortRangeValue(rxRange.Min),
            });
        }

        AddFilterEntries(entries, inventory, runtime.Capabilities?.CanDoTagInventoryStateAwareSingulation == true);
        AddTriggerEntries(
            entries,
            inventory,
            snapshot.FeatureCatalog.SupportsOrUnknown(ReaderFeatures.StandardGpi),
            snapshot.GpiCount);
        AddReportEntries(entries, inventory.Report);
        foreach (ISettingsExtensionContributor extension in extensions.Where(e => e.IsApplicable(snapshot)))
        {
            extension.ContributeLayout(entries, snapshot, runtime);
        }

        return new EffectiveSettingsLayout
        {
            ReaderId = snapshot.ReaderId,
            CapabilityRevision = snapshot.CapabilityRevision,
            Entries = entries,
            FeatureCatalog = snapshot.FeatureCatalog,
        };
    }

    public SettingsSnapshot BuildSnapshot(
        ReaderRuntimeSnapshot snapshot,
        ReaderSettingsRuntimeSnapshot runtime)
    {
        EffectiveSettingsLayout layout = BuildLayout(snapshot, runtime);
        return new SettingsSnapshot
        {
            ReaderId = snapshot.ReaderId,
            CapabilityRevision = snapshot.CapabilityRevision,
            Values = layout.Entries
                .Where(static e => !e.IsReadOnly)
                .ToDictionary(static e => e.Key, static e => e.CurrentValue, StringComparer.Ordinal),
        };
    }

    public ReaderSettings CompileSdk(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        ReaderSettingsRuntimeSnapshot runtime,
        ReaderRuntimeSnapshot reader)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(runtime);

        CompiledSettings compiled = Compile(draft, layout);
        ReaderSettings baseline = runtime.Settings.Settings;
        // LLRP SDK 会把设备当前的 Inventory 配置放在 managed ROSpec 中返回，
        // 而 ReaderSettings.Inventory 可能为空。Apply 必须沿用 Query 展示给用户的
        // 同一份基线，否则保存一个字段时会把其余 Tab1 配置回退到空默认值。
        InventorySettings inventory = runtime.Settings.ManagedRoSpec?.Inventory
            ?? baseline.Inventory
            ?? CreateInventoryBaseline(baseline.Configuration);

        if (compiled.Session is int session)
        {
            inventory = inventory with { Session = checked((byte)session) };
        }

        if (compiled.TagPopulation is int population)
        {
            inventory = inventory with { TagPopulationEstimate = checked((ushort)population) };
        }

        if (compiled.ReportEvery is int reportEvery)
        {
            inventory = inventory with
            {
                ReportEveryNTags = checked((ushort)reportEvery),
                // 与旧 Reader Studio 的 ApplySettings 行为一致：启用 N 标签报告时，
                // 必须同时使用 UponNTagsOrEndOfAiSpec，否则仅修改 N 值在部分 Reader
                // 的默认触发模式下不会产生按 N 聚合的 TagReport。
                Report = inventory.Report with { Trigger = InventoryReportTrigger.UponNTagsOrEndOfAiSpec },
            };
        }

        if (compiled.RfMode is int mode)
        {
            // “默认”哨兵 -1：走 SDK 默认（Mode=0 + Tari=0，SDK 不生成 C1G2RFControl，设备用默认）。
            inventory = inventory with
            {
                ModeIndex = mode == RfModeDefaultSentinel ? (ushort)0 : checked((ushort)mode),
                Tari = mode == RfModeDefaultSentinel ? (ushort)0 : inventory.Tari,
            };
        }

        if (compiled.Tari is int tari && tari != 0)
        {
            inventory = inventory with { Tari = checked((ushort)tari) };
        }

        bool defaultRfModeSelected = compiled.RfMode == RfModeDefaultSentinel;
        if (!defaultRfModeSelected
            && (compiled.RfMode is not null || inventory.ModeIndex != 0 || inventory.Tari != 0))
        {
            inventory = inventory with
            {
                Tari = ResolveTari(runtime.Capabilities, inventory.ModeIndex, inventory.Tari),
            };
        }

        if (compiled.AntennaIds is { Count: > 0 } antennaIds)
        {
            inventory = inventory with { AntennaIds = antennaIds };
        }
        else if (compiled.AntennaId is ushort selectedAntenna)
        {
            inventory = inventory with { AntennaIds = [selectedAntenna] };
        }

        inventory = ExpandAllAntennas(inventory, reader, runtime.Capabilities);

        bool individual = draft.Values.TryGetValue(SettingsKeys.IndividualAntennaSettings, out object? individualValue)
            && Convert.ToBoolean(individualValue, CultureInfo.InvariantCulture);
        bool gpiSupported = reader.FeatureCatalog.SupportsOrUnknown(ReaderFeatures.StandardGpi);
        inventory = inventory with
        {
            AntennaConfigurations = BuildAntennaConfigurations(draft, inventory, runtime.Capabilities, individual),
            Filters = BuildFilters(draft, inventory.Filters, runtime.Capabilities?.CanDoTagInventoryStateAwareSingulation == true),
            StateAwareSingulation = BuildStateAwareSingulation(draft, runtime.Capabilities?.CanDoTagInventoryStateAwareSingulation == true),
            Report = BuildReport(draft, inventory.Report),
            StartTrigger = gpiSupported
                ? BuildStartTrigger(draft, inventory.StartTrigger)
                : inventory.StartTrigger with { Type = InventoryStartTriggerType.Immediate },
            StopTrigger = gpiSupported
                ? BuildStopTrigger(draft, inventory.StopTrigger)
                : inventory.StopTrigger with { Type = InventoryStopTriggerType.None },
        };

        // Keep the Reader-wide SET_READER_CONFIG antenna projection returned by
        // GET_READER_CONFIG. It is a separate configuration scope from the Inventory
        // ROSpec, but clearing it here loses the reader's antenna configuration whenever
        // a settings page saves an unrelated field. Inventory RF is compiled separately
        // in the ROSpec projection above.
        ReaderSettings compiledSettings = baseline with
        {
            Inventory = inventory,
            Configuration = baseline.Configuration,
        };
        if (inventory.StartTrigger.Type == InventoryStartTriggerType.Gpi
            || inventory.StopTrigger.Type == InventoryStopTriggerType.GpiWithTimeout)
        {
            // LLRP 的 Inventory GPI 触发器与 GPI_EVENT 通知是两个独立配置。
            // 只下发 ROSpec 触发器而不打开事件通知时，Reader 可能不会发送
            // GPI 状态变化，平台也就无法把物理输入统一映射为生命周期事件。
            EventNotificationConfiguration events = compiledSettings.Configuration.Events with
            {
                GpiEventEnabled = true,
            };
            compiledSettings = compiledSettings with
            {
                Configuration = compiledSettings.Configuration with { Events = events },
            };
        }
        else if (!gpiSupported && compiledSettings.Configuration.Events.GpiEventEnabled)
        {
            // 设备明确没有 GPI 时不保留一份无效的 GPI_EVENT 请求，避免把
            // 设备对事件通知的拒绝误报为普通设置 Apply 失败。
            EventNotificationConfiguration events = compiledSettings.Configuration.Events with
            {
                GpiEventEnabled = false,
            };
            compiledSettings = compiledSettings with
            {
                Configuration = compiledSettings.Configuration with { Events = events },
            };
        }

        foreach (ISettingsExtensionContributor extension in extensions.Where(e => e.IsApplicable(reader)))
        {
            compiledSettings = extension.Apply(draft, layout, reader, runtime, compiledSettings);
        }

        return compiledSettings;
    }

    private static readonly SettingsOption[] MemoryBankOptions =
    [
        new(1, "EPC"),
        new(2, "TID"),
        new(3, "User"),
        new(0, "Reserved"),
    ];

    private static readonly SettingsOption[] SelectActionOptions =
    [
        new(1, "Select"),
        new(2, "Unselect"),
        new(0, "Do nothing"),
    ];

    private static readonly SettingsOption[] FilterTargetOptions =
    [
        new(0, "Selected flag"),
        new(1, "Session 0"),
        new(2, "Session 1"),
        new(3, "Session 2"),
        new(4, "Session 3"),
    ];

    private static readonly SettingsOption[] FilterActionOptions =
    [
        new(0, "Assert A / Deassert B"),
        new(1, "Assert A / no operation"),
        new(2, "No operation / Deassert B"),
        new(3, "Negate selected / no operation"),
        new(4, "Deassert B / Assert A"),
        new(5, "Deassert B / no operation"),
        new(6, "No operation / Assert A"),
        new(7, "No operation / Negate selected"),
    ];

    /// <summary>生成 Tari 设置项：未指定 RF 控制(0,0)→只读单项下拉；表内固定 Tari→只读单项下拉；可调→下拉；不在表→兜底。</summary>
    private static void AddTariEntry(
        IList<SettingsEntry> entries,
        ReaderCapabilities? capabilities,
        ushort modeIndex,
        ushort tari)
    {
        // 默认：SDK 不指定 RF 控制（Mode=0 && Tari=0），Tari 只读显示为单项 0。
        if (modeIndex == 0 && tari == 0)
        {
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Tari,
                Title = "Tari",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = [new SettingsOption(0, "0")],
                CurrentValue = 0,
                DefaultValue = 0,
                ReadOnlyReason = "默认模式（由设备自动选择），Tari 固定为 0",
            });
            return;
        }

        C1G2RfModeEntry[] modes = SelectModes(capabilities, modeIndex);
        if (modes.Length == 0)
        {
            // 能力表未收录该 mode：保留设备当前值，只读展示，不修改。
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Tari,
                Title = "Tari",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Range = new SettingsRange(0, ushort.MaxValue),
                CurrentValue = (int)tari,
                DefaultValue = (int)tari,
                ReadOnlyReason = "当前 RF Mode 不在能力表，保留设备值",
            });
            return;
        }

        bool fixedTari = modes.All(mode => mode.MinTariValue == mode.MaxTariValue);
        if (fixedTari)
        {
            ushort value = checked((ushort)modes[0].MinTariValue);
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.Tari,
                Title = "Tari",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = [new SettingsOption(value, $"{value} ({value / 1000d:0.###} uS)")],
                CurrentValue = (int)value,
                DefaultValue = (int)value,
                ReadOnlyReason = "由所选 RF Mode 固定，不可编辑",
            });
            return;
        }

        // 可调 Tari：Min..Max 按 Step 生成下拉选项。
        int min = (int)modes.Min(static m => m.MinTariValue);
        int max = (int)modes.Max(static m => m.MaxTariValue);
        int step = modes[0].StepTariValue > 0 ? (int)modes[0].StepTariValue : 1;
        var tariOptions = new List<SettingsOption>();
        for (int v = min; v <= max; v += step)
        {
            tariOptions.Add(new SettingsOption(v, $"{v} ({v / 1000d:0.###} uS)"));
        }
        if (tariOptions.Count == 0) tariOptions.Add(new SettingsOption(min, $"{min} ({min / 1000d:0.###} uS)"));
        // 可调 mode 下 tari=0 表示未指定：落到该 mode 的 MinTari（选项范围内）。
        int currentTari = tari == 0 ? min : (int)tari;
        if (!tariOptions.Any(o => Equals(o.Value, currentTari)))
        {
            tariOptions.Add(new SettingsOption(currentTari, $"{currentTari} (current)"));
        }
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.Tari,
            Title = "Tari",
            EditorKind = EditorKind.Choice,
            ValueType = typeof(int),
            Options = tariOptions,
            CurrentValue = currentTari,
            DefaultValue = currentTari,
        });
    }

    /// <summary>
    /// 为能力表中的每个 RF Mode 生成该 mode 的 Tari 约束（固定值/可调范围/下拉选项），
    /// 供 UI 在切换 RF Mode 时重建 Tari 控件。
    /// </summary>
    private static IReadOnlyList<RfModeTariProfile> BuildRfModeTariProfiles(ReaderCapabilities? capabilities)
    {
        if (capabilities?.RfModes is not { Count: > 0 } allModes)
        {
            return [];
        }

        return allModes
            .GroupBy(static mode => mode.ModeIdentifier)
            .Select(group =>
            {
                C1G2RfModeEntry[] modes = group.ToArray();
                bool fixedTari = modes.All(static mode => mode.MinTariValue == mode.MaxTariValue);
                if (fixedTari)
                {
                    return new RfModeTariProfile(
                        (int)group.Key,
                        IsFixedTari: true,
                        FixedTariValue: (int)modes[0].MinTariValue,
                        TariRange: new SettingsRange(modes[0].MinTariValue, modes[0].MaxTariValue),
                        TariOptions: []);
                }

                int min = (int)modes.Min(static mode => mode.MinTariValue);
                int max = (int)modes.Max(static mode => mode.MaxTariValue);
                int step = modes[0].StepTariValue > 0 ? (int)modes[0].StepTariValue : 1;
                var options = new List<SettingsOption>();
                for (int v = min; v <= max; v += step)
                {
                    options.Add(new SettingsOption(v, $"{v} ({v / 1000d:0.###} uS)"));
                }
                if (options.Count == 0)
                {
                    options.Add(new SettingsOption(min, $"{min} ({min / 1000d:0.###} uS)"));
                }

                return new RfModeTariProfile(
                    (int)group.Key,
                    IsFixedTari: false,
                    FixedTariValue: null,
                    TariRange: new SettingsRange(min, max),
                    TariOptions: options);
            })
            .ToArray();
    }

    private static IReadOnlyList<SettingsOption> BuildRfModeOptions(
        ReaderCapabilities? capabilities,
        int currentMode)
    {
        if (capabilities?.RfModes is not { Count: > 0 } modes)
        {
            return [];
        }

        var options = new List<SettingsOption> { new(-1, "默认") };

        options.AddRange(modes
            .GroupBy(static mode => mode.ModeIdentifier)
            .Select(static group => new SettingsOption(
                (int)group.Key,
                FormatTableOption(
                    group.Key,
                    string.Join(
                        " / ",
                        group.Select(FormatRfModeDescription)
                            .Distinct(StringComparer.Ordinal)))))
            .ToList());

        // 当前设备 Mode 未指定（0＝SDK 默认/不指定）时选中“默认”；否则若能力表不包含该
        // mode，则追加一个可选项以允许不改变设备现状地回滚（round-trip）。
        bool inTable = options.Any(option => Equals(option.Value, currentMode));
        if (currentMode != 0 && !inTable)
        {
            options.Add(new SettingsOption(currentMode, $"{currentMode}: current reader mode"));
        }

        return options;
    }

    private static string FormatRfModeDescription(C1G2RfModeEntry mode)
    {
        string mValue = mode.MValue switch
        {
            0 => "M0",
            1 => "M2",
            2 => "M4",
            3 => "M8",
            _ => $"M{mode.MValue}",
        };
        string bdr = mode.BdrValue >= 1000
            ? $"{(mode.BdrValue / 1000d).ToString("0.###", CultureInfo.InvariantCulture)}K"
            : mode.BdrValue.ToString(CultureInfo.InvariantCulture);
        string tari = (mode.MinTariValue / 1000d).ToString("0.0", CultureInfo.InvariantCulture);
        string pie = (mode.PieValue / 1000d).ToString("0.###", CultureInfo.InvariantCulture);

        return $"{mValue}/{bdr}, Tari: {tari} uS, PIE: {pie}";
    }

    private static void AddFilterEntries(
        List<SettingsEntry> entries,
        InventorySettings inventory,
        bool stateAwareSupported)
    {
        IReadOnlyList<InventorySelectFilter> filters = inventory.Filters;
        bool stateAwareEnabled = filters.Any(static filter => filter.StateAwareAction is not null);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StateAwareFiltersEnabled,
            Title = "State-aware filters",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = stateAwareEnabled,
            DefaultValue = false,
            ReadOnlyReason = stateAwareSupported ? null : "Reader 不支持 state-aware singulation。",
        });

        for (int index = 1; index <= 2; index++)
        {
            InventorySelectFilter? filter = filters.Count >= index ? filters[index - 1] : null;
            InventoryStateAwareFilterAction? stateAction = filter?.StateAwareAction;
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterEnabled(index),
                Title = $"Filter {index} enabled",
                EditorKind = EditorKind.Boolean,
                ValueType = typeof(bool),
                CurrentValue = filter is not null,
                DefaultValue = false,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterMemoryBank(index),
                Title = $"Filter {index} memory bank",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = MemoryBankOptions,
                CurrentValue = filter is null ? 1 : (int)filter.MemoryBank,
                DefaultValue = 1,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterOffset(index),
                Title = $"Filter {index} bit offset",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Range = new SettingsRange(0, ushort.MaxValue),
                CurrentValue = filter?.BitPointer ?? 32,
                DefaultValue = 32,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterBitLength(index),
                Title = $"Filter {index} bit length",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Range = new SettingsRange(0, ushort.MaxValue),
                CurrentValue = filter?.BitLength ?? 0,
                DefaultValue = 0,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterMask(index),
                Title = $"Filter {index} mask (hex)",
                EditorKind = EditorKind.Text,
                ValueType = typeof(string),
                CurrentValue = filter is null ? string.Empty : Convert.ToHexString(filter.Mask.Span),
                DefaultValue = string.Empty,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterMatchAction(index),
                Title = $"Filter {index} match action",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = SelectActionOptions,
                CurrentValue = filter is null ? 1 : (int)filter.MatchAction,
                DefaultValue = 1,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterNonMatchAction(index),
                Title = $"Filter {index} non-match action",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = SelectActionOptions,
                CurrentValue = filter is null ? 2 : (int)filter.NonMatchAction,
                DefaultValue = 2,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterStateTarget(index),
                Title = $"Filter {index} state target",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = FilterTargetOptions,
                CurrentValue = stateAction is null ? 1 : (int)stateAction.Target,
                DefaultValue = 1,
                ReadOnlyReason = stateAwareSupported ? null : "Reader 不支持 state-aware singulation。",
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.FilterStateAction(index),
                Title = $"Filter {index} state action",
                EditorKind = EditorKind.Choice,
                ValueType = typeof(int),
                Options = FilterActionOptions,
                CurrentValue = stateAction is null ? 0 : (int)stateAction.Action,
                DefaultValue = 0,
                ReadOnlyReason = stateAwareSupported ? null : "Reader 不支持 state-aware singulation。",
            });
        }

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StateAwareTarget,
            Title = "State-aware singulation target",
            EditorKind = EditorKind.Choice,
            ValueType = typeof(int),
            Options = [new(0, "State A"), new(1, "State B")],
            CurrentValue = inventory.StateAwareSingulation?.Target is InventoryTarget target ? (int)target : 0,
            DefaultValue = 0,
            ReadOnlyReason = stateAwareSupported ? null : "Reader 不支持 state-aware singulation。",
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StateAwareSelectedFlag,
            Title = "State-aware selected flag",
            EditorKind = EditorKind.Choice,
            ValueType = typeof(int),
            Options = [new(0, "Set"), new(1, "Clear"), new(2, "All")],
            CurrentValue = inventory.StateAwareSingulation?.SelectedFlag is InventorySelectedFlag flag ? (int)flag : 2,
            DefaultValue = 2,
            ReadOnlyReason = stateAwareSupported ? null : "Reader 不支持 state-aware singulation。",
        });
    }

    private static void AddTriggerEntries(
        List<SettingsEntry> entries,
        InventorySettings inventory,
        bool gpiSupported,
        ushort? gpiCount)
    {
        string? readOnlyReason = !gpiSupported
            ? "Reader 不支持 GPI。"
            : gpiCount == 0
                ? "Reader 没有 GPI 端口。"
                : null;
        int maximumPort = gpiCount is > 0 ? gpiCount.Value : ushort.MaxValue;

        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StartGpiEnabled,
            Title = "Start inventory from GPI",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = inventory.StartTrigger.Type == InventoryStartTriggerType.Gpi,
            DefaultValue = false,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StartGpiPort,
            Title = "Start GPI port",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = new SettingsRange(1, maximumPort),
            CurrentValue = Math.Clamp((int)inventory.StartTrigger.GpiPortNumber, 1, maximumPort),
            DefaultValue = 1,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StartGpiLevel,
            Title = "Start GPI level high",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = inventory.StartTrigger.GpiState,
            DefaultValue = false,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StopGpiEnabled,
            Title = "Stop inventory from GPI",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = inventory.StopTrigger.Type == InventoryStopTriggerType.GpiWithTimeout,
            DefaultValue = false,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StopGpiPort,
            Title = "Stop GPI port",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = new SettingsRange(1, maximumPort),
            CurrentValue = Math.Clamp((int)inventory.StopTrigger.GpiPortNumber, 1, maximumPort),
            DefaultValue = 1,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StopGpiLevel,
            Title = "Stop GPI level high",
            EditorKind = EditorKind.Boolean,
            ValueType = typeof(bool),
            CurrentValue = inventory.StopTrigger.GpiState,
            DefaultValue = false,
            ReadOnlyReason = readOnlyReason,
        });
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.StopGpiTimeoutMs,
            Title = "Stop GPI timeout (ms)",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = new SettingsRange(0, uint.MaxValue),
            CurrentValue = inventory.StopTrigger.TimeoutMilliseconds,
            DefaultValue = 1000,
            ReadOnlyReason = readOnlyReason,
        });
    }

    private static void AddReportEntries(List<SettingsEntry> entries, InventoryReportSettings report)
    {
        AddBooleanEntry(entries, SettingsKeys.ReportAntenna, "Report antenna", report.IncludeAntennaId);
        AddBooleanEntry(entries, SettingsKeys.ReportChannel, "Report channel", report.IncludeChannelIndex);
        AddBooleanEntry(entries, SettingsKeys.ReportRssi, "Report peak RSSI", report.IncludePeakRssi);
        AddBooleanEntry(entries, SettingsKeys.ReportFirstSeen, "Report first seen timestamp", report.IncludeFirstSeenTimestamp);
        AddBooleanEntry(entries, SettingsKeys.ReportLastSeen, "Report last seen timestamp", report.IncludeLastSeenTimestamp);
        AddBooleanEntry(entries, SettingsKeys.ReportTagCount, "Report tag seen count", report.IncludeTagSeenCount);
        AddBooleanEntry(entries, SettingsKeys.ReportPcBits, "Report PC bits", report.IncludePcBits);
    }

    private static void AddBooleanEntry(List<SettingsEntry> entries, string key, string title, bool value) => entries.Add(new SettingsEntry
    {
        Key = key,
        Title = title,
        EditorKind = EditorKind.Boolean,
        ValueType = typeof(bool),
        CurrentValue = value,
        DefaultValue = value,
    });

    private static IReadOnlyList<InventoryAntennaConfiguration> BuildAntennaConfigurations(
        SettingsDraft draft,
        InventorySettings baseline,
        ReaderCapabilities? capabilities,
        bool individual)
    {
        IReadOnlyList<ushort> antennaIds = baseline.AntennaIds;
        if (individual)
        {
            var configurations = new List<InventoryAntennaConfiguration>();
            foreach (ushort antennaId in antennaIds.Where(static id => id != 0).Distinct())
            {
                InventoryAntennaConfiguration configuration = ResolveAntennaConfiguration(baseline, antennaId)
                    ?? new InventoryAntennaConfiguration { AntennaId = antennaId };
                ushort? txIndex = GetUshort(draft, SettingsKeys.AntennaTxPowerIndex(antennaId));
                ushort? rxIndex = GetUshort(draft, SettingsKeys.AntennaRxSensitivityIndex(antennaId));
                configuration = ApplyAntennaValues(configuration, antennaId, txIndex, rxIndex, capabilities);
                configurations.Add(configuration);
            }

            return configurations;
        }

        ushort? globalTxIndex = GetUshort(draft, SettingsKeys.TxPowerIndex);
        ushort? globalRxIndex = GetUshort(draft, SettingsKeys.RxSensitivityIndex);
        return antennaIds
            .Where(static antennaId => antennaId > 0)
            .Distinct()
            .Select(antennaId =>
            {
                InventoryAntennaConfiguration existing = ResolveAntennaConfiguration(baseline, antennaId)
                    ?? new InventoryAntennaConfiguration { AntennaId = antennaId };
                return ApplyAntennaValues(existing, antennaId, globalTxIndex, globalRxIndex, capabilities);
            })
            .ToArray();
    }

    private static InventorySettings ResolveInventoryBaseline(ReaderSettingsRuntimeSnapshot runtime)
    {
        if (runtime.Settings.ManagedRoSpec?.Inventory is { } managedInventory)
        {
            return managedInventory;
        }

        if (runtime.Settings.Settings.Inventory is { } currentInventory)
        {
            return currentInventory;
        }

        // A reader may have valid GET_READER_CONFIG antenna RF values while no ROSpec
        // exists yet. Project those values into the settings editor so the first save
        // does not show SDK defaults or overwrite the device's current antenna setup.
        return CreateInventoryBaseline(runtime.Settings.Settings.Configuration);
    }

    private static InventorySettings CreateInventoryBaseline(ReaderConfiguration configuration)
    {
        IReadOnlyList<AntennaConfigurationSettings> source = configuration.Antennas ?? [];
        ushort[] antennaIds = source
            .Select(static antenna => antenna.AntennaId)
            .Where(static antennaId => antennaId > 0)
            .Distinct()
            .OrderBy(static antennaId => antennaId)
            .ToArray();

        AntennaConfigurationSettings[] rfSources = source
            .Where(static antenna =>
                antenna.TransmitPowerIndex.HasValue
                || antenna.ReceiverSensitivityIndex.HasValue
                || antenna.HopTableId.HasValue
                || antenna.ChannelIndex.HasValue)
            .ToArray();
        AntennaConfigurationSettings[] explicitRfSources = rfSources
            .Where(static antenna => antenna.AntennaId > 0)
            .ToArray();
        IReadOnlyList<AntennaConfigurationSettings> selectedSources = explicitRfSources.Length > 0
            ? explicitRfSources
            : rfSources.Where(static antenna => antenna.AntennaId == 0).Take(1).ToArray();

        return new InventorySettings
        {
            AntennaIds = antennaIds.Length > 0 ? antennaIds : [0],
            AntennaConfigurations = selectedSources
                .Select(static antenna => new InventoryAntennaConfiguration
                {
                    AntennaId = antenna.AntennaId,
                    TransmitPowerIndex = antenna.TransmitPowerIndex,
                    ReceiverSensitivityIndex = antenna.ReceiverSensitivityIndex,
                    HopTableId = antenna.HopTableId,
                    ChannelIndex = antenna.ChannelIndex,
                })
                .ToArray(),
        };
    }

    private static InventoryAntennaConfiguration ApplyAntennaValues(
        InventoryAntennaConfiguration configuration,
        ushort antennaId,
        ushort? txIndex,
        ushort? rxIndex,
        ReaderCapabilities? capabilities)
    {
        txIndex ??= configuration.TransmitPowerIndex;
        rxIndex ??= configuration.ReceiverSensitivityIndex;
        ushort? hopTableId = configuration.HopTableId;
        ushort? channelIndex = configuration.ChannelIndex;
        if (txIndex is not null)
        {
            // RFTransmitter is a complete LLRP tuple. The standard settings page does
            // not own fixed-frequency selection, so ChannelIndex is always the neutral
            // structural value 1. A future standard fixed-frequency editor may replace
            // this value; vendor fixed-frequency extensions remain responsible for their
            // own channel selection.
            channelIndex = 1;
            hopTableId = ResolveHopTableId(hopTableId, capabilities);
        }

        return configuration with
        {
            AntennaId = antennaId,
            TransmitPowerIndex = txIndex,
            ReceiverSensitivityIndex = rxIndex,
            ChannelIndex = channelIndex,
            HopTableId = hopTableId,
        };
    }

    private static ushort ResolveHopTableId(ushort? existing, ReaderCapabilities? capabilities)
    {
        if (existing is > 0)
        {
            return existing.Value;
        }

        ushort? advertised = capabilities?.HopTables
            .Select(static table => (ushort)table.HopTableId)
            .FirstOrDefault(static id => id > 0);
        if (advertised is > 0)
        {
            return advertised.Value;
        }

        // The standard settings page does not expose fixed-frequency selection.
        // Use the standard device default table instead of emitting HopTableID=0,
        // which is rejected by the target standard reader and is not a safe
        // substitute for a real capability-table identifier.
        return 1;
    }

    private static IReadOnlyList<ushort> ParseAntennaIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Antennas must not be empty; select one or more explicit device antenna IDs.");
        }

        ushort[] antennaIds = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ushort.Parse(part, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
        if (antennaIds.Contains((ushort)0))
        {
            throw new InvalidOperationException("Antenna ID 0 is not supported; select the explicit device antenna IDs.");
        }

        return antennaIds;
    }

    private static InventorySettings ExpandAllAntennas(
        InventorySettings inventory,
        ReaderRuntimeSnapshot reader,
        ReaderCapabilities? capabilities)
    {
        ushort[] availableAntennaIds = reader.Antennas
            .Select(static antenna => antenna.AntennaId)
            .Where(static antennaId => antennaId > 0)
            .Distinct()
            .OrderBy(static antennaId => antennaId)
            .ToArray();
        if (availableAntennaIds.Length == 0 && capabilities?.MaxNumberOfAntennas is > 0 and ushort maxAntennas)
        {
            availableAntennaIds = Enumerable.Range(1, maxAntennas)
                .Select(static antennaId => checked((ushort)antennaId))
                .ToArray();
        }

        bool selectsAll = inventory.AntennaIds.Count == 0 || inventory.AntennaIds.Contains((ushort)0);
        ushort[] selectedAntennaIds = selectsAll
            ? availableAntennaIds
            : inventory.AntennaIds
                .Where(static antennaId => antennaId > 0)
                .Distinct()
                .ToArray();
        if (selectedAntennaIds.Length == 0)
        {
            throw new InvalidOperationException("The reader did not advertise any explicit antenna IDs; antenna ID 0 will not be sent.");
        }

        InventoryAntennaConfiguration? commonConfiguration = inventory.AntennaConfigurations
            .FirstOrDefault(static configuration => configuration.AntennaId == 0);
        Dictionary<ushort, InventoryAntennaConfiguration> explicitConfigurations = inventory.AntennaConfigurations
            .Where(static configuration => configuration.AntennaId > 0)
            .GroupBy(static configuration => configuration.AntennaId)
            .ToDictionary(static group => group.Key, static group => group.First());
        InventoryAntennaConfiguration[] antennaConfigurations = selectedAntennaIds
            .Select(antennaId => explicitConfigurations.TryGetValue(antennaId, out InventoryAntennaConfiguration? explicitConfiguration)
                ? explicitConfiguration
                : commonConfiguration is null
                    ? null
                    : commonConfiguration with { AntennaId = antennaId })
            .Where(static configuration => configuration is not null)
            .Select(static configuration => configuration!)
            .ToArray();

        return inventory with
        {
            AntennaIds = selectedAntennaIds,
            AntennaConfigurations = antennaConfigurations,
        };
    }

    private static ushort? GetUshort(SettingsDraft draft, string key) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToUInt16(value, CultureInfo.InvariantCulture)
            : null;

    private static InventorySelectFilter[] BuildFilters(
        SettingsDraft draft,
        IReadOnlyList<InventorySelectFilter> baseline,
        bool stateAwareSupported)
    {
        bool stateAwareEnabled = stateAwareSupported && GetBool(draft, SettingsKeys.StateAwareFiltersEnabled);
        var filters = new List<InventorySelectFilter>(2);
        for (int index = 1; index <= 2; index++)
        {
            if (!GetBool(draft, SettingsKeys.FilterEnabled(index)))
            {
                continue;
            }

            string maskText = GetString(draft, SettingsKeys.FilterMask(index));
            if (string.IsNullOrWhiteSpace(maskText))
            {
                continue;
            }

            string normalizedMask = NormalizeHex(maskText);
            byte[] mask = Convert.FromHexString(normalizedMask);
            ushort memoryBank = checked((ushort)GetInt(draft, SettingsKeys.FilterMemoryBank(index), 1));
            ushort offset = checked((ushort)GetInt(draft, SettingsKeys.FilterOffset(index), 32));
            ushort bitLength = checked((ushort)GetInt(draft, SettingsKeys.FilterBitLength(index), mask.Length * 8));

            if (!Enum.IsDefined((LlrpSdk.TagMemoryBank)memoryBank))
            {
                throw new FormatException($"Filter {index} 的 Memory Bank 无效。");
            }

            if (bitLength == 0 || bitLength > mask.Length * 8)
            {
                throw new FormatException($"Filter {index} 的位长度必须大于 0 且不能超过掩码长度。");
            }

            InventorySelectAction matchAction = (InventorySelectAction)GetInt(
                draft,
                SettingsKeys.FilterMatchAction(index),
                1);
            InventorySelectAction nonMatchAction = (InventorySelectAction)GetInt(
                draft,
                SettingsKeys.FilterNonMatchAction(index),
                2);
            if (!Enum.IsDefined(matchAction) || !Enum.IsDefined(nonMatchAction))
            {
                throw new FormatException($"Filter {index} 的匹配动作无效。");
            }

            InventorySelectFilter filter = new()
            {
                MemoryBank = memoryBank,
                BitPointer = offset,
                Mask = mask,
                BitLength = bitLength,
                MatchAction = matchAction,
                NonMatchAction = nonMatchAction,
            };
            if (stateAwareEnabled)
            {
                InventoryFilterTarget target = (InventoryFilterTarget)GetInt(
                    draft,
                    SettingsKeys.FilterStateTarget(index),
                    1);
                InventoryFilterAction action = (InventoryFilterAction)GetInt(
                    draft,
                    SettingsKeys.FilterStateAction(index),
                    0);
                if (!Enum.IsDefined(target) || !Enum.IsDefined(action))
                {
                    throw new FormatException($"Filter {index} 的 state-aware 动作无效。");
                }

                filter = filter with
                {
                    StateAwareAction = new InventoryStateAwareFilterAction
                    {
                        Target = target,
                        Action = action,
                    },
                };
            }

            filters.Add(filter);
        }

        return filters.ToArray();
    }

    private static InventoryStateAwareSingulation? BuildStateAwareSingulation(SettingsDraft draft, bool supported)
    {
        if (!supported || !GetBool(draft, SettingsKeys.StateAwareFiltersEnabled))
        {
            return null;
        }

        return new InventoryStateAwareSingulation
        {
            Target = (InventoryTarget)GetInt(draft, SettingsKeys.StateAwareTarget, 0),
            SelectedFlag = (InventorySelectedFlag)GetInt(draft, SettingsKeys.StateAwareSelectedFlag, 2),
        };
    }

    private static InventoryReportSettings BuildReport(SettingsDraft draft, InventoryReportSettings baseline) => baseline with
    {
        IncludeAntennaId = GetBool(draft, SettingsKeys.ReportAntenna, baseline.IncludeAntennaId),
        IncludeChannelIndex = GetBool(draft, SettingsKeys.ReportChannel, baseline.IncludeChannelIndex),
        IncludePeakRssi = GetBool(draft, SettingsKeys.ReportRssi, baseline.IncludePeakRssi),
        IncludeFirstSeenTimestamp = GetBool(draft, SettingsKeys.ReportFirstSeen, baseline.IncludeFirstSeenTimestamp),
        IncludeLastSeenTimestamp = GetBool(draft, SettingsKeys.ReportLastSeen, baseline.IncludeLastSeenTimestamp),
        IncludeTagSeenCount = GetBool(draft, SettingsKeys.ReportTagCount, baseline.IncludeTagSeenCount),
        IncludePcBits = GetBool(draft, SettingsKeys.ReportPcBits, baseline.IncludePcBits),
    };

    private static InventoryStartTrigger BuildStartTrigger(SettingsDraft draft, InventoryStartTrigger baseline) =>
        GetBool(draft, SettingsKeys.StartGpiEnabled)
            ? new InventoryStartTrigger
            {
                Type = InventoryStartTriggerType.Gpi,
                GpiPortNumber = checked((ushort)GetInt(draft, SettingsKeys.StartGpiPort, Math.Max(1, (int)baseline.GpiPortNumber))),
                GpiState = GetBool(draft, SettingsKeys.StartGpiLevel, baseline.GpiState),
                TimeoutMilliseconds = baseline.TimeoutMilliseconds,
            }
            : baseline with { Type = InventoryStartTriggerType.Immediate };

    private static InventoryStopTrigger BuildStopTrigger(SettingsDraft draft, InventoryStopTrigger baseline) =>
        GetBool(draft, SettingsKeys.StopGpiEnabled)
            ? new InventoryStopTrigger
            {
                Type = InventoryStopTriggerType.GpiWithTimeout,
                GpiPortNumber = checked((ushort)GetInt(draft, SettingsKeys.StopGpiPort, Math.Max(1, (int)baseline.GpiPortNumber))),
                GpiState = GetBool(draft, SettingsKeys.StopGpiLevel, baseline.GpiState),
                TimeoutMilliseconds = checked((uint)GetInt(draft, SettingsKeys.StopGpiTimeoutMs, (int)Math.Min(int.MaxValue, baseline.TimeoutMilliseconds))),
            }
            : baseline with { Type = InventoryStopTriggerType.None };

    private static string GetString(SettingsDraft draft, string key, string fallback = "") =>
        draft.Values.TryGetValue(key, out object? value) && value is not null ? FormatInvariant(value) : fallback;

    private static string FormatInvariant(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty,
    };

    private static string NormalizeHex(string value)
    {
        string normalized = new string(value
            .Where(static character => !char.IsWhiteSpace(character) && character is not '-' and not ':')
            .ToArray());
        return normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? normalized[2..]
            : normalized;
    }

    private static int GetInt(SettingsDraft draft, string key, int fallback = 0) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToInt32(value, CultureInfo.InvariantCulture)
            : fallback;

    private static bool GetBool(SettingsDraft draft, string key, bool fallback = false) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToBoolean(value, CultureInfo.InvariantCulture)
            : fallback;

    private static InventoryAntennaConfiguration? ResolveAntennaConfiguration(InventorySettings inventory, ushort antenna)
    {
        InventoryAntennaConfiguration? specific = inventory.AntennaConfigurations.FirstOrDefault(c => c.AntennaId == antenna);
        return specific ?? (antenna != 0
            ? inventory.AntennaConfigurations.FirstOrDefault(static c => c.AntennaId == 0)
            : null);
    }

    private static string FormatAntennaIds(IReadOnlyList<ushort> ids, IReadOnlyList<ReaderAntennaInfo> capabilities)
    {
        if (ids.Count == 0 || (ids.Count == 1 && ids[0] == 0))
        {
            return string.Join(", ", capabilities.Select(static antenna => antenna.AntennaId));
        }

        return string.Join(", ", ids);
    }

    private static ushort? ResolveTxPowerIndex(
        InventorySettings inventory,
        ushort antenna)
    {
        return ResolveAntennaConfiguration(inventory, antenna)?.TransmitPowerIndex;
    }

    private static SettingsRange ResolveTxPowerIndexRange(ReaderCapabilities? capabilities)
    {
        if (capabilities?.TxPowers is not { Count: > 0 } powers)
        {
            return new SettingsRange(0, ushort.MaxValue);
        }

        return new SettingsRange(
            powers.Min(static p => (decimal)p.Index),
            powers.Max(static p => (decimal)p.Index));
    }

    private static IReadOnlyList<SettingsOption> BuildTxPowerOptions(ReaderCapabilities? capabilities) =>
        capabilities?.TxPowers is { Count: > 0 } powers
            ? powers
                .Select(power => new SettingsOption(
                    power.Index,
                    FormatTableOption(
                        power.Index,
                        $"{power.TransmitPowerDbm.ToString("0.###", CultureInfo.InvariantCulture)} dBm")))
                .ToArray()
            : [];

    private static ushort? ResolveRxSensitivityIndex(
        InventorySettings inventory,
        ushort antenna)
    {
        return ResolveAntennaConfiguration(inventory, antenna)?.ReceiverSensitivityIndex;
    }

    private static SettingsRange ResolveRxSensitivityIndexRange(ReaderCapabilities? capabilities)
    {
        if (capabilities?.RxSensitivities is not { Count: > 0 } sensitivities)
        {
            return new SettingsRange(0, ushort.MaxValue);
        }

        return new SettingsRange(
            sensitivities.Min(static r => (decimal)r.Index),
            sensitivities.Max(static r => (decimal)r.Index));
    }

    private static IReadOnlyList<SettingsOption> BuildRxSensitivityOptions(ReaderCapabilities? capabilities)
    {
        if (capabilities?.RxSensitivities is not { Count: > 0 } sensitivities)
        {
            return [];
        }

        // 1.1/2.0 设备提供 MaximumReceiveSensitivityDbm 时显示实际灵敏度（Max + 偏移）；
        // 1.0.1 无该参数（null），保留 "offset dB offset" 描述。写入值始终是能力表 index。
        short? maxDbm = capabilities.MaximumReceiveSensitivityDbm;
        return sensitivities
            .Select(sensitivity =>
            {
                string description = maxDbm is { } max
                    ? $"{sensitivity.ReceiveSensitivityDb.ToString(CultureInfo.InvariantCulture)} ({(max + sensitivity.ReceiveSensitivityDb).ToString(CultureInfo.InvariantCulture)} dBm)"
                    : $"{sensitivity.ReceiveSensitivityDb.ToString(CultureInfo.InvariantCulture)} dB offset";
                return new SettingsOption(
                    sensitivity.Index,
                    FormatTableOption(sensitivity.Index, description));
            })
            .ToArray();
    }

    private static ushort ToUshortRangeValue(decimal value) =>
        checked((ushort)Math.Clamp(value, 0, ushort.MaxValue));

    private static string FormatTableOption(object index, string description) =>
        $"{Convert.ToString(index, CultureInfo.InvariantCulture)} ({description})";

    private static SettingsRange ResolveTariRange(
        ReaderCapabilities? capabilities,
        ushort modeIndex)
    {
        C1G2RfModeEntry[] modes = SelectModes(capabilities, modeIndex);
        return modes.Length == 0
            ? new SettingsRange(0, ushort.MaxValue)
            : new SettingsRange(
                modes.Min(static mode => (decimal)mode.MinTariValue),
                modes.Max(static mode => (decimal)mode.MaxTariValue));
    }

    private static ushort ResolveTari(
        ReaderCapabilities? capabilities,
        ushort modeIndex,
        ushort tari)
    {
        C1G2RfModeEntry[] modes = SelectModes(capabilities, modeIndex);
        if (modes.Length == 0)
        {
            return tari;
        }

        bool fixedTari = modes.All(m => m.MinTariValue == m.MaxTariValue);
        // 设备为该 mode 声明固定 Tari（如 Impinj R400 Mode 1002：Min==Max==6250），设备层按 lrb 能力表
        // 要求该值，不接受 0。UI 已隐藏 Tari 入口，编译时直接使用设备当前运行的合法 Tari；
        // 若为空则补该 mode 的 MinTariValue。固定 mode 不抛“超出允许范围”，也不下发 0。
        if (fixedTari)
        {
            return checked((ushort)modes[0].MinTariValue);
        }

        if (tari == 0)
        {
            return checked((ushort)modes[0].MinTariValue);
        }

        if (modes.Any(mode => IsSupportedTari(mode, tari)))
        {
            return tari;
        }

        throw new InvalidOperationException($"Tari {tari} is not valid for RF Mode {modeIndex}.");
    }

    /// <summary>当前 mode 是否由设备声明为固定 Tari（宽范围内 Min==Max）。固定时 UI 不暴露输入。</summary>
    private static bool IsTariFixedByMode(ReaderCapabilities? capabilities, ushort modeIndex)
    {
        C1G2RfModeEntry[] modes = SelectModes(capabilities, modeIndex);
        return modes.Length > 0 && modes.All(mode => mode.MinTariValue == mode.MaxTariValue);
    }

    /// <summary>按 LLRP 标准：ModeIndex 引用能力表中 ModeIdentifier 相同的 RF mode。
    /// ModeIdentifier=0 也可能是设备能力表中的真实 RF Mode，不能与 UI 的默认哨兵 -1 混淆。</summary>
    private static C1G2RfModeEntry[] SelectModes(
        ReaderCapabilities? capabilities,
        ushort modeIndex)
    {
        IReadOnlyList<C1G2RfModeEntry>? all = capabilities?.RfModes;
        if (all is not { Count: > 0 } modes)
        {
            return [];
        }

        return modes.Where(mode => mode.ModeIdentifier == modeIndex).ToArray();
    }

    private static bool IsSupportedTari(C1G2RfModeEntry mode, ushort tari)
    {
        if (tari < mode.MinTariValue || tari > mode.MaxTariValue)
        {
            return false;
        }

        return mode.StepTariValue == 0
            ? mode.MinTariValue == mode.MaxTariValue && tari == mode.MinTariValue
            : (tari - mode.MinTariValue) % mode.StepTariValue == 0;
    }
}
