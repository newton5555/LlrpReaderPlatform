# LlrpReaderPlatform 交接文档（Handoff）

 > 状态：基础中间层与首个 WPF 消费者首版代码已完成；能力目录已接入运行时快照和设置布局，并按型号、固件和 SDK 能力画像限定 Impinj L4 能力；自动化测试 201 项全绿，真机已完成标准 Probe/Settings Query、WPF 设置/GPO/GPI 状态查询、Impinj debounce/FastID/Phase/Search/Low Duty/固定频率回写、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复和 FastID/Phase 扩展 TagReport，R420 Doppler 已按 SDK 能力隐藏；代码已补齐统一 `LifecycleChanged` 事件，手动 Stop、GPI Stop、定时结束、连接 Faulted、ReaderException、设备主动关闭都会由平台事件驱动 WPF 收尾；GPI 启停触发器保存时，标准设置编译器会同步开启 `Configuration.Events.GpiEventEnabled`；设备列表已提供 Faulted Reader 的重新连接/能力刷新入口；标准 Tag Access 按 Reader 能力声明降级，明确不支持的设备不会在服务或 Tag Memory 页显示为可用；标准 GPIO 端口数量来自 General Device Capabilities，明确无端口时 Tab1/Tab2 对应操作降级，部分 GPO 设备只启用实际端口；SQLite 只维护新平台数据，早期 schema 变化允许清空数据库重建；GPI 物理事件/触发、其它 Memory Bank 写入、多 Reader、断网/重启现场恢复及其它现场证据仍在验收。
> 生成日期：以提交时为准。
> 本文档供接手的开发者在短时间内了解项目现状、关键设计、已知边界与下一步。

## 1. 项目定位

LLRP Reader Platform 是一个**厂商无关的 LLRP 应用框架 + 首个 WPF 消费者**：

- 共享服务层（Contracts / Services / Infrastructure）可被多个 UI 消费者复用，不绑定 WPF；
- 底层复用 `LlrpSdk 1.2.0`（标准 LLRP 协议层），不重造协议；
- 厂商能力通过**可插拔扩展模块**接入（当前仅 Impinj 模块），为更多设备类型做适配准备；
- 旧仓库 `LlrpReaderStudio` 已冻结（`F:\Projects\LLRP\LlrpReaderStudio`），仅作行为/迁移参考，非本仓库依赖。

## 2. 当前基线（可复现）

```text
dotnet build LlrpReaderPlatform.slnx   # 0 警告 0 错误
dotnet test  LlrpReaderPlatform.slnx --no-build   # 201 项全绿
```

测试分布：Contracts.Tests 4、Services.Tests 113、Infrastructure.Tests 5、App.Wpf.Tests 59、Architecture.Tests 7、Extensions.Impinj.Tests 13。

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
| 能力驱动设置 | 真实 Query/Validate/Apply；标准 Session/Population/Report/RF Mode/Tari/Tx/Rx 映射；CapabilityRevision 保存前复核；`CompiledSettings` 留在 Services 内部 | `SettingsService`、`StandardSettingsCompiler`、`IReaderSettingsService` |
| 盘存 | Inventory 长租约；运行中短操作返回 `ReaderBusy`；TagReport 与 TagLog 均使用有界 Channel 和单消费者，聚合/日志写入不会按报告创建无界后台 Task | `IInventoryService`、`TagObservation` |
| 扩展模块 | `IReaderExtensionModule`（IsApplicable/ConfigureBuilder）；两阶段匹配（标准 Probe → Match → 带扩展会话） | `ReaderProbeInfo`、`ReaderBuilderContext`、`ImpinjReaderExtensionModule` |
| 能力解析 | 从 `ReaderCapabilities.MaxNumberOfAntennas` 生成天线列表填充 snapshot | `ReaderAntennaFactory` |
| TagAccess 映射 | 平台 TagRead/WriteRequest ↔ LlrpSdk ReadTag/WriteTagRequest（EPC TagSelection、word 大端）；服务边界在短连接前校验目标、密码、读字数和写入 word | `SdkTagAccessMapper` |

### 4.2 首个 WPF 消费者（对齐旧项目功能面）

布局对齐旧 `LlrpReaderStudio.Wpf`：MahApps `MetroWindow` + Light.Cobalt 主题 + Accent；左栏设备侧边栏（Brand 卡片 / SHOWCASES 导航 / DATA SOURCES 列表）+ 右侧 `ContentControl` 页面路由（DataTemplate）+ 底部状态栏 + busy 遮罩（WPF 原生 `ProgressBar`）。

页面：添加数据源（LLRP 版本 + mDNS 发现/选用/提交，提交后回显 Probe 协议/身份/固件和扩展匹配）、设备设置（外层 DockPanel、顶部设备信息/刷新/默认、底部保存/取消状态栏均对齐旧 `DataSourceSettingsView`；Tab1 旧项目双栏分组 + Tab2 四路 GPO/GPI 状态，EditorKind 动态编辑器）、寻卡（多 Reader 全局 Start/Stop + 实时队列 + 12 列表列，含旧项目 `#` 行号 + Unique/耗时/速率）、Tag 内存读/写、应用设置（Tag Logging）、关于。设备项 `ToggleSwitch` 双向绑定启用/禁用，点击项打开设置页。

