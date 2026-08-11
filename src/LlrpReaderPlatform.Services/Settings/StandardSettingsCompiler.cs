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
            Key = SettingsKeys.TxPowerDbm,
            Title = "Tx Power (dBm)",
            EditorKind = EditorKind.Decimal,
            ValueType = typeof(decimal),
            Range = new SettingsRange(0, 30),
            CurrentValue = 20m,
            DefaultValue = 20m,
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
                case SettingsKeys.TxPowerDbm:
                    compiled.TxPowerDbm = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    break;
                case SettingsKeys.RxSensitivityDb:
                    compiled.RxSensitivityDb = Convert.ToInt32(value, CultureInfo.InvariantCulture);
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

        InventorySettings inventory = runtime.Settings.ManagedRoSpec?.Inventory
            ?? runtime.Settings.Settings.Inventory
            ?? new InventorySettings();
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

        decimal? txPower = ResolveTxPower(inventory, currentAntenna, runtime.Capabilities);
        SettingsRange range = ResolveTxPowerRange(runtime.Capabilities);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.TxPowerDbm,
            Title = "Tx Power (dBm)",
            EditorKind = EditorKind.Decimal,
            ValueType = typeof(decimal),
            Options = BuildTxPowerOptions(runtime.Capabilities),
            Range = range,
            CurrentValue = txPower ?? range.Min,
            DefaultValue = txPower ?? range.Min,
        });

        SettingsRange rxRange = ResolveRxSensitivityRange(runtime.Capabilities);
        int rxCurrent = ResolveRxSensitivity(inventory, currentAntenna, runtime.Capabilities) ?? (int)rxRange.Min;
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.RxSensitivityDb,
            Title = "Rx Sensitivity (dBm)",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Options = BuildRxSensitivityOptions(runtime.Capabilities),
            Range = rxRange,
            CurrentValue = rxCurrent,
            DefaultValue = rxCurrent,
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

        int currentRfMode = (int)inventory.ModeIndex;
        IReadOnlyList<SettingsOption> rfModes = BuildRfModeOptions(runtime.Capabilities, currentRfMode);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.RfMode,
            Title = "RF Mode",
            EditorKind = rfModes.Count > 0 ? EditorKind.Choice : EditorKind.Integer,
            ValueType = typeof(int),
            Options = rfModes,
            Range = rfModes.Count > 0 ? null : new SettingsRange(0, ushort.MaxValue),
            CurrentValue = currentRfMode,
            DefaultValue = currentRfMode,
        });

        SettingsRange tariRange = ResolveTariRange(runtime.Capabilities, inventory.ModeIndex, inventory.Tari);
        entries.Add(new SettingsEntry
        {
            Key = SettingsKeys.Tari,
            Title = "Tari",
            EditorKind = EditorKind.Integer,
            ValueType = typeof(int),
            Range = tariRange,
            CurrentValue = (int)inventory.Tari,
            DefaultValue = (int)inventory.Tari,
        });

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
            InventoryAntennaConfiguration? configuration = ResolveAntennaConfiguration(inventory, antenna.AntennaId);
            decimal? antennaTx = ResolveTxPower(inventory, antenna.AntennaId, runtime.Capabilities);
            int? antennaRx = ResolveRxSensitivity(inventory, antenna.AntennaId, runtime.Capabilities);
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.AntennaTxPowerDbm(antenna.AntennaId),
                Title = $"{antenna.Name ?? $"Antenna {antenna.AntennaId}"} Tx Power (dBm)",
                EditorKind = EditorKind.Decimal,
                ValueType = typeof(decimal),
                Options = BuildTxPowerOptions(runtime.Capabilities),
                Range = range,
                CurrentValue = antennaTx ?? txPower ?? range.Min,
                DefaultValue = antennaTx ?? txPower ?? range.Min,
            });
            entries.Add(new SettingsEntry
            {
                Key = SettingsKeys.AntennaRxSensitivityDb(antenna.AntennaId),
                Title = $"{antenna.Name ?? $"Antenna {antenna.AntennaId}"} Rx Sensitivity (dBm)",
                EditorKind = EditorKind.Integer,
                ValueType = typeof(int),
                Options = BuildRxSensitivityOptions(runtime.Capabilities),
                Range = rxRange,
                CurrentValue = antennaRx ?? rxCurrent,
                DefaultValue = antennaRx ?? rxCurrent,
            });

            if (GetFrequencyCount(runtime.Capabilities) > 0)
            {
                SettingsRange channelRange = new(1, GetFrequencyCount(runtime.Capabilities));
                int channel = configuration?.ChannelIndex is ushort value ? value : 1;
                entries.Add(new SettingsEntry
                {
                    Key = SettingsKeys.AntennaChannelIndex(antenna.AntennaId),
                    Title = $"{antenna.Name ?? $"Antenna {antenna.AntennaId}"} Channel",
                    EditorKind = EditorKind.Integer,
                    ValueType = typeof(int),
                    Range = channelRange,
                    CurrentValue = channel,
                    DefaultValue = channel,
                });
            }
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
            ?? new InventorySettings();

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
            inventory = inventory with { ModeIndex = checked((ushort)mode) };
        }

        if (compiled.Tari is int tari)
        {
            inventory = inventory with { Tari = checked((ushort)tari) };
        }

        if (compiled.AntennaIds is { Count: > 0 } antennaIds)
        {
            inventory = inventory with { AntennaIds = antennaIds };
        }
        else if (compiled.AntennaId is ushort selectedAntenna)
        {
            inventory = inventory with { AntennaIds = [selectedAntenna] };
        }

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

        // ReaderConfiguration.Antennas is the Reader-wide SET_READER_CONFIG projection
        // returned by GET_READER_CONFIG. Inventory RF belongs to the ROSpec projection
        // above. Echoing the Reader-wide antenna list makes some standard readers reject
        // their own RFTransmitter values, while the same tuple is valid in a ROSpec.
        // Do not replay those unrelated Reader-wide antenna defaults when saving Inventory.
        ReaderSettings compiledSettings = baseline with
        {
            Inventory = inventory,
            Configuration = baseline.Configuration with { Antennas = [] },
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

    private static IReadOnlyList<SettingsOption> BuildRfModeOptions(
        ReaderCapabilities? capabilities,
        int currentMode)
    {
        if (capabilities?.RfModes is not { Count: > 0 } modes)
        {
            return [];
        }

        var options = modes
            .Select(static mode => new SettingsOption(
                (int)mode.ModeIdentifier,
                $"{mode.ModeIdentifier}: {mode.ForwardLinkModulation}"))
            .ToList();

        // Some readers report a valid active mode that is absent from the RF mode
        // capability table. Keep that value selectable so an unrelated setting
        // change can round-trip without the service rejecting the reader's own
        // current configuration.
        if (!options.Any(option => Equals(option.Value, currentMode)))
        {
            options.Add(new SettingsOption(currentMode, $"{currentMode}: current reader mode"));
        }

        return options;
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
        IReadOnlyList<ushort> antennaIds = GetAntennaIds(draft, baseline);
        if (individual)
        {
            var configurations = new List<InventoryAntennaConfiguration>();
            foreach (ushort antennaId in antennaIds.Where(static id => id != 0).Distinct())
            {
                InventoryAntennaConfiguration configuration = ResolveAntennaConfiguration(baseline, antennaId)
                    ?? new InventoryAntennaConfiguration { AntennaId = antennaId };
                decimal? tx = GetDecimal(draft, SettingsKeys.AntennaTxPowerDbm(antennaId));
                int? rx = GetInt(draft, SettingsKeys.AntennaRxSensitivityDb(antennaId));
                int? channel = GetInt(draft, SettingsKeys.AntennaChannelIndex(antennaId));
                configuration = ApplyAntennaValues(configuration, antennaId, tx, rx, channel, capabilities);
                configurations.Add(configuration);
            }

            return configurations;
        }

        decimal? globalTx = GetDecimal(draft, SettingsKeys.TxPowerDbm);
        int? globalRx = GetInt(draft, SettingsKeys.RxSensitivityDb);
        InventoryAntennaConfiguration existing = ResolveAntennaConfiguration(baseline, 0)
            ?? new InventoryAntennaConfiguration { AntennaId = 0 };
        if (globalTx is null && globalRx is null && existing.TransmitPowerIndex is null && existing.ReceiverSensitivityIndex is null)
        {
            return baseline.AntennaConfigurations;
        }

        return [ApplyAntennaValues(existing, 0, globalTx, globalRx, existing.ChannelIndex, capabilities)];
    }

    private static InventoryAntennaConfiguration ApplyAntennaValues(
        InventoryAntennaConfiguration configuration,
        ushort antennaId,
        decimal? txDbm,
        int? rxDbm,
        int? channel,
        ReaderCapabilities? capabilities)
    {
        ushort? txIndex = txDbm is decimal tx && capabilities?.TxPowers is { Count: > 0 } powers
            ? powers.OrderBy(p => Math.Abs((decimal)p.TransmitPowerDbm - tx)).First().Index
            : configuration.TransmitPowerIndex;
        ushort? rxIndex = rxDbm is int rx && capabilities?.RxSensitivities is { Count: > 0 } sensitivities
            ? sensitivities.OrderBy(r => Math.Abs(r.ReceiveSensitivityDb - rx)).First().Index
            : configuration.ReceiverSensitivityIndex;
        ushort? channelIndex = channel is int channelValue and > 0 and <= ushort.MaxValue
            ? (ushort)channelValue
            : configuration.ChannelIndex;
        ushort? hopTableId = configuration.HopTableId;
        if (txIndex is not null)
        {
            // RFTransmitter is a complete LLRP tuple. Even when a hopping region ignores
            // ChannelIndex, the SDK requires TransmitPower, HopTableID, and ChannelIndex
            // to be supplied together. Channel 1 is therefore the neutral structural
            // value for hopping readers; a future standard fixed-frequency editor will
            // replace it with the selected FixedFrequencyTable index.
            hopTableId ??= capabilities?.HopTables is { Count: > 0 } hopTables
                ? hopTables[0].HopTableId
                : (ushort)0;
            channelIndex ??= 1;
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

    private static IReadOnlyList<ushort> GetAntennaIds(SettingsDraft draft, InventorySettings baseline)
    {
        if (draft.Values.TryGetValue(SettingsKeys.AntennaIds, out object? value))
        {
            return ParseAntennaIds(value is null ? null : FormatInvariant(value));
        }

        if (draft.Values.TryGetValue(SettingsKeys.Antenna, out value) && value is not null)
        {
            return [Convert.ToUInt16(value, CultureInfo.InvariantCulture)];
        }

        return baseline.AntennaIds.Count > 0 ? baseline.AntennaIds : [0];
    }

    private static IReadOnlyList<ushort> ParseAntennaIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Antennas must not be empty; use ALL to select every antenna.");
        }

        return text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ushort.Parse(part, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct()
            .ToArray();
    }

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

    private static decimal? GetDecimal(SettingsDraft draft, string key) =>
        draft.Values.TryGetValue(key, out object? value) && value is not null
            ? Convert.ToDecimal(value, CultureInfo.InvariantCulture)
            : null;

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

    private static int GetFrequencyCount(ReaderCapabilities? capabilities) =>
        capabilities?.HopTables.FirstOrDefault()?.Frequencies.Count
        ?? capabilities?.TxFrequencies.Count
        ?? 0;

    private static decimal? ResolveTxPower(
        InventorySettings inventory,
        ushort antenna,
        ReaderCapabilities? capabilities)
    {
        ushort? index = inventory.AntennaConfigurations
            .FirstOrDefault(c => c.AntennaId == antenna)?.TransmitPowerIndex;
        if (index is null || capabilities?.TxPowers is not { Count: > 0 } powers)
        {
            return null;
        }

        TxPowerEntry? entry = powers.FirstOrDefault(p => p.Index == index.Value);
        return entry is null ? null : (decimal)entry.TransmitPowerDbm;
    }

    private static SettingsRange ResolveTxPowerRange(ReaderCapabilities? capabilities)
    {
        if (capabilities?.TxPowers is not { Count: > 0 } powers)
        {
            return new SettingsRange(0, 30);
        }

        return new SettingsRange(
            (decimal)powers.Min(static p => p.TransmitPowerDbm),
            (decimal)powers.Max(static p => p.TransmitPowerDbm));
    }

    private static IReadOnlyList<SettingsOption> BuildTxPowerOptions(ReaderCapabilities? capabilities) =>
        capabilities?.TxPowers is { Count: > 0 } powers
            ? powers
                .Select(power => new SettingsOption(
                    (decimal)power.TransmitPowerDbm,
                    $"Index {power.Index}: {power.TransmitPowerDbm.ToString("0.###", CultureInfo.InvariantCulture)} dBm"))
                .ToArray()
            : [];

    private static int? ResolveRxSensitivity(
        InventorySettings inventory,
        ushort antenna,
        ReaderCapabilities? capabilities)
    {
        ushort? index = inventory.AntennaConfigurations
            .FirstOrDefault(c => c.AntennaId == antenna)?.ReceiverSensitivityIndex;
        if (index is null || capabilities?.RxSensitivities is not { Count: > 0 } sensitivities)
        {
            return null;
        }

        RxSensitivityEntry? entry = sensitivities.FirstOrDefault(r => r.Index == index.Value);
        return entry?.ReceiveSensitivityDb;
    }

    private static SettingsRange ResolveRxSensitivityRange(ReaderCapabilities? capabilities)
    {
        if (capabilities?.RxSensitivities is not { Count: > 0 } sensitivities)
        {
            return new SettingsRange(-120, 0);
        }

        return new SettingsRange(
            sensitivities.Min(static r => r.ReceiveSensitivityDb),
            sensitivities.Max(static r => r.ReceiveSensitivityDb));
    }

    private static IReadOnlyList<SettingsOption> BuildRxSensitivityOptions(ReaderCapabilities? capabilities) =>
        capabilities?.RxSensitivities is { Count: > 0 } sensitivities
            ? sensitivities
                .Select(sensitivity => new SettingsOption(
                    sensitivity.ReceiveSensitivityDb,
                    $"Index {sensitivity.Index}: {sensitivity.ReceiveSensitivityDb.ToString(CultureInfo.InvariantCulture)} dBm"))
                .ToArray()
            : [];

    private static SettingsRange ResolveTariRange(
        ReaderCapabilities? capabilities,
        ushort modeIndex,
        ushort currentTari)
    {
        C1G2RfModeEntry? mode = capabilities?.RfModes?.FirstOrDefault(m => m.ModeIdentifier == modeIndex);
        return mode is null
            ? new SettingsRange(0, ushort.MaxValue)
            : new SettingsRange(
                Math.Min(mode.MinTariValue, currentTari),
                Math.Max(mode.MaxTariValue, currentTari));
    }
}
