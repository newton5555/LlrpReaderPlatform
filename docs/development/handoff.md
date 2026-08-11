# LlrpReaderPlatform 交接文档（Handoff）

> 状态：基础中间层与首个 WPF 消费者首版代码已完成；能力目录已接入运行时快照和设置布局，并按型号、固件和 SDK 能力画像限定 Impinj L4 能力；自动化测试 304 项全绿，真机已完成标准 Probe/Settings Query、WPF 设置/GPO/GPI 状态查询、Impinj debounce/FastID/Phase/Search/Low Duty/固定频率回写、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复和 FastID/Phase 扩展 TagReport，R420 Doppler 已按 SDK 能力隐藏；代码已补齐统一 `LifecycleChanged` 事件；平台通知订阅者异常不会中断生命周期收尾，TagReport/GPI/定时停止任务绑定来源 Session 与 InventoryRun，旧事件不会跨 Run 污染新数据；手动 Stop、GPI Stop、定时结束、连接 Faulted、ReaderException、设备主动关闭都会由平台事件驱动 WPF 收尾；GPI 启停触发器保存时，标准设置编译器会同步开启 `Configuration.Events.GpiEventEnabled`；GPI 平台事件保留 Reader 事件时间戳并记录端口/状态/触发匹配日志，便于 WPF 状态和真机记录对齐；设备列表刷新期间保持选中 Reader，避免取消在途设置查询的竞态；设备列表已提供 Faulted Reader 的重新连接/能力刷新入口；标准 Tag Access 按 Reader 能力声明降级，明确不支持的设备不会在服务或 Tag Memory 页显示为可用；标准 GPIO 端口数量来自 General Device Capabilities，明确无端口时 Tab1/Tab2 对应操作降级，部分 GPO 设备只启用实际端口；WPF 设置保存、Tag Memory 和寻卡启动按稳定 `PlatformErrorCode` 投影忙碌/不支持/设备错误等状态；Contracts 的 `PlatformOperationException` 让 Settings Query/GPI/GPO 在 ReaderBusy 时保留同一错误码，明确无 GPO 能力时返回 Unsupported；SQLite 只维护新平台数据，早期 schema 变化允许清空数据库重建；Settings Preset 以版本化语义 JSON 同时保存设置和 Inventory 字段，不考虑旧库导入；短连接断开失败时设置布局转为只读并要求重新激活，Reader 能力快照过期时设置页同步禁用编辑和保存；能力解析循环使用整数索引，避免 `ushort` 最大值回绕；Inventory 服务入口拒绝无效时长、重复天线和混合全部天线/指定天线参数；寻卡页已恢复旧 WPF 的 ELAPSED TIME 顶部布局、列头右键菜单列选择和 Peak RSSI 命名，同时保留平台 Tag List 附加列与原生 ProgressBar；添加数据源页加入 Host/Port 校验、发现记录归一化与 IPv6 端点展示，并在 Probe、发现、提交期间锁定端点及发现条目选择；离开页面会取消在途操作；其它 WPF 页面退出时会取消未完成的 Reader 操作和数据库操作；设备状态、GPI 事件和运行记录的 WPF Dispatcher 投影在页面销毁或应用 Dispatcher 关闭竞态下会静默收口；应用设置页会显示 JSONL TagLog 的默认目录；GPI 物理事件/触发、其它 Memory Bank 写入、多 Reader、断网/重启现场恢复及其它现场证据仍在验收。
> 生成日期：以提交时为准。
> 本文档供接手的开发者在短时间内了解项目现状、关键设计、已知边界与下一步。

2026-08-11 追加真机证据：使用 `win-x64` 发布包直接启动 WPF，设备列表显示 R420 已同步能力、LLRP 1.0.1、Impinj 身份和固件；寻卡页 Start 后实时收到真实 EPC，运行中显示 5～6 个唯一标签、约 269～300 tags/s；Stop 后最终回到 `Start`/已同步能力，正常关闭窗口后进程退出，验证发布运行时的 WPF Start→TagReport→Stop→DisposeAsync 路径。

2026-08-11 再追加启动恢复证据：使用同一最新 `win-x64` 发布包、现有新平台 SQLite 数据库启动 WPF，SDK 日志记录对 `192.168.41.134:5084` 建立 LLRP 会话、设备拒绝 1.1 后回落到 1.0.1，并完成能力/配置短连接；通过正常关闭窗口结束进程，退出码为 0，验证从新库恢复 Reader 到 `DisposeAsync` 的发布运行时路径。