## 5. 关键设计说明

- **单 Session/Gate**：每个 Reader 一个 `SemaphoreSlim` 串行化所有操作；公开操作在取得该 Gate 前短暂经过 registry gate，避免 Remove 竞态下旧调用使用已释放 Session；不同 Reader 不被全局锁串行化。Settings、Tag Access、GPO 为短连接（用完即断），而 Inventory 从 Start 到 Stop 持有同一个完整 LLRP 长连接租约；冲突返回 `ReaderBusy` 而非隐式停止。
- **Add/Remove 原子性**：重复 Reader Guid 的检查、Profile 持久化和 Session 注册受 registry gate 保护；注册失败会删除本次新写入的 Profile，Remove 也持有同一 gate 直到旧 Profile 删除完成，避免同 Guid 的新 Add 被旧删除补偿误删。
- **寻卡生命周期**：Start 时建立唯一 Session、读取/应用 Inventory 配置并启动 ROSpec；运行期间持续接收该 Session 的 TagReport；Stop 时停止 ROSpec、断开 TCP 并释放租约。不得按标签、按报告或按 UI 刷新重新连接。
- **寻卡 UI 操作边界**：WPF Start/Stop/全局 Start/Stop 使用独立生命周期忙碌门闩和原生进度条，重复停止/切换会回到状态栏提示，不会向同一 Reader 重复发起短操作。
- **多 Reader 寻卡**：旧 WPF 的全局 Start/Stop 只选择启用 Reader；每台 Reader 由平台服务独立持有自己的 Session/Gate 和 Inventory 长租约，UI 按 EPC 合并展示并保留 Reader 名称，单台失败不抢占其它 Reader。
- **两阶段厂商匹配**：先标准连接读身份（`ReaderProbeInfo.ManufacturerId/ModelId`），再按模块 `IsApplicable` 匹配，注册会话时经 `ConfigureBuilder`（如 `UseImpinj()`）启用扩展；不把设备识别绑定单一厂商。真机 Probe 已确认 Impinj ManufacturerId 为 `0x651A`。
- **离线启动恢复**：启动时 Probe 失败的 Reader 仍保留标准会话；首次重新激活会重新 Probe，必要时替换为匹配厂商扩展的会话。会话替换后，旧会话迟到事件会被忽略。
- **能力驱动 UI**：设置布局由 `EffectiveSettingsLayout` 生成，UI 只按 `EditorKind` 渲染并提交 `SettingsDraft`；无能力时显示“需要连接以获取能力”只读态。
- **mDNS 错误语义**：Zeroconf 发现的普通网络异常会记录后继续向 WPF 传播，由添加数据源页显示“发现失败”；只有取消按取消语义传播，不把网络故障误报为“未发现设备”。
- **页面上下文**：设备设置 Tab1/Tab2 和 Tag Memory 页分别由自身 VM 承载 Reader 信息/操作状态，WPF Views 不再反向读取 `Window.DataContext.SelectedReader`；窗口层只负责把当前 Reader 上下文投影给页面 VM。
- **Tab2 短操作**：GPO 开关和 GPI/GPO 刷新共用 Diagnostics VM 的单操作门闩；四路 GPO 开关保留旧 WPF 的快速连续切换交互，操作按输入顺序异步排队，刷新按钮在忙碌时禁用并显示原生 ProgressBar，完成后仍由 ReaderManager 的 Reader Session Gate 做最终串行化。
- **消费者异步操作**：Tag Memory、Tag Lists、Inventory Runs 和应用设置页也使用独立忙碌门闩与原生 ProgressBar，重复读写、保存、导入或刷新不会并发进入同一页面操作。
- **故障事件线程**：Reader 异常和设备主动断连事件只投递后台故障收敛任务，Stop/Drain/Disconnect 不在 SDK 消息泵回调线程执行；WPF 在真实 `Application` 环境通过 Dispatcher 投影状态，headless 测试环境不依赖 Dispatcher 泵帧。
- **事件线程**：服务在后台线程发布事件/聚合，UI 层自行切换线程（当前 WPF 用显式刷新，未自动 marshal）。
- **当前连接链路**：新增设备从 `AddDataSourceViewModel` 提交后执行 `Probe → 持久化 → 注册 → ActivateAsync`；激活阶段建立 TCP/LLRP 会话，读取身份、固件和最大天线能力，更新 `ReaderRuntimeSnapshot`，然后为短连接主动断开。寻卡页如果先于设置页启动，Inventory 长连接在启动前也会刷新同一份身份、CapabilityRevision 和 FeatureCatalog。因而激活成功后的稳定状态可能是 `Disconnected`，判断是否已就绪应看 `IsStale == false`、`CapabilityRevision > 0` 以及身份/能力字段，而不是只看 `State == Connected`。设备列表 Enable 开关也执行同一套激活/停用流程。
- **WPF 启动链路**：组合根先创建并显示 `MainWindow`，窗口 `Loaded` 再调用 `MainViewModel.InitializeAsync`，由 ViewModel 依次执行 Reader 启动恢复、应用设置加载和列表刷新；初始化期间使用 WPF 原生 `ProgressBar`，异常保留在状态栏而不让窗口静默退出。
- **WPF 日志链路**：组合根将应用/服务日志写入 `platform-*.log`，将 SDK/LLRP 协议日志写入独立的 `sdk-*.log`；两者均按天和 50 MB 滚动，最多保留 14 个文件，退出时随组合根释放。
- **当前设置链路**：打开设置页时若能力缓存过期，UI 先激活并同步能力，再由 `SettingsService.QueryAsync` 通过短连接读取 SDK `ReaderSettingsSnapshot`，生成能力驱动布局；保存时重新 Query、校验、编译 SDK `ReaderSettings`，由 ReaderManager 在短租约内 Validate/Apply 并持久化平台 JSON 快照。
- **设置校验边界**：`SettingsService` 会缓存最近一次实时 Query/Defaults 的完整布局供同步 `Validate` 使用，因此 Tab1 的 Filter/GPI/Report/扩展项不会绕过校验；标准或扩展编译阶段的格式/范围错误会返回可显示的 Apply 失败结果，不会直接冒泡为未处理异常。
- **取消语义**：Probe、Initialize、Activate、Inventory Start/Stop 在调用方取消时先完成必要的会话/运行上下文清理，再传播 `OperationCanceledException`；普通网络/SDK 异常仍按设备失败或 StopFailed 返回，不与取消混淆。
- **当前设置基线**：SDK 返回 managed ROSpec Inventory 时，编译器优先沿用该 Inventory，再合并 Tab1 草稿；旧布局中的固定控件和扩展能力项均随 `SettingsEntry.IsReadOnly` 同步禁用，不会出现“界面可改但保存被忽略”。
- **离线设置回退**：Reader 激活失败时仍进入设置页；设备 Query 成功会缓存完整 Tab1 语义布局，已有 SchemaVersion=1 语义 Preset 以只读方式展示，无缓存时显示“能力未就绪”占位；`REFRESH SETTINGS`/`LOAD DEFAULTS` 会重试标准激活；旧扁平语义 JSON 仍兼容。

