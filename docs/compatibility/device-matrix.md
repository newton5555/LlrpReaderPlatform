# 设备支持等级与兼容性矩阵

## 支持等级

| 等级 | 支持内容 |
|---|---|
| L1 | TCP、LLRP 握手、协议版本、身份、标准能力查询 |
| L2 | 标准 Inventory、EPC、RSSI、天线、信道、SeenCount、时间戳 |
| L3 | 标准设置、Gen2 Filter、Tag Access、GPI/GPO |
| L4 | 厂商扩展，例如 Impinj Search Mode、FastID、Phase、Doppler |

## 首版矩阵

| 设备 | 协议 | 目标等级 | 状态 |
|---|---|---|---|
| 已验证标准 Reader | LLRP 1.0.1 | L1～L2，L3 按能力 | 基线，迁移后回归 |
| Impinj R420 | LLRP 1.0.1/SDK 自动策略 | L1～L4 | 基线，迁移后回归 |
| 其他标准 Reader | 以实测协议版本为准 | L1～L2 最低 | 待设备接入 |

> **协议版本边界（2026-08-14）**：平台已接入 LLRP 2.0 连接策略（`Force20`）与协商版本显示，并对 1.1/2.0 提供标准参数门控基础设施（见 ADR-0011/0012）。由于当前无 LLRP 1.1 / 2.0 真机，这些版本的标准参数行与 2.0 专属参数全部标记为 `PendingHardware`，不因代码/SDK 能力表声明支持；当前实机验收基线仍为 LLRP 1.0.1（R420 与 192.168.41.148）。
| 其他厂商扩展 | 以模块和设备测试为准 | 未验收前不声明 L4 | 待扩展 |

## 记录要求

每个设备至少记录：

- 厂商、型号（ModelId）、固件版本；
- LLRP 协议版本和连接策略；
- 身份、能力、Inventory、Settings、Tag Access、GPI/GPO 结果；
- 扩展模块和扩展字段结果；
- 已知限制、错误码、复现步骤和测试日期。

**设备同一性判定**：IP 地址不是稳定标识，可能被 DHCP 或部署变更。判定两条记录是否同一设备，必须依据固件上报的**厂商 ManufacturerId + 型号 ModelId + 固件版本**（能取到 MAC/LUID 时一并记录），不能用 IP 或主机名代替。同一台设备更换 IP 后，应沿用既有验证记录并注明新地址，不因 IP 变化重置或提升兼容性结论。

设备支持等级必须来自实际测试，不能仅由厂商名称、SDK 包或接口实现推导。

## 实施与验收状态（回填）

当前现场端点记录：Impinj R420 的 Profile 当前为 `192.168.40.87:5084`；下方部分历史记录仍使用此前验收地址 `192.168.41.134:5084`，历史地址变化不自动提升或降低兼容性结论，需在新端点重新执行现场项后再更新结果。标准 1.0.1 Reader 仍为 `192.168.41.148:5084`。

> 当前软件基线（2026-08-21）：Release 构建 0 警告/0 错误，自动化测试 385 项全绿。该数字只代表平台代码回归，不替代下方各设备的现场证据；报文级 Virtual Device Manager UI 已完成首版，但不改变真实 Reader 的兼容性等级。

2026-08-11 复核 R420 的 WPF 设置页：设备列表在连接状态变化时重建期间保持 `SelectedReader`，不再取消在途设置查询；页面实际显示 `Loaded from Reader`、Save 可用和 62 个回读值，包含 Impinj 字段、4 个天线行和 4 个 GPI 行。物理 GPI 触发、多 Reader、断网/重启和其它 Memory Bank 写入仍按现场验收项保留。