2026-08-11 再追加发布包冒烟证据：重新发布后的 `artifacts/publish/win-x64/App.Wpf.exe` 启动后窗口保持响应，标题为 `LLRP Reader Studio`，正常关闭后无残留 WPF 进程；本轮同时将设置、GPI/GPO、Tag Memory、Tag List、运行记录、发现和主设备入口的未结构化异常统一投影为 `PlatformErrorCode` 文本，保留底层详情。

2026-08-11 再追加当前版本连接证据：最新发布包启动期间 SDK 日志再次记录与 `192.168.41.134:5084` 建立 LLRP TCP 会话并启动接收循环，窗口保持响应，正常关闭后退出码为 0；本次 GPIO 能力降级修改未影响真实 R420 的连接/释放路径。

2026-08-11 再追加退出释放核对：启动同一发布包后正常请求关闭，进程退出码为 0；退出后检查主机到 `192.168.41.134:5084` 的 TCP 状态，仅剩 2 条由系统持有的 `TIME_WAIT` 记录，没有活动连接，确认本次启动恢复/短连接清理未留下存活的 Reader TCP 会话。

2026-08-11 再追加 WPF Tab2 竞态修复：GPO 开关快速连续切换时，失败的旧短操作现在只会按该端口最后确认状态回滚，并受操作代数保护，不会覆盖后续用户意图；新增回归测试覆盖该场景。完整基线更新为 304 项通过（App.Wpf 114）。

2026-08-11 再追加 Tab2 状态投影修复：主窗口 Reader 状态刷新会重复投影同一 Reader 上下文，Diagnostics 现在仅在 Reader 或 GPIO 能力上下文发生变化时清空状态；普通连接状态刷新不会再清空 GPI 事件表或把已确认 GPO 状态重置为 OFF，新增回归测试覆盖同一上下文重复投影。

2026-08-11 再追加能力上下文竞态修复：设置页在 Reader 能力重新捕获后会重新计算可保存门禁；同一 Reader 的能力版本、陈旧状态或 GPIO/Tag Access 能力上下文变化会使在途设置查询和 Tag Memory 结果失效，避免旧能力结果覆盖当前设备上下文。新增 3 项 WPF 回归测试，完整基线为 304 项通过（App.Wpf 114）。

2026-08-11 再追加一次完整真机回归：使用当前 `win-x64` 发布包连接 R420，寻卡约 36 秒收到 7 个唯一 EPC、约 9655 次读取；手动 Stop 后按钮回到 `Start`，活动 TCP 连接为 0。Inventory Runs 随后加载 17 条记录，最新一条为 `Manual / 9655 reads / 7 unique`；设备设置 Tab1 显示 `Loaded from Reader` 且 Save 可用，Tab2 回读 4 路 GPI/4 路 GPO，GPI 均为低电平，GPO1 恢复 OFF。该证据确认发布 WPF 的真实 Start→TagReport→Stop→RunStore→Settings Query→Disconnect 链路继续稳定；GPI 物理触发、第二台 Reader、多 Reader、断网/重启和其它 Memory Bank 写入仍是现场剩余项。

2026-08-11 再追加生命周期收尾修复：直接从寻卡页启动时，扩展探测阶段若被取消，ReaderManager 现在会回收当前/候选 Session 并恢复为 `Disconnected`，避免停留在 `Faulted`/`Connecting` 或保留半开传输；WPF 收到停止生命周期事件后会继续排空已经进入有界 UI 队列的最后一批 TagObserved，再停止刷新计时器，避免手动 Stop 后最后一批真实标签不显示；新增 Services 取消清理回归，当前基线为 304 项通过。

2026-08-11 追加第二台标准 Reader 探测证据：`192.168.41.148:5084` TCP 可达；使用平台 `Force101` 策略执行 Probe→Add→Activate，实际协商 `Version101`，Model `57690:40`、Firmware `1.0.0.233`，激活后状态为 `Disconnected` 且能力非陈旧，Settings Query 生成 57 个可编辑语义项。该设备目前没有接天线，因此不执行 Inventory/TagAccess；补天线后再做标准 L2、TagAccess 以及与 R420 的多 Reader 并行验收。

2026-08-11 补充第二台 Reader 的 GPIO 状态证据：`192.168.41.148` 的能力快照未声明 GPI/GPO 数量，但真实短连接查询分别返回 4 个 GPI 和 4 个 GPO 端口；当前无天线，未执行盘存，也未把状态查询当作物理触发验收。

