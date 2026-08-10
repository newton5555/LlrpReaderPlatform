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
| 其他厂商扩展 | 以模块和设备测试为准 | 未验收前不声明 L4 | 待扩展 |

## 记录要求

每个设备至少记录：

- 厂商、型号、固件版本；
- LLRP 协议版本和连接策略；
- 身份、能力、Inventory、Settings、Tag Access、GPI/GPO 结果；
- 扩展模块和扩展字段结果；
- 已知限制、错误码、复现步骤和测试日期。

设备支持等级必须来自实际测试，不能仅由厂商名称、SDK 包或接口实现推导。

## 实施与验收状态（回填）

当前仓库已完成基础服务、标准 Probe、生命周期、标准设置 Query/Apply 服务链路、EF SQLite Profile/Preset/TagList/InventoryRun 基础持久化、扩展匹配和 WPF 实时事件投影。标准多天线/Gen2 Filter/GPI/Report、Tag Access 和 Impinj 设置贡献已接入；真实设备已完成 WPF Settings Apply、寻卡 Start→Stop、GPO 回写、GPI 状态查询、TagReport 聚合、EPC/TID/User/Reserved 读取、User Bank 写入恢复和 GPI 状态事件的软件链路验证；设备主动断连、一般 Connection Faulted 和 ReaderException 的连接收敛、统一 Inventory `LifecycleChanged`、匹配 GPI Stop 触发器的 Inventory 收尾、InventoryRun 收尾和重新 Start 已有自动化覆盖。标准 Tag Access 以设备能力声明为准，明确不支持的 Reader 不会被服务或 Tag Memory 页误报为可用；标准 GPI/GPO 端口数量从 General Device Capabilities 读取，明确为 0 的端口不再显示为可配置，部分 GPO 设备只启用实际存在的端口。自动化测试全绿（201 项：Contracts 4、Services 113、Infrastructure 5、Impinj 13、Architecture 7、App.Wpf 59），构建 0 警告 0 错误；自动化测试使用 `FakeSession`，真实设备结论单独记录如下。

能力适配器同时读取 LLRP 1.0.1 和 1.1 的标准 GPIO 参数；这只证明协议参数映射路径，不替代至少一台真实 LLRP 1.1 Reader 的连接和 Inventory 验收。

待实机验收设备：

| 设备 | 协议 | 目标等级 | 验收状态 |
|---|---|---|---|
| 真机（地址 192.168.41.134） | LLRP / Impinj（型号以 ModelId 记录） | L1～L3 已验证部分；L4 已验证部分 | 标准 Probe、标准 Settings Query、Impinj 扩展 Builder 连接以及有界 Inventory Start→Stop→Disconnect 已成功：ManufacturerId `0x651A`（25882）、ModelId `0x1E886A`（2001002）、固件 `6.4.1.240`、最大天线数 4；WPF Tab1 `Report Every N Tags` 1→2 保存并刷新回读成功，随后恢复 1；WPF Tab2 GPO1 ON→OFF 成功并恢复 OFF；新平台 GPI 4 路状态查询成功；Impinj GPI1 debounce 20→250、FastID/Phase、Search Mode、Low Duty、固定频率均 Apply/回读成功并恢复；新平台 ReaderManager 10 秒真实寻卡收到 1533 条 TagObserved、聚合 8 个唯一 EPC，另一次 FastID/Phase 寻卡聚合 6 个 EPC 并出现 `impinj.serializedTid`、`impinj.rfPhaseAngle`、`impinj.peakRssi`，以 `InventorySpec.Antennas=[1]` 覆盖天线时再次聚合 10 个标签；平台 Tag Access 使用 EPC `E201E24F3E0B0E1CFAAF8700` 读取 EPC/TID/User/Reserved 四个 Memory Bank 成功，其中 TID 返回 `E201E24F3E0B0E1C00008600`、EPC 返回 `3000E201E24F3E0B0E1CFAAF`、User 返回 `0000`、Reserved 返回 `00000000`，User Bank `0000`→`A55A`→`0000` 写入恢复成功；另一次 3 秒 Inventory 以 `StopReason=Duration` 完成 12 次读取并生成 12 行 JSONL TagLog；Doppler 按 SDK 能力画像隐藏；GPI 事件/触发、其它 Memory Bank 写入、多 Reader 和断网/重启现场恢复待验收 |
| 标准 LLRP 1.0.1 Reader | LLRP 1.0.1 | L1～L2 | 待接入 |

### 真机标准 Probe/Settings Query 与 WPF 验证记录（2026-08-09～2026-08-10）

完整逐项记录见 [Impinj R420 真机验收记录](impinj-r420-2026-08-10.md)。

- 地址：`192.168.41.134:5084`；ping 和 TCP 5084 均可达；
- 连接策略：平台 `Auto` 标准会话；只执行连接、读取身份/能力、断开；
- 身份：ManufacturerId `25882 (0x651A)`、ModelId `2001002 (0x1E886A)`、Firmware `6.4.1.240`；
- 标准能力：`MaxNumberOfAntennas = 4`；
- 二阶段连接：带 `UseImpinj()` 的扩展会话连接、读取身份/能力、断开均成功；
- 标准 Settings Query：成功读到 `AntennaIds = 1,2,3,4`、`Session = 1`、`TagPopulationEstimate = 31`、`ReportEveryNTags = 1`、`ModeIndex = 1000`、Tx/Rx index；
- 已执行：WPF Settings Apply 并重新 Query、GPO1 ON→OFF、5 秒 Inventory Start/Stop/Disconnect，以及一次新平台 ReaderManager 的 10 秒真实 Inventory；后者收到 1533 条 TagObserved，聚合出 8 个唯一 EPC，结束后状态为 Disconnected。使用真实 EPC `E201E24F3E0B0E1CFAAF8700`，通过平台 Tag Access 读取 EPC/TID/User/Reserved 四个 Memory Bank，并完成 User Bank 写入临时值后恢复原值。新平台 GPI 4 路状态查询、Impinj GPI debounce、FastID/Phase、Search/Low Duty/Fixed Frequency Apply/回读均成功，FastID/Phase 寻卡观察到 `serializedTid`、RF phase、peak RSSI 扩展字段，另一次定时寻卡生成 JSONL TagLog 并与 12 次读取统计一致，Doppler 未进入布局。未完成：GPI 状态变化事件/触发、其它 Memory Bank 写入、多 Reader 和故障恢复。

验收方式：在带 GUI 的应用会话运行 LlrpReaderPlatform.App.Wpf，添加数据源并执行
Probe/激活/盘存，核对设备矩阵记录要求。真实协议验收必须由用户在设备现场完成。