当前仓库已完成基础服务、标准 Probe、生命周期、标准设置 Query/Apply 服务链路、EF SQLite Profile/Preset/TagList/InventoryRun 基础持久化、扩展匹配和 WPF 实时事件投影。标准多天线/Gen2 Filter/GPI/Report、Tag Access 和 Impinj 设置贡献已接入；真实设备已完成 WPF Settings Apply、寻卡 Start→Stop、GPO 回写、GPI 状态查询、TagReport 聚合、EPC/TID/User/Reserved 读取、User Bank 写入恢复和 GPI 状态事件的软件链路验证；设备主动断连、一般 Connection Faulted 和 ReaderException 的连接收敛、统一 Inventory `LifecycleChanged`、匹配 GPI Stop 触发器的 Inventory 收尾、InventoryRun 收尾和重新 Start 已有自动化覆盖。标准 Tag Access 以设备能力声明为准，明确不支持的 Reader 不会被服务或 Tag Memory 页误报为可用；标准 GPI/GPO 端口数量优先从 General Device Capabilities 读取，明确为 0 的端口不再显示为可配置，部分端口设备只启用实际存在的 GPO；若能力响应没有数量，成功的 GPI/GPO 状态查询会按返回端口补充当前运行时快照，但不把状态查询当作物理接线验收；无 GPI/GPO 能力的状态查询返回稳定 `Unsupported`，不污染 Reader Faulted 状态；短连接断开失败后的设置布局会转为只读，能力快照过期时 WPF 设置页也会关闭编辑/保存入口；能力解析循环使用整数索引，避免 `ushort` 最大值回绕；Inventory 服务入口拒绝无效时长、重复天线和混合全部天线/指定天线参数；Start 返回与早到生命周期停止事件之间的状态覆盖已有 WPF 回归；Tag List 保存/删除后，已显示的 Inventory 行会即时重新投影标签名称，不会重启 Reader 生命周期；Activate、Inventory Start 和短操作取消时会回收并重建可能处于半开状态的 Session，下一次操作重新执行扩展探测，取消后的下一次 Activate 已有能力恢复回归。自动化测试全绿（368 项：Contracts 5、Services 188、Infrastructure 10、App.Wpf 133、Architecture 9、Extensions.Impinj 17、Extensions.Zebra 6），构建 0 警告 0 错误；自动化测试使用 `FakeSession`，真实设备结论单独记录如下。

> 上一段的 368 项是阶段性记录；当前自动化基线为 385 项，分项见[测试策略](../development/testing-strategy.md)。

本轮代码回归还覆盖直接从寻卡页启动时扩展探测取消的 Session 清理，以及停止生命周期事件后 WPF 最终 TagObserved 队列排空；这两项属于软件生命周期/UI 收尾保证，不提升设备硬件支持等级。

能力适配器同时读取 LLRP 1.0.1 和 1.1 的标准 GPIO 参数；这只证明协议参数映射路径，不替代至少一台真实 LLRP 1.1 Reader 的连接和 Inventory 验收。

代码边界已覆盖退出期间 Session 注册保护、已知能力下的超范围天线拒绝、Tag Access 选择长度溢出拒绝和 GPO 端口 0 拦截；这些属于平台输入/生命周期保证，不提升任何设备的硬件支持等级。

待实机验收设备：