2026-08-11 补充第二台 Reader 的 Tab1 设置回写证据：在 `Force101` 短连接路径上将 `Report Every N Tags` 从 1 改为 2、真实回读为 2，再恢复为 1 并回读为 1。期间发现该 Reader 的当前 RF Mode 可能不在能力表，标准设置编译器现将设备当前值追加为兼容 Choice，避免修改无关设置时被 `InvalidSettings` 拒绝。

2026-08-11 追加双 Reader 短连接隔离证据：R420（Auto→`Version101`）与 `192.168.41.148`（Force101→`Version101`）并行完成 Activate、Settings Query 和 GPIO Query；R420 得到 69 个设置项并启用 Impinj，148 得到 57 个标准设置项，两台均返回 4 GPI/4 GPO、回到 `Disconnected`、能力非陈旧且无 `Faulted`，随后检查两台 TCP 活动连接均为 0。双 Reader 同时 Inventory 仍待 148 接天线。

2026-08-11 追加多 Reader 短操作回归：Services.Tests 新增两个 Reader 并行 GPIO 短租约测试，确认各自连接/断开计数独立、状态均回到 `Disconnected`；全量基线更新为 304 项通过（Services 158）。

2026-08-11 补充协议策略可见性：设备列表和设备设置页现在同时显示持久化的 LLRP 连接策略与最近一次实际协商版本；`Force101` Reader 在离线、重启恢复或尚未重新协商时仍能明确显示为 `Force LLRP 1.0.1`，避免把 `192.168.41.148` 误当成 Auto 设备；新增 3 项 WPF 回归测试，完整基线仍为 304 项通过（App.Wpf 114）。

2026-08-11 补充连接生命周期可见性：设备列表和设置页现在把短操作完成后的正常状态显示为“能力已同步，短连接已释放”，寻卡期间显示 LLRP 长连接已建立；这只增加 WPF 状态解释，不改变每个 Reader 单 Session/Gate 和寻卡长租约的架构。

## 1. 项目定位

LLRP Reader Platform 是一个**厂商无关的 LLRP 应用框架 + 首个 WPF 消费者**：

- 共享服务层（Contracts / Services / Infrastructure）可被多个 UI 消费者复用，不绑定 WPF；
- 底层复用 `LlrpSdk 1.2.0`（标准 LLRP 协议层），不重造协议；
- 厂商能力通过**可插拔扩展模块**接入（当前仅 Impinj 模块），为更多设备类型做适配准备；
- 旧仓库 `LlrpReaderStudio` 已冻结（`F:\Projects\LLRP\LlrpReaderStudio`），仅作行为/迁移参考，非本仓库依赖。

## 2. 当前基线（可复现）

```text
dotnet build LlrpReaderPlatform.slnx   # 0 警告 0 错误
dotnet test  LlrpReaderPlatform.slnx --no-build   # 304 项全绿
```

测试分布：Contracts.Tests 5、Services.Tests 158、Infrastructure.Tests 7、App.Wpf.Tests 114、Architecture.Tests 7、Extensions.Impinj.Tests 13。

WPF 发布：`dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj -c Release -r win-x64 --self-contained false -o artifacts/publish/win-x64`，然后运行 `artifacts/publish/win-x64/App.Wpf.exe`；发布目录已加入 `.gitignore`。

协议诊断：Services 将 SDK 实际协商的 LLRP 1.0.1/1.1 版本映射到 Contracts 的 `ReaderProbeResult`、`ReaderRuntimeSnapshot` 和 `ReaderCapabilityCapture`；设置页设备信息会显示该实际版本。尚未识别的未来协议版本保留为空，不会误报为已支持版本。

