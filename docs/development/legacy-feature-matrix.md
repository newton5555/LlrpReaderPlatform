# 旧 LlrpReaderStudio 功能迁移矩阵

本文是 WP0 的行为覆盖矩阵。旧仓库 `F:\Projects\LLRP\LlrpReaderStudio` 只作为冻结参考；新平台不引用旧项目运行时程序集。

状态含义：

- `Implemented`：新平台已有真实实现；
- `AutomatedTested`：有对应的服务、基础设施或 WPF 测试；
- `HardwareVerified`：在真实 Reader 上已完成对应能力验收；
- `PendingHardware`：代码链路已具备，但仍需现场设备执行并回填结果。

| 旧功能 | 新契约/服务 | WPF 消费者 | 自动化 | 真机状态 |
|---|---|---|---|---|
| Reader Profile、Enable/Disable、Remove | `IReaderManager`、`IReaderProfileStore` | Data Sources 左栏 | `ReaderManager`/WPF/SQLite | `HardwareVerified`（标准 Probe） |
| mDNS `_llrp._tcp` 发现 | `IReaderDiscoveryService` | Add Data Source | WPF discovery 测试 | `PendingHardware` |
| 标准 Probe、身份、能力、固件 | `IReaderManager`、`ReaderRuntimeSnapshot` | Data Sources/Settings | 生命周期测试 | `HardwareVerified`（192.168.41.134） |
| 标准 Settings Query/Default/Validate/Apply | `IReaderSettingsService`、`IReaderSettingsRuntime` | Reader Settings 动态编辑器 | Settings/Compiler 测试 | Query/Apply `HardwareVerified`（R420 Report Every 回写并回读） |
| 天线、Tx/Rx、RF Mode、Session、Population、Report | `StandardSettingsCompiler` | Reader Settings | Compiler 测试 | `PendingHardware` |
| Gen2 Filter、State-aware、GPI Start/Stop | `StandardSettingsCompiler` | Reader Settings | Compiler 测试 | `PendingHardware` |
| Impinj Search/FastID/Phase/Doppler/Low Duty/Frequency/GPI Debounce | `ImpinjSettingsContributor`、TagReport projection | Reader Settings vendor rows、Inventory 扩展字段 | Impinj contributor/投影测试 | GPI Debounce、FastID、Phase、Search、Low Duty、Frequency 及 FastID/Phase 扩展 TagReport `HardwareVerified`；当前 R420 Doppler 经 SDK 能力画像拒绝并隐藏 |
| Inventory Start→Report→Stop→Disconnect | `IInventoryService`、`ReaderManager` | Inventory | 生命周期、Busy、Report 测试 | `HardwareVerified`（R420 新平台真实 10 秒运行） |
| EPC/TID/PC/RSSI/天线/信道/时间聚合 | `TagObservation`、扩展 TID 投影 | Inventory DataGrid | Services/WPF 测试 | 标准字段 `HardwareVerified`（R420）；Impinj TID/其它扩展字段按字段验收 |
| Report 列选择 | `InventoryReportSpec` | Inventory 列开关 | Inventory 服务测试 | `PendingHardware` |
| Tag Memory Read/Write | `IInventoryService`、`SdkTagAccessMapper` | Tag Memory | Mapper/Busy/Write 测试 | R420 EPC/TID/User/Reserved Read、User Bank 写入/恢复 `HardwareVerified`；其它 Memory Bank 写入与 Inventory 冲突待补 |
| GPO、GPI 状态与事件 | `GpioCommand`、`GpiPortStatus`、`GpoPortStatus`、`GpioStatusSnapshot` | Reader Settings Tab2（GPO）；Tab1 GPI Configuration | Services/WPF 组合测试；匹配 GPI Stop 触发器自动收尾测试 | GPO1 ON/OFF、4 路 GPI 状态查询、Services 会话事件投影 `HardwareVerified`；GPI 物理事件/触发 `PendingHardware` |
| Tag Logging | `IInventoryTagLog` | App Settings | JSONL SQLite 测试 | R420 真实 3 秒 Inventory 生成 12 行 JSONL `HardwareVerified` |
| Tag Lists 与 EPC 匹配显示 | `ITagListStore` | Tag Lists、Inventory Tag List 列 | WPF/SQLite 测试 | 不需设备 |
| Inventory Runs 与统计 | `IInventoryRunStore` | Inventory Runs | Services/SQLite 测试 | R420 真实定时停止记录 `Duration`、5 个唯一标签/12 次读取 `HardwareVerified` |
| App Settings | `IAppSettingsStore` | Software Settings | SQLite/WPF 测试 | 不需设备 |
| 异步 DI Container Dispose | `IAsyncDisposable`、WPF App exit | App composition root | DI 测试 | 不需设备 |

当前自动化基线：`dotnet build` 0 警告 0 错误；`dotnet test --no-build` 201 项全绿（Contracts 4、Services 113、Infrastructure 5、Impinj 13、Architecture 7、App.Wpf 59）。Connection Faulted、ReaderException 事件投影、统一 Inventory `LifecycleChanged`、Faulted Reader 重新连接、匹配 GPI Stop 触发器、Inventory 收尾与重新 Start、Reader 明确不支持 Tag Access/无 GPIO 端口/部分 GPO 端口时的服务/UI 降级已有 Services/WPF 自动化覆盖。

标准 GPIO 能力解析同时覆盖 LLRP 1.0.1 与 1.1 的 `GeneralDeviceCapabilities` 参数；V1.1 端口数量和能力目录映射已有自动化回归，尚未将此代码证据提升为真实 1.1 设备验收结论。

真实设备仍须由现场按 [设备矩阵](../compatibility/device-matrix.md) 执行写入、TagReport、TagAccess、GPI/GPO 和扩展字段验收；代码测试不能替代这些硬件结论。