| 设备 | 协议 | 目标等级 | 验收状态 |
|---|---|---|---|
| 真机（地址 192.168.41.134） | LLRP / Impinj（型号以 ModelId 记录） | L1～L3 已验证部分；L4 已验证部分 | 标准 Probe、标准 Settings Query、Impinj 扩展 Builder 连接以及有界 Inventory Start→Stop→Disconnect 已成功：ManufacturerId `0x651A`（25882）、ModelId `0x1E886A`（2001002）、固件 `6.4.1.240`、最大天线数 4；WPF Tab1 `Report Every N Tags` 1→2 保存并刷新回读成功，随后恢复 1；WPF Tab2 GPO1 ON→OFF 成功并恢复 OFF；新平台 GPI 4 路状态查询成功；Impinj GPI1 debounce 20→250、FastID/Phase、Search Mode、Low Duty、固定频率均 Apply/回读成功并恢复；新平台 ReaderManager 10 秒真实寻卡收到 1533 条 TagObserved、聚合 8 个唯一 EPC，另一次 FastID/Phase 寻卡聚合 6 个 EPC 并出现 `impinj.serializedTid`、`impinj.rfPhaseAngle`、`impinj.peakRssi`，以 `InventorySpec.Antennas=[1]` 覆盖天线时再次聚合 10 个标签；平台 Tag Access 使用 EPC `E201E24F3E0B0E1CFAAF8700` 读取 EPC/TID/User/Reserved 四个 Memory Bank 成功，其中 TID 返回 `E201E24F3E0B0E1C00008600`、EPC 返回 `3000E201E24F3E0B0E1CFAAF`、User 返回 `0000`、Reserved 返回 `00000000`，User Bank `0000`→`A55A`→`0000` 写入恢复成功；另一次 3 秒 Inventory 以 `StopReason=Duration` 完成 12 次读取并生成 12 行 JSONL TagLog；Doppler 按 SDK 能力画像隐藏；GPI 事件/触发、其它 Memory Bank 写入、多 Reader 和断网/重启现场恢复待验收 |
| 真机（地址 192.168.40.87；与下方 `.134` 同 identity） | LLRP 1.0.1 / Impinj | R420：L1~L4 | 该地址是此前 `192.168.41.134` 记录中**同一台 R420**（判定依据 ManufacturerId `0x651A`=25882、ModelId `2001002`、固件 `6.4.1.240`，IP 已变更）。2026-08 真机探针确认：连接后以 `ImpinjInventoryReportOptions{ IncludeRfPhaseAngle=true, IncludeSerializedTid=true }` 启动盘存，真实标签报告每条均含 `impinj.rfPhaseAngle` 与 `impinj.serializedTid`（30 条报告全部命中）；对照组（不请求 phase、仅标准字段）25 条真实报告均无 `impinj.rfPhaseAngle`/TID、仅含标准 RSSI/EPC，证明寻卡相位联动（R2/R3）及能力门控在真实 R420 生效；不因此 IP 变化重置旧验证结论。未做 GPI 物理触发/多 Reader 同时寻卡等现场剩余项 |
| 真机（地址 192.168.40.88） | LLRP 1.0.1 / Zebra（实验） | Zebra Experimental，L4 未声明 | 2026-08 真机探针确认：Zebra FX9600（Mfr `161`、Model `96008`、固件 `3.32.37.0`，即平台 `VerifiedFx9600Firmware` 固件基线）TCP 5084 可达并完成标准连接与身份读取。未执行盘存/设置回写；按规划 Zebra 扩展为实验性，未真机画像标定前不提升 L4 支持等级。 |
| 真机（地址 192.168.41.148） | LLRP 1.0.1（强制） | L1 已验证；L2/L3 部分验证 | TCP 5084 可达；平台强制 `Force101` 的 Probe、Add、Activate 均成功，协商版本为 `Version101`，Model `57690:40`、Firmware `1.0.0.233`；激活后状态 `Disconnected`、能力非陈旧、4 个逻辑天线端口、GPI/GPO 能力为空；Settings Query 成功生成 57 个可编辑语义项；Tab1 语义设置 `Report Every N Tags` 已真实执行 `1→2→1` Apply/回读并恢复原值，且当前 RF Mode 不在能力表时已由兼容选项保留，避免无关设置被误拒。设备当前未接天线，因此未执行 Inventory/TagAccess，不把无标签结果当作失败或通过 |

### 真机标准 Probe/Settings Query 与 WPF 验证记录（2026-08-09～2026-08-11）

完整逐项记录见 [Impinj R420 真机验收记录](impinj-r420-2026-08-10.md)。