真机：`192.168.41.134:5084` 网络和 TCP 均可达；标准 Probe、标准 Settings Query、带 `UseImpinj()` 的二阶段会话以及有界 Inventory Start→Stop→Disconnect 均成功。WPF 真机验证中，Tab1 将 `Report Every N Tags` 从 1 改为 2 后保存成功并刷新回读为 2，随后恢复为 1；Tab2 GPO1 ON→OFF 回写成功并恢复 OFF；Impinj GPI1 debounce 20→250 保存成功、刷新回读为 250，随后恢复 20。真实标签补测中，新平台 ReaderManager 运行 10 秒收到 1533 条 TagObserved，聚合 8 个唯一 EPC，自动停止后状态为 Disconnected；使用 EPC `E201E24F3E0B0E1CFAAF8700` 读取 EPC/TID/User/Reserved 四个 Memory Bank 成功，TID 返回 `E201E24F3E0B0E1C00008600`、EPC 返回 `3000E201E24F3E0B0E1CFAAF`、User 返回 `0000`、Reserved 返回 `00000000`，User Bank 另完成 `0000`→`A55A`→`0000` 写入恢复。开启 FastID/Phase 后真实报告出现 `impinj.serializedTid`、`impinj.rfPhaseAngle` 和 `impinj.peakRssi`；以 `InventorySpec.Antennas=[1]` 覆盖天线时仍成功聚合 10 个标签。另一次 3 秒定时 Inventory 记录 `StopReason=Duration`、5 个唯一标签/12 次读取并生成 12 行 JSONL TagLog。自动化层另有高频 TagReport 背压、匹配 GPI Stop 触发器自动收尾和 GPI 会话事件投影测试，已验证服务 Channel、TagLog Channel、WPF 事件队列和展示聚合均有上限，停止排空和断开不会卡死；GPI 物理事件尚未在现场触发。

## 3. 仓库结构

```text
LlrpReaderPlatform.slnx
├── Solution Items/     AGENTS.md、Directory.*、global.json、README.md
├── docs/               规划、架构、兼容性、开发、决策（见第 8 节文档地图）
├── src/
│   ├── LlrpReaderPlatform.Contracts/           平台契约（UI/SDK/厂商无关）
│   ├── LlrpReaderPlatform.Services/            生命周期、设置、盘存、扩展模块抽象
│   ├── LlrpReaderPlatform.Infrastructure/      mDNS 发现（Zeroconf）等实现细节
│   ├── LlrpReaderPlatform.Extensions.Impinj/   Impinj 扩展模块
│   └── LlrpReaderPlatform.App.Wpf/             首个 WPF 消费者（MahApps.Metro + CommunityToolkit.Mvvm）
└── tests/
    ├── LlrpReaderPlatform.TestKit/             FakeSession / FakeProfileStore / FakeSessionFactory
    ├── LlrpReaderPlatform.Services.Tests/
    ├── LlrpReaderPlatform.Extensions.Impinj.Tests/
    ├── LlrpReaderPlatform.App.Wpf.Tests/
    ├── LlrpReaderPlatform.Infrastructure.Tests/       EF SQLite CRUD
    └── LlrpReaderPlatform.Architecture.Tests/  依赖方向与公开 API 边界
```

依赖方向（架构测试守护）：`UI consumer → Services → Contracts`，`Services → Infrastructure → 扩展模块`；Contracts 不引用 WPF/SDK/厂商，Services 不引用 Impinj 厂商包。

## 4. 已完成内容

### 4.1 已完成的基础框架（F1~F5 基础部分）

| 模块 | 说明 | 关键类型 |
|---|---|---|
| 生命周期 | 补偿式 Add（Probe→持久化→注册→可选激活，失败回滚）；单 Session + 异步 Gate 串行化；短连接激活；Enable 与连接状态分离；设备断连守卫 | `ReaderManager`、`IReaderManager`、`ReaderRuntimeSnapshot` |
| 能力驱动设置 | 真实 Query/Validate/Apply；标准 Session/Population/Report/RF Mode/Tari/Tx/Rx 映射；CapabilityRevision 保存前复核；稳定 `SettingsKeys` 位于 Contracts，`CompiledSettings` 留在 Services 内部 | `SettingsService`、`StandardSettingsCompiler`、`IReaderSettingsService` |
| 盘存 | Inventory 长租约；运行中短操作返回 `ReaderBusy`；TagReport 与 TagLog 均使用有界 Channel 和单消费者，聚合/日志写入不会按报告创建无界后台 Task | `IInventoryService`、`TagObservation` |
| 扩展模块 | `IReaderExtensionModule`（IsApplicable/ConfigureBuilder）；两阶段匹配（标准 Probe → Match → 带扩展会话） | `ReaderProbeInfo`、`ReaderBuilderContext`、`ImpinjReaderExtensionModule` |
| 能力解析 | 从 `ReaderCapabilities.MaxNumberOfAntennas` 生成天线列表填充 snapshot | `ReaderAntennaFactory` |
| TagAccess 映射 | 平台 TagRead/WriteRequest ↔ LlrpSdk ReadTag/WriteTagRequest（EPC TagSelection、word 大端）；服务边界在短连接前校验目标、密码、读字数和写入 word | `SdkTagAccessMapper` |

### 4.2 首个 WPF 消费者（对齐旧项目功能面）

