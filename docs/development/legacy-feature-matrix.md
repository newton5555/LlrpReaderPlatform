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
| 标准 Probe、身份、能力、固件 | `IReaderManager`、`ReaderRuntimeSnapshot` | Data Sources/Settings | 生命周期测试 | `HardwareVerified`（Impinj R420） |
| 标准 Settings Query/Default/Validate/Apply | `IReaderSettingsService`、`IReaderSettingsRuntime` | Reader Settings 动态编辑器 | Settings/Compiler 测试 | Query/Apply `HardwareVerified`（R420 Report Every 回写并回读） |
| 天线、Tx/Rx、RF Mode、Session、Population、Report | `StandardSettingsCompiler` | Reader Settings | Compiler 测试 | `PendingHardware` |
| Gen2 Filter、State-aware、GPI Start/Stop | `StandardSettingsCompiler` | Reader Settings | Compiler 测试 | `PendingHardware` |
| Impinj Search/FastID/Phase/Doppler/Low Duty/Frequency/GPI Debounce | `ImpinjSettingsContributor`、TagReport projection | Reader Settings vendor rows、Inventory 扩展字段 | Impinj contributor/投影测试 | GPI Debounce、FastID、Phase、Search、Low Duty、Frequency 及 FastID/Phase 扩展 TagReport `HardwareVerified`；当前 R420 Doppler 经 SDK 能力画像拒绝并隐藏 |
| Inventory Start→Report→Stop→Disconnect | `IInventoryService`、`ReaderManager` | Inventory（生命周期原因显示在主窗口底部状态栏） | 生命周期、Busy、Report 测试 | `HardwareVerified`（R420 新平台真实 10 秒运行） |
| EPC/TID/PC/RSSI/天线/信道/时间聚合 | `TagObservation`、扩展 TID 投影 | Inventory DataGrid | Services/WPF 测试 | 标准字段 `HardwareVerified`（R420）；Impinj TID/其它扩展字段按字段验收 |
| Report 列选择 | `InventoryReportSpec` | DataGrid 列头右键菜单列开关（含 EPC、Peak RSSI） | Inventory 服务/WPF 测试 | 不需设备 |
| Tag Memory Read/Write | `IInventoryService`、`SdkTagAccessMapper` | Tag Memory | Mapper/Busy/Write 测试 | R420 EPC/TID/User/Reserved Read、User Bank 写入/恢复 `HardwareVerified`；其它 Memory Bank 写入与 Inventory 冲突待补 |
| GPO、GPI 状态与事件 | `GpioCommand`、`GpiPortStatus`、`GpoPortStatus`、`GpioStatusSnapshot` | Reader Settings Tab2（GPO）；Tab1 GPI Configuration | Services/WPF 组合测试；匹配 GPI Stop 触发器自动收尾测试 | GPO1 ON/OFF、4 路 GPI 状态查询、Services 会话事件投影 `HardwareVerified`；GPI 物理事件/触发 `PendingHardware` |
| 盘存数据记录 | `IInventorySnapshotStore`、`IInventoryTagLog` | App Settings | JSON/JSONL SQLite 测试 | 默认停止后最终快照；R420 真实 3 秒 Inventory 生成 12 行 JSONL `HardwareVerified` |
| Tag Lists 与 EPC 匹配显示 | `ITagListStore` | Tag Lists、Inventory Tag List 列 | WPF/SQLite 测试 | 不需设备 |
| Inventory Runs 与统计 | `IInventoryRunStore` | Inventory Runs | Services/SQLite 测试 | R420 真实定时停止记录 `Duration`、5 个唯一标签/12 次读取 `HardwareVerified` |
| App Settings | `IAppSettingsStore` | Software Settings | SQLite/WPF 测试 | 不需设备 |
| 异步 DI Container Dispose | `IAsyncDisposable`、WPF App exit | App composition root | DI 测试 | 不需设备 |

全局寻卡的部分失败状态会按 Reader 名称和底层错误摘要显示；一台 Reader 启动或停止异常时，不会把其它 Reader 的长连接租约误报为整体失败，便于多 Reader 现场验收逐台定位。

Tag Memory 页面保留平台层的 `TagMemoryBank` 枚举和通用四 Bank 读写契约，WPF 显示文本已对齐旧项目的 `EPC`、`TID`、`User`、`Reserved`；不会把旧项目类型带入共享层。

当前自动化验证：`dotnet build` 0 警告 0 错误；`dotnet test --no-build` 382 项全绿（Contracts 5、Services 192、Infrastructure 10、App.Wpf 133、Architecture 9、Extensions.Impinj 17、Extensions.Zebra 6、VirtualReader 10）。Connection Faulted、ReaderException 事件投影、统一 Inventory `LifecycleChanged`、Faulted Reader 重新连接、故障/取消 Session 回收与干净 Session 重建、取消后重新 Probe 恢复能力、匹配 GPI Stop 触发器、Inventory 收尾与重新 Start、Reader 明确不支持 Tag Access/无 GPIO 端口/部分 GPI/GPO 端口时的服务/UI 降级、无 GPI/GPO 能力的状态查询 Unsupported 语义、未知 GPIO 数量从成功状态查询回填运行时快照、短连接断开失败后的只读设置降级、能力解析上限回绕、Inventory 输入边界校验、WPF 页面退出取消在途操作、设置能力过期后的编辑门禁、设备列表刷新期间保持设置页选中 Reader、添加页 Host/Port 校验、发现端点归一化和 IPv6 展示、Probe/发现/提交互斥和发现条目输入门禁、应用设置默认目录和生产组合根解析 SQLite Store、盘存记录模式、Start 返回与早到生命周期停止事件的状态保护、Settings Query 在 ReaderBusy 时保留稳定错误码、Tag List 保存/删除后即时刷新 Inventory 行名称已有 Services/WPF 自动化覆盖。

标准 GPIO 能力解析同时覆盖 LLRP 1.0.1 与 1.1 的 `GeneralDeviceCapabilities` 参数；V1.1 端口数量和能力目录映射已有自动化回归，尚未将此代码证据提升为真实 1.1 设备验收结论。

本轮还覆盖了关闭期间 Session 注册保护、已知能力下的 Inventory 天线边界和 Tag Access 选择长度边界；这些自动化结果仍不替代现场设备验收。

本轮还补充了直接从寻卡页启动时扩展探测取消的 Session 清理，以及停止生命周期事件后 WPF 最终 TagObserved 队列排空；这些自动化结果仍不替代现场设备验收。

真实设备仍须由现场按 [设备矩阵](../compatibility/device-matrix.md) 执行写入、TagReport、TagAccess、GPI/GPO 和扩展字段验收；代码测试不能替代这些硬件结论。

本轮运行时边界补充：IPv6 Host 的方括号归一化已放入 Contracts，ReaderManager、SQLite Profile Store、SDK Session Factory 和 WPF 端点展示共用该规则；程序化添加路径与 UI 添加路径保持一致。

发现入口补充：发现服务、主设备页和添加数据源页共享 Contracts 的发现结果归一化与端点去重，主设备页不会再直接显示原始 mDNS 重复记录或非法端口。

设置页补充：旧 WPF Tab1 的固定分组仍由平台语义行投影，但当部分设备能力不提供某一组设置时，该分组会隐藏；Tab2 的 GPO 控制和 GPI/GPO 刷新入口按实际端口能力显示，避免把空控件当作设备故障。