- 地址：`192.168.41.134:5084`；ping 和 TCP 5084 均可达；
- 连接策略：平台 `Auto` 标准会话；只执行连接、读取身份/能力、断开；
- 身份：ManufacturerId `25882 (0x651A)`、ModelId `2001002 (0x1E886A)`、Firmware `6.4.1.240`；
- 标准能力：`MaxNumberOfAntennas = 4`；
- 二阶段连接：带 `UseImpinj()` 的扩展会话连接、读取身份/能力、断开均成功；
- 标准 Settings Query：成功读到 `AntennaIds = 1,2,3,4`、`Session = 1`、`TagPopulationEstimate = 31`、`ReportEveryNTags = 1`、`ModeIndex = 1000`、Tx/Rx index；
- 已执行：WPF Settings Apply 并重新 Query、GPO1 ON→OFF、5 秒 Inventory Start/Stop/Disconnect，以及一次新平台 ReaderManager 的 10 秒真实 Inventory；后者收到 1533 条 TagObserved，聚合出 8 个唯一 EPC，结束后状态为 Disconnected。使用真实 EPC `E201E24F3E0B0E1CFAAF8700`，通过平台 Tag Access 读取 EPC/TID/User/Reserved 四个 Memory Bank，并完成 User Bank 写入临时值后恢复原值。新平台 GPI 4 路状态查询、Impinj GPI debounce、FastID/Phase、Search/Low Duty/Fixed Frequency Apply/回读均成功，FastID/Phase 寻卡观察到 `serializedTid`、RF phase、peak RSSI 扩展字段，另一次定时寻卡生成 JSONL TagLog 并与 12 次读取统计一致，Doppler 未进入布局。未完成：GPI 状态变化事件/触发、其它 Memory Bank 写入、多 Reader 和故障恢复。
- 2026-08-11 追加发布包 WPF 验收：同一 R420 在真实寻卡页显示 5～6 个唯一 EPC、约 269～300 tags/s，手动 Stop 最终回到 Start，正常关闭窗口后进程退出；随后检查到 R420 的活动 TCP 连接数为 0，仅剩系统持有的 `TIME_WAIT` 记录。该项证明 WPF 发布运行时的连接、报告投影、停止和异步释放闭环，不替代现场 GPI 物理触发、多 Reader、断网/重启和其它 Memory Bank 写入验收。
- 2026-08-11 追加同一发布包的完整回归：寻卡运行约 36 秒收到 7 个唯一 EPC、约 9655 次读取，手动 Stop 后回到 Start，活动 TCP 连接数为 0；Inventory Runs 加载 17 条记录，最新记录为 `Manual / 9655 reads / 7 unique`；设备设置 Tab1 保持 `Loaded from Reader`/Save 可用，Tab2 回读 4 路 GPI、4 路 GPO，GPI 均为低电平且 GPO1 恢复 OFF。该项补强 WPF 的真实 Start→Report→Stop→RunStore→Settings Query→Disconnect 闭环，不替代 GPI 物理触发、多 Reader、断网/重启和其它 Memory Bank 写入验收。
- 2026-08-11 追加第二台标准 Reader 现场探测：`192.168.41.148:5084` TCP 可达；不使用 Auto，明确以 `Force101` 执行 Probe→Add→Activate，三步均成功并实际协商 `Version101`，身份为 Model `57690:40`、Firmware `1.0.0.233`；激活后正常回到 `Disconnected` 且能力非陈旧，Settings Query 成功生成 57 个可编辑项。该设备当前未接天线，只完成 L1 和设置链路的 L3 部分，Inventory、TagReport、TagAccess 及其与 R420 的多 Reader 并行仍待补天线后现场验收。
- 同一第二台 Reader 的 GPIO 状态短操作随后也完成真实验证：虽然能力快照未声明 GPI/GPO 数量（均为 null，保留未知能力回退），`GetGpiStatusAsync` 与 `GetGpoStatusAsync` 均成功返回 4 个端口；这不等价于已完成物理 GPI 事件/触发验收。
- 同一第二台 Reader 的设置短操作随后完成真实验证：使用 `Force101` 查询到 57 个语义项，将 `Report Every N Tags` 从 1 改为 2、回读为 2，再恢复为 1 并回读为 1；设备返回的 RF Mode 即使未出现在能力表，也会被平台作为当前值保留在 Choice 选项中。
- 2026-08-11 追加双 Reader 短连接隔离验证：R420（Auto，实际 `Version101`）与 `192.168.41.148`（Force101，实际 `Version101`）并行完成 Add→Activate→Settings Query→GPIO Query；R420 返回 69 个设置项、Impinj 扩展、4 GPI/4 GPO，148 返回 57 个设置项、标准路径、4 GPI/4 GPO，两台均回到 `Disconnected`、能力非陈旧且无 `Faulted`；两条 TCP 活动连接均为 0。该证据只覆盖短操作隔离，不替代双 Reader 同时 Inventory。

端点兼容性：Contracts 统一 IPv6 方括号 Host 的归一化；程序化添加、SQLite Profile、SDK Session Factory 和 WPF 展示共用同一规则。该项属于代码边界验证，不替代真实设备连接验收。

验收方式：在带 GUI 的应用会话运行 LlrpReaderPlatform.App.Wpf，添加数据源并执行
Probe/激活/盘存，核对设备矩阵记录要求。真实协议验收必须由用户在设备现场完成。