布局对齐旧 `LlrpReaderStudio.Wpf`：MahApps `MetroWindow` + Light.Cobalt 主题 + Accent；左栏设备侧边栏（Brand 卡片 / SHOWCASES 导航 / DATA SOURCES 列表）+ 右侧 `ContentControl` 页面路由（DataTemplate）+ 底部状态栏 + busy 遮罩（WPF 原生 `ProgressBar`）。

页面：添加数据源（LLRP 版本 + mDNS 发现/选用/提交，提交后回显 Probe 协议/身份/固件和扩展匹配）、设备设置（外层 DockPanel、顶部设备信息/刷新/默认、底部保存/取消状态栏均对齐旧 `DataSourceSettingsView`；Tab1 旧项目双栏分组 + Tab2 四路 GPO/GPI 状态，EditorKind 动态编辑器）、寻卡（多 Reader 全局 Start/Stop + 实时队列 + 12 列表列，含旧项目 `#` 行号 + Unique/耗时/速率，并支持按秒自动停止，0 表示持续运行）、Tag 内存读/写、应用设置（Tag Logging）、关于。设备项 `ToggleSwitch` 双向绑定启用/禁用，点击项打开设置页。

## 5. 关键设计说明

- **退出释放闸门**：ReaderManager 在释放开始后拒绝新的 Add/Probe，持有注册表 Gate 完成既有 Reader 的 Stop/Drain/Disconnect/Dispose，再清空注册表和关闭 Channel，避免应用退出期间插入未释放的 Session。
- **输入边界**：Inventory 服务在已知能力下拒绝超范围天线；Tag Access 选择条件拒绝 LLRP `ushort BitLength` 溢出；Tab2 GPO 端口必须从 1 开始。上述校验不会改变未知设备的兼容性降级策略。