## 6. 已知待办与边界（诚实清单）

1. **真机 LLRP 功能验收**（最高优先）：`192.168.41.134` 上已完成真实 TagReport 聚合、EPC/TID/User/Reserved Memory Bank 读取、Tag Access 写入恢复、FastID/Phase 扩展 TagReport、Impinj Search/Low Duty/固定频率和 GPI 状态查询；继续完成 GPI 事件/触发、其它 Memory Bank 写入、多 Reader、故障恢复及其它现场证据，Settings Apply、寻卡生命周期和 GPI/GPO 已有 WPF 验证记录并回填设备矩阵；
2. **Impinj 扩展字段**：厂商 ID 已校准为 `0x651A`，带扩展 Builder 的连接和 R420 FastID/Phase 扩展 TagReport 已通过；Doppler 按固件/SDK 能力画像隐藏，不得下发；
3. **能力深度**：标准多天线 RF、Gen2 Filter、state-aware、GPI 启停和 Report 字段已生成布局并编译；频率表已支持能力驱动的集合编辑，更多扩展字段仍需设备矩阵验收；
4. **设置表单完整度**：标准 Gen2 Filter、每天线独立 RF、GPI 启停和 Report 字段，以及 Impinj Search/FastID/Phase/Doppler/Low Duty/Fixed Frequency 已由扩展贡献点接入；WPF Tab1/Tab2 已补齐旧项目的设备信息、取消编辑、每天线 RF 展开区、GPI 四行矩阵、Filter 1/2 双栏、四路 GPO 和 GPI 状态刷新表现；R420 的对应 Apply/回读已完成，剩余是其它设备能力差异和 GPI 触发现场验证；
5. **Inventory 生命周期闭环**：服务和 WPF 已完成单 Session 长租约、实时事件队列、Stop 断开、时长/速率/耗时投影、InventoryRun 和可选 JSONL Tag Logging；R420 真实 TagReport 和生命周期已验收，真实日志文件及其它设备仍需补测；
6. **日志与附属业务**：Tag Logging、Tag List、Inventory Run 存储边界和 TagList/Run 管理 UI 已补齐；Inventory Stop/断连/退出会等待队列及 TagLog 写入完成；R420 已完成一次真实 TagReport、定时 Inventory 和 TagLogging 记录，多设备现场记录仍待补测；
7. **Infrastructure 持久化**：EF Core SQLite 已完成 Profile、Settings JSON、AppSettings、TagList、InventoryRun、基础 Migration 和启动恢复；早期 schema 变化允许清空数据库重建，不把历史数据兼容作为交付门槛；

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