- **单 Session/Gate**：每个 Reader 一个 `SemaphoreSlim` 串行化所有操作；公开操作在取得该 Gate 前短暂经过 registry gate，避免 Remove 竞态下旧调用使用已释放 Session；不同 Reader 不被全局锁串行化。Settings、Tag Access、GPO 为短连接（用完即断），而 Inventory 从 Start 到 Stop 持有同一个完整 LLRP 长连接租约；冲突返回 `ReaderBusy` 而非隐式停止。
- **Add/Remove 原子性**：重复 Reader Guid 的检查、Profile 持久化和 Session 注册受 registry gate 保护；注册失败会删除本次新写入的 Profile，Remove 也持有同一 gate 直到旧 Profile 删除完成，避免同 Guid 的新 Add 被旧删除补偿误删。
- **寻卡生命周期**：Start 时建立唯一 Session、读取/应用 Inventory 配置并启动 ROSpec；运行期间持续接收该 Session 的 TagReport；Stop 时停止 ROSpec、断开 TCP 并释放租约。不得按标签、按报告或按 UI 刷新重新连接。
- **寻卡 UI 操作边界**：WPF Start/Stop/全局 Start/Stop 使用独立生命周期忙碌门闩和原生进度条，重复停止/切换会回到状态栏提示，不会向同一 Reader 重复发起短操作。
- **多 Reader 寻卡**：旧 WPF 的全局 Start/Stop 只选择启用 Reader；每台 Reader 由平台服务独立持有自己的 Session/Gate 和 Inventory 长租约，UI 按 EPC 合并展示并保留 Reader 名称，单台失败不抢占其它 Reader。
- **两阶段厂商匹配**：先标准连接读身份（`ReaderProbeInfo.ManufacturerId/ModelId`），再按模块 `IsApplicable` 匹配，注册会话时经 `ConfigureBuilder`（如 `UseImpinj()`）启用扩展；不把设备识别绑定单一厂商。真机 Probe 已确认 Impinj ManufacturerId 为 `0x651A`。
- **离线启动恢复**：启动时 Probe 失败的 Reader 仍保留标准会话；首次重新激活会重新 Probe，必要时替换为匹配厂商扩展的会话。故障、停用或 Inventory 启动失败会标记下一次激活/短操作必须重新解析扩展，适配同一地址更换设备；会话替换后，旧会话迟到事件会被忽略。
- **能力驱动 UI**：设置布局由 `EffectiveSettingsLayout` 生成，UI 只按 `EditorKind` 渲染并提交 `SettingsDraft`；无能力时显示“需要连接以获取能力”只读态。
- **mDNS 错误语义**：Zeroconf 发现的普通网络异常会记录后继续向 WPF 传播，由添加数据源页显示“发现失败”；只有取消按取消语义传播，不把网络故障误报为“未发现设备”。
- **页面上下文**：设备设置 Tab1/Tab2、寻卡和 Tag Memory 页分别由自身 VM 承载 Reader 信息/操作状态，WPF Views 不再反向读取 `Window.DataContext.SelectedReader`；窗口层只负责把当前 Reader 上下文投影给页面 VM。切换 Reader 会取消旧设置 Query/Defaults，并用上下文版本保护回读结果，旧 Reader 的慢响应不能覆盖当前页面。
- **Tab2 短操作**：GPO 开关和 GPI/GPO 刷新共用 Diagnostics VM 的单操作门闩；四路 GPO 开关保留旧 WPF 的快速连续切换交互，操作按输入顺序异步排队，刷新按钮在忙碌时禁用并显示原生 ProgressBar，完成后仍由 ReaderManager 的 Reader Session Gate 做最终串行化。
- **消费者异步操作**：Tag Memory、Tag Lists、Inventory Runs 和应用设置页也使用独立忙碌门闩与原生 ProgressBar，重复读写、保存、导入或刷新不会并发进入同一页面操作。
- **故障事件线程**：Reader 异常和设备主动断连事件只投递后台故障收敛任务，Stop/Drain/Disconnect 不在 SDK 消息泵回调线程执行；WPF 在真实 `Application` 环境通过 Dispatcher 投影状态，headless 测试环境不依赖 Dispatcher 泵帧。
- **事件线程**：服务在后台线程发布事件/聚合，UI 层自行切换线程（当前 WPF 用显式刷新，未自动 marshal）。
- **当前连接链路**：新增设备从 `AddDataSourceViewModel` 提交后执行 `Probe → 持久化 → 注册 → ActivateAsync`；激活阶段建立 TCP/LLRP 会话，读取身份、固件和最大天线能力，更新 `ReaderRuntimeSnapshot`，然后为短连接主动断开。`ReaderProbeResult` 和添加结果都会回显匹配的扩展 Id，快照的 `ActiveExtensionIds` 记录当前 Session 选择的模块，空集合表示标准 LLRP 路径；设备设置页顶部也显示该诊断信息。寻卡页如果先于设置页启动，Inventory 长连接在启动前也会刷新同一份身份、CapabilityRevision 和 FeatureCatalog。因而激活成功后的稳定状态可能是 `Disconnected`，判断是否已就绪应看 `IsStale == false`、`CapabilityRevision > 0` 以及身份/能力字段，而不是只看 `State == Connected`；协议/网络故障收敛会保留快照但标记 `IsStale=true` 并要求下一次激活/短操作重新执行标准 Probe 与扩展匹配，设置、Tag Memory 和 Tab2 短操作会在入口自恢复能力并等待重新激活；故障或断开不可靠的旧 Session 会先被回收并替换为干净 Session，恢复 Probe 失败则保持 Faulted 并等待下一次显式重试。设备列表 Enable 开关也执行同一套激活/停用流程；添加/激活失败通过 `ReaderAddResult`/`ReaderActivationResult.ErrorCode` 投影稳定的 ReaderBusy/设备错误语义，WPF 不解析错误文本；未结构化的 Probe、添加或激活异常统一归类为设备错误并保留详细信息。
- **WPF 启动链路**：组合根先注册 Services 的内存兜底，再由 `AddLlrpInfrastructure()` 覆盖为 SQLite Store，随后创建并显示 `MainWindow`；窗口 `Loaded` 再调用 `MainViewModel.InitializeAsync`，由 ViewModel 依次执行 Reader 启动恢复、应用设置加载和列表刷新。ViewModel 只接收 Contracts Store 接口，不创建 InMemory/SQLite 实现；各 SQLite Store 通过同一 `DbContextFactory` 维度的迁移闸门串行初始化 schema，实际读写仍使用独立 `DbContext`；初始化期间使用 WPF 原生 `ProgressBar`，异常保留在状态栏而不让窗口静默退出。
- **寻卡停止状态投影**：`InventoryViewModel` 以平台 `LifecycleChanged` 为唯一运行收尾来源；手动停止、GPI 触发、定时结束、设备断开和 Reader 异常都会更新按钮、计时器和 `Status`，主窗口底部状态栏同时显示该停止原因，真机验收无需通过按钮轮询推断生命周期。
- **WPF 日志链路**：组合根将应用/服务日志写入 `platform-*.log`，将 SDK/LLRP 协议日志写入独立的 `sdk-*.log`；两者均按天和 50 MB 滚动，最多保留 14 个文件，退出时随组合根释放。
- **当前设置链路**：打开设置页时若能力缓存过期，UI 先激活并同步能力，再由 `SettingsService.QueryAsync` 通过短连接读取 SDK `ReaderSettingsSnapshot`，生成能力驱动布局；保存时重新 Query、校验、编译 SDK `ReaderSettings`，由 ReaderManager 在短租约内 Validate/Apply 并持久化平台 JSON 快照。
- **设置校验边界**：`SettingsService` 会缓存最近一次实时 Query/Defaults 的完整布局供同步 `Validate` 使用，因此 Tab1 的 Filter/GPI/Report/扩展项不会绕过校验；标准或扩展编译阶段的格式/范围错误会返回可显示的 Apply 失败结果，不会直接冒泡为未处理异常。
- **取消语义**：Probe、Initialize、Activate、Inventory Start/Stop 在调用方取消时先完成必要的会话/运行上下文清理，再传播 `OperationCanceledException`；普通网络/SDK 异常仍按设备失败或 StopFailed 返回，不与取消混淆。
- **当前设置基线**：SDK 返回 managed ROSpec Inventory 时，编译器优先沿用该 Inventory，再合并 Tab1 草稿；旧布局中的固定控件和扩展能力项均随 `SettingsEntry.IsReadOnly` 同步禁用，不会出现“界面可改但保存被忽略”。
- **离线设置回退**：Reader 激活失败时仍进入设置页；设备 Query 成功会缓存完整 Tab1 语义布局，新的 SchemaVersion=1 结构化语义 Preset 以只读方式展示，无缓存或格式过旧时显示“能力未就绪”占位；`REFRESH SETTINGS`/`LOAD DEFAULTS` 会重试标准激活。SQLite 只维护新平台数据，旧库/旧扁平 JSON 不在兼容范围内，早期变更可清空数据库重建。
- **Reader 上下文投影**：寻卡页在非运行态切换 Reader 时重新读取当前 Reader 的标签，移除或取消选择时清空旧标签；Inventory Runs 查询会取消旧 Reader 的读取并按上下文版本丢弃晚到结果，避免页面显示跨 Reader 的历史数据；运行记录页订阅统一 `LifecycleChanged`，选中 Reader 的一次寻卡结束并完成落库后会自动刷新记录。

## 6. 已知待办与边界（诚实清单）

1. **真机 LLRP 功能验收**（最高优先）：`192.168.41.134` 上已完成真实 TagReport 聚合、EPC/TID/User/Reserved Memory Bank 读取、Tag Access 写入恢复、FastID/Phase 扩展 TagReport、Impinj Search/Low Duty/固定频率和 GPI 状态查询；继续完成 GPI 事件/触发、其它 Memory Bank 写入、多 Reader、故障恢复及其它现场证据，Settings Apply、寻卡生命周期和 GPI/GPO 已有 WPF 验证记录并回填设备矩阵；
2. **Impinj 扩展字段**：厂商 ID 已校准为 `0x651A`，带扩展 Builder 的连接和 R420 FastID/Phase 扩展 TagReport 已通过；Doppler 按固件/SDK 能力画像隐藏，不得下发；
3. **能力深度**：标准多天线 RF、Gen2 Filter、state-aware、GPI 启停和 Report 字段已生成布局并编译；频率表已支持能力驱动的集合编辑，更多扩展字段仍需设备矩阵验收；
4. **设置表单完整度**：标准 Gen2 Filter、每天线独立 RF、GPI 启停和 Report 字段，以及 Impinj Search/FastID/Phase/Doppler/Low Duty/Fixed Frequency 已由扩展贡献点接入；WPF Tab1/Tab2 已补齐旧项目的设备信息、取消编辑、每天线 RF 展开区、GPI 四行矩阵、Filter 1/2 双栏、四路 GPO 和 GPI 状态刷新表现；R420 的对应 Apply/回读已完成，剩余是其它设备能力差异和 GPI 触发现场验证；
5. **Inventory 生命周期闭环**：服务和 WPF 已完成单 Session 长租约、实时事件队列、Stop 断开、时长/速率/耗时投影、InventoryRun 和可选 JSONL Tag Logging；R420 真实 TagReport 和生命周期已验收，真实日志文件及其它设备仍需补测；
6. **日志与附属业务**：Tag Logging、Tag List、Inventory Run 存储边界和 TagList/Run 管理 UI 已补齐；Inventory Stop/断连/退出会等待队列及 TagLog 写入完成；R420 已完成一次真实 TagReport、定时 Inventory 和 TagLogging 记录，多设备现场记录仍待补测；
7. **Infrastructure 持久化**：EF Core SQLite 已完成 Profile、Settings JSON、AppSettings、TagList、InventoryRun、基础 Migration 和启动恢复；生产 WPF 组合根的 DI 回归会确认五类持久化契约解析到 SQLite Store，而不是 Services 的内存兜底；早期 schema 变化允许清空数据库重建，不把历史数据兼容作为交付门槛；

## 7. 接手后建议路径

1. 按 P8 在 `192.168.41.134` 继续完成 GPI 状态变化事件/触发、其它 Memory Bank 写入和故障恢复验收并回填矩阵；
2. 对旧 SDK 设置转换结果做真机 Query/Apply 回读复核，并继续增强少量厂商专用设置编辑器；
3. 接入至少一台额外标准 LLRP Reader，验证未知厂商标准路径、多 Reader 并行和故障恢复；
4. 每个阶段执行 `dotnet build LlrpReaderPlatform.slnx`、`dotnet test LlrpReaderPlatform.slnx --no-build` 并回填主计划和设备矩阵。

## 8. 文档地图

- 主计划：`docs/llrp-framework-vision.md`（F 阶段产品路线、P0～P8 分层执行顺序、最终交付工作包和进度回填）
- 架构：`docs/architecture/`（overview / reader-runtime / extensions-and-settings）
- 兼容性：`docs/compatibility/device-matrix.md`
- 开发：`docs/development/roadmap.md`、`docs/development/testing-strategy.md`、`docs/development/legacy-feature-matrix.md`、`docs/development/hardware-validation-runbook.md`
- 决策：`docs/decisions/`（ADR 0001~0005）
- 冻结仓库参考：`docs/legacy/README.md`

## 9. 交接注意事项

- 修改共享层（Contracts/Services）后必须跑全量测试；架构测试会拦截"契约泄漏 SDK/WPF"、"Services 引厂商包"类回归；
- 新增文档/移动文档时，同步更新 `docs/README.md`、根 `README.md` 与 `LlrpReaderPlatform.slnx`；
- 真机相关结论（厂商 ID、能力字段、扩展字段）必须来自实测，勿从 SDK 包名或接口推断；
- 本仓库当前阶段：P0～P7 首版代码已落地，P8 真机与多设备验收进行中；旧 WPF 主要功能入口和服务链路已迁移，剩余是硬件结论、少量专用编辑器和扩展设备覆盖。
- 本轮代码边界：Contracts 新增 Reader 端点归一化和 `PlatformOperationException`，程序化添加、SQLite、SDK 会话构造和 WPF IPv6 展示共享同一 Host 规则；同时保护 Start 返回与早到终止生命周期事件之间的状态竞态；Tag Logging 关闭时不会创建 Run 文件；全局寻卡部分失败状态按 Reader 名称和错误摘要定位；本轮针对 App.Wpf 的回归为 114 项通过，完整基线为 304 项，构建 0 警告 0 错误，格式校验通过。
- 本轮 WPF 补充：主设备页发现与添加数据源页发现共用归一化/去重逻辑，主设备页的非法端口会回退 5084，IPv6 会统一使用无方括号 Host 和带方括号的端点展示；设置 Tab1 的旧分组按实际语义行显隐，Tab2 的 GPO/GPI 区域按端口能力降级，不再显示空的伪控件；所有页面的设备、连接和持久化异常统一经过 `PlatformErrorCode` 投影，现场可区分设备错误与本地保存失败。
- 本轮 WPF 生命周期补充：Tag Memory 读写、设置加载和其它页面异步操作在页面销毁后会静默收口晚到的非取消异常，不再把已关闭页面的底层失败变成未观察异常；未销毁页面仍按原有状态文本显示失败。
- 本轮设备入口补充：添加数据源页和主设备页对 Probe、添加、激活的未结构化异常统一使用稳定设备错误分类，保留底层详细信息供现场诊断。
- 本轮页面导航补充：添加数据源、设备设置（含 Tab2 GPI/GPO）、Tag Memory、Tag Lists、运行记录和应用设置离开侧栏页面时取消各自短操作；Shell 发起的设置加载也受同一导航代际保护，旧结果不会把当前页切回设置；寻卡页不因导航停止，继续保持一次完整的 Start→Inventory→Stop→Disconnect 生命周期。设置 Apply 在预查询后和实际编译回调内复核 CapabilityRevision，变化时返回 StaleCapability，不把旧 Draft 下发到新能力上下文。
