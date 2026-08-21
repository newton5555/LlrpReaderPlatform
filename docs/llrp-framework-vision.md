# LlrpReaderPlatform 应用框架与首个 WPF 消费者开发计划

> 状态：持续维护计划（2026-08-21，真实 Reader 客户端、报文级 Virtual Device Manager UI、`LlrpReaderManager` 跨平台 Blazor 消费者和 Linux GTK4 Head 首版已落地，Linux Head 决策见 [ADR-0019](decisions/ADR-0019-maui-linux-gtk4-head.md)；平台级预设式 Data Source 作为长期规划保留）
> 基线仓库：`LlrpReaderStudio` 将冻结，不作为新项目的 ProjectReference 或运行时依赖；仅作为已验证行为、迁移经验和测试样例的参考。
> 目标：在新仓库中建设可被多个 UI 消费者复用的 LLRP 应用框架。主要交付对象是新的真实 Reader 客户端 `LlrpReaderPlatform.App.Wpf`；`LlrpReaderManager` 是共享服务的 MAUI Blazor 跨平台消费者，另有独立的 `LlrpVirtualDevice.App.Wpf` 报文级虚拟设备管理 UI。
> 当前验证基线：现有项目已验证标准 LLRP 1.0.1 设备和 Impinj R420；新项目以此为回归基线，逐步扩展到更多 LLRP 设备和厂商能力。自动化测试当前为 385 项全绿（含 Virtual Reader 场景与生命周期测试）；Windows x64 交付采用 NuGet SDK、自包含单文件 `LlrpReaderPlatform.exe`。

## 0. 目标与定位

- 新建**厂商无关的 LLRP 应用服务层（独立类库）**，作为正式产品长期维护；
- 新建 **WPF UI 应用**（`App.Wpf`），作为第一个消费者；未来其他 UI 框架复用相同的 Contracts/Services/Infrastructure；
- 新建 **MAUI Blazor UI 应用**（`LlrpReaderManager`），验证共享服务在桌面和手持屏幕上的响应式消费方式；
- 新建 **Linux GTK4 Head**（`LlrpReaderManager.Linux`），复用 Blazor 页面并通过 Ubuntu CI/Release 验证 Linux 桌面交付；
- 提供独立的 **虚拟设备管理 WPF UI**（`LlrpVirtualDevice.App.Wpf`），作为 SDK TCP/LLRP Virtual Device 的消费者，不把虚拟设备管理逻辑下沉到真实 Reader 客户端服务层；
- 依赖底层 `LlrpSdk`（标准 LLRP 核心 + 厂商扩展架构），**不重造协议层**；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过扩展模块接入；
- 服务层、Contracts 和 Infrastructure 不引用 WPF 或其他具体 UI 框架；WPF 只是第一个 UI 适配层，未来其他 UI 框架通过相同的服务契约接入。

## 1. 关键前提（已核实）

| 前提 | 事实 |
|---|---|
| 底层 SDK | `LlrpSdk` 与 `LlrpSdk.Extensions.Impinj` 提供标准 LLRP API 和 Impinj 扩展；相邻 `LLRPCSharp` 仓库用于可选源码联调 |
| SDK 引用方式 | 默认使用中央版本管理的 NuGet 包；设置 `UseLocalLlrpSdk=true` 时通过 `LlrpSdkSourceRoot` 切换为本地 `ProjectReference`，不通过长期分支切换 |
| 标准 LLRP 支持 | 已实测标准 LLRP 1.0.1；Auto/Force101/Force11 仅表示连接策略代码路径，Force11 仍需真实 1.1 设备验收 |
| Impinj 支持 | 已实测 Impinj R420；R700、Speedway 或其他型号不能仅凭 SDK 支持声明视为已验收 |
| 冻结项目角色 | 现有实现是**行为和经验基线**。可选择性迁移生命周期、防卡死、状态隔离等实现，但新仓库不引用旧项目，也不把旧项目的 Impinj 耦合带入新服务层 |

### 1.1 支持等级与设备矩阵

“支持更多 LLRP 设备”按能力等级声明，不把“能够连接”解释为支持该厂商全部功能：

| 等级 | 支持范围 | 首版要求 |
|---|---|---|
| L1 | TCP、LLRP 握手、协议版本、身份、标准能力查询 | 未知标准 Reader 必须支持 |
| L2 | 标准 Inventory、EPC、RSSI、天线、信道、SeenCount、时间戳 | 未知标准 Reader 必须支持 |
| L3 | 标准设置、Gen2 Filter、Tag Access、GPI/GPO | 按设备声明能力逐项启用和验收 |
| L4 | Impinj Search Mode、FastID、Phase、Doppler 等厂商扩展 | 仅在对应扩展模块和实机验收通过后声明 |

首版设备矩阵：

| 设备类别 | 目标等级 | 说明 |
|---|---|---|
| 已验证标准 LLRP 1.0.1 设备 | L1～L2 必须，L3 按能力 | 标准路径回归基线 |
| Impinj R420 | L1～L4 | Impinj 扩展首个实机回归基线 |
| 其他标准 LLRP Reader | L1～L2 最低目标，L3 按能力 | 不能从协议合规推导全部设置均可用 |
| 其他厂商或型号扩展 | 未验收前不声明 L4 | 必须增加模块测试和实机记录 |

## 2. 项目结构（一个解决方案、多个职责分离项目）

```
LlrpReaderPlatform.slnx（新仓库）
├── src/                                  ★ 产品项目与辅助工具
│   ├── LlrpReaderPlatform.Contracts/     UI 无关的公开模型、状态和设置契约
│   ├── LlrpReaderPlatform.Services/      生命周期、能力、设置和盘存编排
│   │   ├── Lifecycle/                    Reader 注册 / 激活 / 短连接 / Enable 语义
│   │   ├── Settings/                     能力驱动设置模型和协议编译
│   │   ├── Inventory/                    Inventory / TagReport / Tag Access 协调
│   │   ├── Capabilities/                 ReaderCapabilities 运行时缓存
│   │   ├── Modules/                      IReaderExtensionModule 抽象和模块注册
│   │   └── Persistence/                  持久化契约，不放具体 SQLite 实现
│   ├── LlrpReaderPlatform.Infrastructure/ 持久化、发现和日志实现
│   ├── LlrpReaderPlatform.Extensions.Impinj/ Impinj 扩展模块
│   ├── LlrpReaderPlatform.Extensions.Zebra/  Zebra 扩展模块
│   ├── LlrpReaderPlatform.VirtualReader/ 开发/验收用进程内 Reader 替身
│   ├── LlrpReaderPlatform.App.Wpf/       主要交付：真实 Reader 客户端 WPF 消费者
│       ├── Views/                        页面视图（纯 UI）
│       ├── ViewModels/                   页面状态 / 命令（只消费服务层）
│       ├── Messages/                     ViewModel 间消息
│       ├── Converters/                   值转换
│       └── Assets/                       图标等
│   ├── LlrpReaderManager/                MAUI Blazor Windows/Android/Mac Catalyst Reader 管理消费者
│   ├── LlrpReaderManager.Linux/          Linux GTK4 Reader 管理 Head，共享同一套 Blazor 页面
│       ├── Components/Pages/             Readers、Settings、Inventory、Tag Access、Runs、TOI、GPI/GPO
│       ├── State/                         UI 状态投影，不持有 SDK Session
│       └── VirtualDevices/                SDK 报文虚拟设备挂件与 Reader 注册交接
│   └── LlrpVirtualDevice.App.Wpf/        报文级 TCP 虚拟设备管理 UI（辅助工具）
└── tests/                                ★ 测试、测试支撑和人工硬件验收项目
    ├── LlrpReaderPlatform.Contracts.Tests/
    ├── LlrpReaderPlatform.TestKit/       可控 Session/Reader 测试替身
    ├── LlrpReaderPlatform.Services.Tests/
    ├── LlrpReaderPlatform.Infrastructure.Tests/
    ├── LlrpReaderPlatform.Extensions.Impinj.Tests/
    ├── LlrpReaderPlatform.Extensions.Zebra.Tests/
    ├── LlrpReaderPlatform.VirtualReader.Tests/
    ├── LlrpReaderPlatform.App.Wpf.Tests/
    ├── LlrpReaderPlatform.Architecture.Tests/
    └── LlrpReaderPlatform.Hardware.Tests/ 硬件验收 CLI（非自动化测试项目）
```

产品项目统一放在顶层 `src/`，测试项目、测试支撑库和硬件验收工具统一放在顶层
`tests/`；测试工程不得嵌套在对应产品项目目录中。`TestKit` 虽不是 xUnit 项目，仍属于
测试专用支撑代码，不进入产品发布依赖。

其中 `LlrpReaderPlatform.App.Wpf` 是本项目面向用户的主要客户端，复用平台 Services、Contracts、
Infrastructure 和厂商扩展；`LlrpVirtualDevice.App.Wpf` 是独立的报文级虚拟设备管理 UI，直接消费
SDK 的 Virtual Device Hosting 边界，管理 TCP 虚拟 Reader，不改变主客户端的 Reader 生命周期架构。
`LlrpReaderManager` 同样复用平台 Services、Contracts、Infrastructure 和厂商扩展；它是 MAUI Blazor
Hybrid 消费者，Reader 页面启动 SDK Virtual Device Host 后，把 loopback TCP endpoint 通过正常的
`IReaderManager.AddAsync` 注册为普通 Reader，虚拟设备不创建第二套 Reader 生命周期。

**设计铁律**：
- Contracts 是 UI 与服务之间的稳定边界，**不暴露任何 `LlrpSdk`、`LlrpNet.Protocol`、WPF 控件、Dispatcher 或 ViewModel 类型**；
- Services 只依赖 Contracts 和 LlrpSdk（默认 NuGet、可选本地项目，不依赖 UI 框架）；服务层本身不引用 `UseWPF`；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过注册 `IReaderExtensionModule` 加载，避免冻结项目中 Core 直接依赖 Impinj 类型的问题；
- Infrastructure 负责 Profile、Snapshot、Preset、发现和日志等外部资源；Services 只依赖接口；
- 每个 UI 应用只负责展示与交互，设备生命周期/能力聚合/设置编译全部在共享服务层；**ViewModel 不直接碰 SDK、数据库或具体 Store 实现**，也不使用 Service Locator 或直接 new Service/ViewModel。应用级设置、Tag List、Inventory Run 等持久化契约由组合根注入。
- 共享 DI 注册由 `AddLlrpReaderPlatform()`、`AddLlrpInfrastructure()`、`AddImpinjExtension()` 和
  `AddZebraExtension()` 等扩展方法提供；各 UI 只在自己的组合根选择并注册模块。

## 3. 模块职责与核心 API 草案

### 3.1 Lifecycle —— Reader 生命周期（服务层）

从冻结项目的 `ReaderFleetService` 提炼并厂商无关化：

```csharp
public interface IReaderManager
{
    // 对外暴露原子结果语义；内部通过补偿完成 probe -> 持久化 -> 注册 -> 可选激活
    public Task<ReaderAddResult> AddAsync(ReaderProfile profile, bool enableAfterAdding, CancellationToken ct);
    public Task<ReaderProbeResult> ProbeAsync(ReaderProfile profile, CancellationToken ct);
    public Task RemoveAsync(Guid readerId, CancellationToken ct);
    public Task SetEnabledAsync(Guid readerId, bool enabled, CancellationToken ct);

    // 激活（短连接：连接→读身份/能力/配置→写缓存→断开）
    public Task<ReaderActivationResult> ActivateAsync(Guid readerId, CancellationToken ct);
    public Task DeactivateAsync(Guid readerId, CancellationToken ct);

    public IReadOnlyList<ReaderRuntimeSnapshot> Readers { get; }
    public ReaderRuntimeSnapshot GetSnapshot(Guid readerId);

    // Enable 语义：IsEnabled（用户意图，持久化）与连接状态（Disconnected/Connecting/Connected/...）分离
    public event EventHandler<ReaderStateChangedEventArgs> StateChanged;
}
```

- **连接租约分类**：Probe 使用临时 Session；激活/新增后的能力与设置同步、Settings、Tag Access、GPO 使用短连接租约，用完即断；**Inventory 从 Start 到 Stop 持有同一个完整 LLRP 长连接租约**，期间不重新连接、不被其他页面抢占；所有连接租约必须由服务层统一管理，ViewModel 不得手动 Connect/Disconnect；
- **Inventory 生命周期**：`StartInventoryAsync` 获取该 Reader 的 Gate，建立唯一 Session，读取/应用 Inventory 配置，启动 ROSpec/InventorySession 并进入 `Inventorying`；`StopInventoryAsync` 停止 ROSpec/InventorySession，断开 TCP 并释放租约，回到 `Disconnected`。这是一轮完整的“连接→盘存→停止→断开”生命周期，不是多个短操作的拼接；
- **Enable=true**：启动时自动激活同步到缓存，不保持 Session（冻结项目中已验证的语义）。
- `ReaderManager` 实现 `IReaderManager`，并在每个 UI 应用组合根中注册为 Singleton；UI 层通过接口访问 Reader，不复制设备生命周期逻辑。
- **TCP 独占**：每个 `ReaderHandle` 在任一时刻最多拥有一个活动 Session，并使用独立异步 Gate 串行化 Probe 之外的 Connect、Settings、Inventory、Tag Access、GPO、Disable 和 Remove 操作；
- **操作冲突策略**：Inventory 持有长连接租约。Inventory 运行时，Settings、Tag Access 和 GPO 默认返回明确的 `ReaderBusy` 结果，不隐式停止盘存；调用者必须先显式 Stop。Disable/Remove 可以取消当前操作，随后停止 Inventory、断开并释放 Session；连接故障或短租约断开不可靠时，下一次 Activate/Start/短操作必须回收旧 Session 并创建干净 Session，不复用故障连接。
- **架构不变性**：新增设置、盘存、厂商扩展和 SQLite 功能只能扩展 Contracts/Services/Infrastructure/Extensions 的既有边界，不允许把连接编排下沉到 ViewModel，不允许为设置或盘存创建第二套 Fleet/Session 管理器，也不允许让 Infrastructure 或 Extensions 反向依赖 App.Wpf；
- **状态发布线程**：Services 不捕获 UI SynchronizationContext；状态事件在服务线程发布，各 UI 适配层负责切换到自己的 UI 线程。

`AddAsync` 不是跨网络、SQLite 和内存注册的数据库事务，而是补偿式流程：

```text
Probe 失败       -> 不持久化、不注册
Profile 保存失败 -> 不注册
注册失败         -> 删除刚保存的 Profile
可选激活失败     -> 保留 Profile，回滚并持久化 IsEnabled=false，记录错误
```

### 3.2 Capabilities —— 能力与缓存（服务层）

- `ReaderProfile`、用户 Preset 和需要离线保留的 Settings Snapshot 持久化；
- 实时 `ReaderCapabilities`、连接状态、操作状态和活动扩展属于 `ReaderRuntimeSnapshot`，**只保存在内存，不持久化到 DB**；
- 每次激活/连接时更新对应 ReaderHandle 的 RuntimeSnapshot；断开后保留本进程最后一次成功激活得到的能力，并标记 `CapturedAt`/`IsStale`；
- 能力驱动的 UI 选项（RF mode / Tx/Rx / 频率）从此内存缓存填充，避免"读缓存页下拉空"。
- 应用重启后，如果 Reader 尚未成功激活，则设置页进入“需要连接以获取能力”的只读状态，不使用数据库中的旧能力猜测可用参数。

### 3.3 Settings —— 能力驱动设置模型（服务层，核心创新，冻结项目没有）

```csharp
public sealed class ReaderFeatureCatalog
{
    // 由 ReaderCapabilities + 激活的扩展模块聚合出"该设备支持什么"
    public required IReadOnlyList<Feature> SupportedFeatures { get; init; }
    public bool Supports(Feature feature) => SupportedFeatures.Contains(feature);
}

public sealed class EffectiveSettingsLayout
{
    // 根据 catalog 决定：哪些设置项显示/隐藏/只读/可选值/校验规则
    public required IReadOnlyList<SettingsEntry> Entries { get; init; }
}

public sealed record SettingsEditorModel(
    EffectiveSettingsLayout Layout,
    SettingsSnapshot Snapshot,
    SettingsDraft Draft);

public interface IReaderSettingsService
{
    Task<SettingsEditorModel> QueryAsync(Guid readerId, CancellationToken ct);
    SettingsValidationResult Validate(SettingsDraft draft);
    Task<SettingsApplyResult> ApplyAsync(Guid readerId, SettingsDraft draft, CancellationToken ct);
}

// 仅 Services 内部使用；SettingsCompiler 实现该接口
internal interface ISettingsCompiler
{
    CompiledSettings Compile(SettingsDraft draft, EffectiveSettingsLayout layout);
}
```

- 标准 LLRP 设置为服务层核心；Impinj Search Mode / FastID / Phase / Doppler / 定频 / Low Duty / GPI debounce 等由扩展模块贡献；
- 未知厂商设备：身份与盘存按 L1/L2 提供；设置页只显示设备能力明确支持的标准 L3 设置，不出现厂商项。
- `EffectiveSettingsLayout`、`SettingsSnapshot`、`SettingsDraft`、`CompiledSettings` 是不同模型；每个设置项必须有稳定 Key、值类型、当前值、默认值、可选项/范围、只读原因和来源。
- `SettingsEntry` 使用 UI 无关的 `EditorKind`（Boolean/Choice/Integer/Decimal/Text/Collection）和语义化数据类型；不得出现 TextBox、ComboBox、CheckBox、Visibility 等 WPF 类型或控件名称；
- `SettingsDraft` 携带 ReaderId 和 CapabilityRevision。保存前重新校验能力版本，防止设备重连或固件变化后把过期选项发送给 Reader；
- UI 只调用 `IReaderSettingsService`。验证可以同步执行，Query/Apply 负责连接租约和设备 I/O；`CompiledSettings` 与 SDK `ReaderSettings` 始终留在 Services/Extension 内部。

### 3.4 Modules —— 厂商扩展模块抽象（服务层）

```csharp
public interface IReaderExtensionModule
{
    string Id { get; }
    bool IsApplicable(ReaderProbeInfo info);
    IReadOnlyList<Feature> GetFeatures(ReaderProbeInfo info);
    ISettingsExtensionContributor? SettingsContributor { get; }
    // 在创建/连接 SDK Reader 前注册协议扩展，例如 Impinj 的 Builder 配置
    void ConfigureBuilder(ReaderBuilderContext context);
    // 将 SDK TagReport 中的扩展字段投影到 UI 无关 DTO
    ReaderTagReportProjection ProjectTagReport(TagReport report);
}
```

- `LlrpReaderPlatform.Extensions.Impinj`（独立项目）实现 Impinj R420 模块；
- 模块由宿主应用的组合根通过 `AddImpinjExtension()` 注册，Services 不扫描、硬编码或直接引用厂商包；
- 未来 Seuic/其他厂商新增独立模块，不改服务层核心；但每个模块必须有实际设备或协议测试，不因接口存在就宣称已支持。

Auto 模式采用明确的两阶段连接，解决“模块要在连接前配置，但厂商身份要在连接后识别”的顺序问题：

```text
标准 Probe（不加载厂商扩展）
  -> 读取 Identity / 标准 Capabilities / 协议版本
  -> ExtensionResolver 调用已注册模块的 Match
  -> 无匹配：生成标准 ReaderRuntimeSnapshot 并结束
  -> 有匹配：断开标准 Session
       -> 新建 Session 并调用模块 ConfigureReader
       -> 扩展连接，读取扩展能力与设置
       -> ParseSettings / ContributeCapabilities / ContributeSettings
       -> 更新 ActiveExtensions 后断开
```

用户显式选择 Standard 时跳过扩展匹配；显式强制某扩展但模块未注册或设备拒绝扩展时返回明确错误，不静默降级。

### 3.5 Inventory / TagReport / TagAccess（服务层）

- 标准 Inventory 启动/停止（ROSpec 部署）；
- TagReport 经有界 Channel 后台聚合（沿用冻结项目的防卡死方案：泵线程 O(1) 入队 + 后台聚合 + 定时批量）；
- TagAccess（读/写 TagMemory）标准封装；
- GPI/GPO 能力控制。

### 3.6 UI 层架构（App.Wpf，第一个 WPF 消费者）

**技术栈**：WPF + `CommunityToolkit.Mvvm`（`[ObservableProperty]`/`[RelayCommand]`）+ `MahApps.Metro`（窗口/控件）。忙碌反馈使用 WPF 原生 `ProgressBar`，不自绘进度指示器；沿用冻结项目已验证的组合，不引入新 UI 库。

**页面结构（ViewModel-first 导航）**：

```text
Views/  +  ViewModels/
├── DataSourcesView / DataSourcesViewModel      设备列表：名称/端点/开关(Enable)/状态/删除/新增入口
├── AddDataSourceView / AddDataSourceViewModel  新增：Host/Name/Port/LLRP版本/厂商选项
├── ReaderSettingsView / ReaderSettingsViewModel 能力驱动设置页（由 EffectiveSettingsLayout 生成）
├── InventoryView / InventoryViewModel           寻卡：Start/Stop、读速率/唯一tag计数、Tag表格
├── TagMemoryView / TagMemoryViewModel           Tag 读写
├── Reader Settings Tab2 / DiagnosticsViewModel  旧项目 Tab2 的 GPO 控制与 GPI/GPO 状态刷新
└── Shell（MainWindow/MainViewModel）           左侧设备列表 + 右侧 ContentControl 页面
```

**能力驱动设置页（关键设计）**：
- `ReaderSettingsViewModel` **不手写字段**，而是绑定 `EffectiveSettingsLayout.Entries`（服务层生成）；
- 每个 `SettingsEntry` 描述：稳定 Key、标题、`EditorKind`、值类型、可选值/范围、显示条件、只读原因、校验规则和来源；
- WPF 将 `EditorKind` 映射为自己的 DataTemplate；其他 UI 框架可以采用不同控件，不同设备能力仍生成相同语义布局；
- 保存时把 `SettingsDraft` 提交给 `IReaderSettingsService.ApplyAsync`；服务内部完成能力复核、编译、短连接 Apply 和结果更新，WPF 不直接接触 `SettingsCompiler` 或 SDK 的 `ReaderSettings`。

**与服务层的对接规则**：
- `MainViewModel` 消费 `IReaderManager`（列表/开关/状态）；`ReaderSettingsViewModel` 消费 `IReaderSettingsService` 返回的 `SettingsEditorModel`；
- ViewModel **只引用 Contracts/Services 的 DTO**，不直接引用 `LlrpSdk`、`LlrpNet.Protocol` 或任何厂商扩展类型；
- 页面间共享状态用 Singleton 服务 + 强类型消息（`WeakReferenceMessenger`），沿用冻结项目经验但更收敛。

**UI 经验（从冻结项目继承）**：
- per-reader 状态隔离（每设备独立设置 VM）；
- capabilities 内存缓存填充下拉；
- 短连接 + 显式刷新（REFRESH）；
- 高频 TagReport 批量渲染（不逐条刷新 UI）；
- 启动/同步时忙碌遮罩（WPF 原生 ProgressBar）。

## 4. 与冻结项目（LlrpReaderStudio）的迁移边界

**迁移原则**：旧仓库冻结后只作为参考，**新仓库不引用旧项目**；能证明与厂商无关的实现可以迁移，涉及 Impinj 类型、旧 ViewModel 和旧持久化耦合的部分必须重写：

| 冻结项目项 | 迁移方式 |
|---|---|
| ReaderFleetService 生命周期 | **选择性迁移**，提炼为 ReaderManager，并改为可被多个 UI 消费者调用的服务 |
| 短连接 / Enable 语义 | **直接沿用**（已修正文档语义） |
| 有界 Channel 防卡死 | **直接沿用**（泵线程入队 + 后台聚合） |
| capabilities 内存缓存 | **直接沿用** |
| per-reader 状态隔离 | **直接沿用**（字典 ReaderId→Handle） |
| Inventory / TagMemory / 设备设置 Tab2 | **可迁移**，随 UI 重构落位 |
| DataSourceSettingsViewModel（1613 行） | **不迁移**，重写为能力驱动设置模型（EffectiveSettingsLayout） |
| MainWindow/导航/MahApps 风格 | 可沿用主题与导航模式，结构重构 |
| LlrpSdk 引用 | 默认使用 NuGet；跨仓库联调时通过 MSBuild 属性切换为相邻 LLRPCSharp SDK 的 ProjectReference；不引用旧 WPF 项目的 DLL 或源码 |
| SQLite Profile/Preset | 由新仓库建立新数据目录；早期版本不承诺历史数据兼容 |
| mDNS Discovery/日志 | 迁移到 Infrastructure；不放入 WPF ViewModel |

## 5. 分阶段实施计划

### F1：新仓库骨架、契约与依赖验证（1~2 人日）
- 建 `LlrpReaderPlatform.slnx`、Contracts、Services、Infrastructure、App.Wpf、Tests 和 Impinj 扩展项目；
- 固定 Contracts/Services/Infrastructure/Extensions 为 `r`net10.0`，App.Wpf 为 `r`net10.0-windows`；
- `Services` 默认引用 LlrpSdk NuGet；通过 `UseLocalLlrpSdk` 和 `LlrpSdkSourceRoot` 可切换到本地 SDK/LlrpNet 项目；
- 在 Contracts 定义 `IReaderManager`、`ReaderRuntimeSnapshot`、Settings Layout/Snapshot/Draft、稳定的 `SettingsKeys`、`IReaderSettingsService` 和状态 DTO；在 Services 内部定义 `CompiledSettings`；
- 定义扩展模块注册接口和 `IReaderProfileStore` 等服务边界；
- 建立可控的 `LlrpReaderPlatform.TestKit/FakeSession`，先验证不依赖旧项目和 SDK 源码即可构建；
- 提供 `AddLlrpReaderPlatform()`、`AddLlrpInfrastructure()`、`AddImpinjExtension()`；在 App.Wpf 组合根完成 DI 启动/退出和一个最小页面；
- 增加架构测试，阻止 Contracts/Services/Infrastructure 引用 WPF，并阻止 Contracts 暴露 SDK 类型。

### F2：标准生命周期与 Capabilities（服务层，3~5 人日）
- `ReaderProfile`（厂商无关，含 LLRP 版本策略）、`IReaderManager`/`ReaderManager`、`ReaderHandle`、`ReaderRuntimeSnapshot`；
- 完成 Probe -> 持久化 -> 注册 -> 可选激活的补偿式添加流程和失败回滚；
- 短连接激活、Enable 分离、状态事件；
- capabilities 内存缓存 + 激活填充；每 Reader 单 Session + 异步 Gate；设置/Tag Access/GPO 使用统一连接租约；
- 明确 Inventory 与其他操作的 `ReaderBusy`、Stop、Disable、Remove 状态转换和清理测试；
- 以标准 LLRP 1.0.1 设备完成第一轮实机验证。

### F3：能力驱动设置模型（服务层，4~6 人日，核心）
- `ReaderFeatureCatalog` / `EffectiveSettingsLayout` / `SettingsEditorModel` / `IReaderSettingsService`；
- UI 无关的 `EditorKind`、Settings Snapshot/Draft、CapabilityRevision 和校验结果；
- Services 内部 `SettingsCompiler` 与 `CompiledSettings`，不向 UI 暴露 SDK 类型；
- 标准 LLRP 设置编译（Tx/Rx/RF mode/频率/Gen2 Filter/天线）；
- 未知厂商设备的降级显示。

### F4：Inventory / TagReport / TagAccess（服务层，3~4 人日）
- 标准盘存的完整租约生命周期：Start 建立并持有单一 LLRP Session，Stop 停止 ROSpec/InventorySession 后断开并释放；
- TagReport 有界 Channel 聚合，SDK 消息泵线程只做非阻塞入队，后台线程聚合，UI 线程批量刷新；
- TagAccess、GPI/GPO，以及 Inventory 运行时的 `ReaderBusy` 冲突；
- Stop/Start 重入、设备主动断连、ROSpec 清理和 Dispose 路径测试。

### F5：Impinj R420 扩展模块（3~5 人日）
- `LlrpReaderPlatform.Extensions.Impinj` 独立项目，并通过 `AddImpinjExtension()` 由宿主组合根显式注册；
- 迁移 Search Mode/FastID/Phase/Doppler/定频/Low Duty/GPI debounce/Preset/TagReport 投影。
- 首版已实现标准 Probe 匹配、Builder 配置、能力/设置贡献和 `ProjectTagReport`；固件范围能力匹配及更多厂商字段仍按设备矩阵扩展；
- 以 Impinj R420 完成回归，不把其他 Impinj 型号默认为已验收。

### F6：WPF UI 骨架与设备管理（2~3 人日）
- App.Wpf 骨架（MahApps 主题、导航、DI）；
- DataSources 页（列表/开关/状态）+ AddDataSource 页 + 服务层 `IReaderManager` 对接；
- 忙碌遮罩、状态展示。

### F7：能力驱动设置页 UI（3~4 人日）
- `ReaderSettingsViewModel` 绑定 `SettingsEditorModel`；
- 将语义化 `EditorKind` 映射为 WPF DataTemplate；
- 保存走 `IReaderSettingsService.ApplyAsync`；REFRESH/短连接交互；
- 无 RuntimeCapabilities 时显示“需要连接以获取能力”的只读状态。

### F8：Inventory / TagMemory / 设备设置 Tab2 UI（3~4 人日）
- 寻卡页（Start/Stop、速率/计数、Tag 表格批量渲染）；
- Tag Memory 读写页；
- 设备设置 Tab2 的 GPO 控制（沿用旧项目四路 ToggleSwitch）和 GPI 状态刷新，不新增独立 Diagnostics 页面。

### F9：测试与实机验收（3~5 人日，取决于设备可用性）
- 使用 `LlrpReaderPlatform.TestKit` 的 FakeSession 覆盖生命周期、设置验证、扩展选择和错误回滚；
- Services.Tests 不依赖真实网络，Extensions.Impinj.Tests 覆盖模块注册、探测和设置编译；
- 覆盖补偿式 Add、单 Session/Gate、ReaderBusy、CapabilityRevision 过期和扩展两阶段连接；
- App.Wpf 做 DI 创建、页面 DataTemplate 和关键 ViewModel 状态投影的冒烟测试；
- Impinj R420 与标准 LLRP 1.0.1 设备分别及同时运行；
- LLRP 1.1 只有在取得真实 1.1 设备后才提升为已支持能力，否则标记为待验收。

### F10：SQLite 持久化与启动恢复
- 使用 EF Core SQLite 实现 Reader Profile、Settings/Inventory Preset、Tag List、Inventory Run、App Settings；
- 启动时从数据库恢复 Profile，按 `IsEnabled` 执行激活同步；
- 配置缓存只作为离线只读回退，设备查询成功后覆盖缓存；
- 数据库初始化、必要的 EF schema 变更和临时测试数据库隔离；早期版本允许通过清空数据库重建，不把历史数据兼容作为交付门槛。

### F11：完整设备设置闭环
- 扩展 Session 的 Query/Default/Validate/Apply SDK 操作；
- 完成标准 LLRP 设置、Gen2 Filter、RF、Report、GPI/GPO 和 Impinj 扩展设置；
- `SettingsService` 负责能力版本校验、编译、短连接下发、重新 Query 验证和缓存更新；
- Inventory 运行时设置操作必须返回 `ReaderBusy`，不得停止或替换 Inventory Session。

### F12：完整 Inventory 和 WPF 实时消费
- Inventory ViewModel 订阅 `TagObserved`，通过有界 UI 队列和 DispatcherTimer 批量渲染；
- 实现 Start→长连接租约→TagReport→Stop→断开的完整生命周期；
- 完成读速率、计时、Unique Tags、报告字段、Tag List 和 Inventory Run；
- 真实验证高频报告下 SDK 消息泵、Services 和 WPF 均不阻塞。

### F13：全应用功能迁移
- 完成设备设置 Tab2 GPO、Tag Memory、App Settings、日志、Tag List 和运行记录页面；
- 页面沿用旧 WPF 的交互语义，但 ViewModel 只调用新平台服务；
- 所有忙碌反馈使用 WPF 原生 `ProgressBar`，不自绘进度组件。

### F14：多设备、多厂商和兼容性提升
- 建立标准 LLRP 1.0.1/1.1、Impinj R420 和其他型号的 L1～L4 设备矩阵；
- 扩展模块负责厂商能力、设置、TagReport 和协议差异，核心服务保持不变；
- 未知设备至少提供 L1/L2；不显示或下发未声明能力的厂商参数；
- 每个新厂商模块必须有模块单测、FakeSession 测试和真实硬件验证记录。

### 5.1 按依赖层级实施的完整开发顺序

WPF 不是功能实现的起点，而是最后一个消费者层。任何页面在对应的 Services/Infrastructure 能力没有真实实现前，不得以“页面可操作”作为完成标准。

| 顺序 | 层级 | 必须完成的内容 | 进入下一层的门槛 |
|---|---|---|---|
| P0 | 架构与契约 | ADR、设备能力模型、Settings/Inventory/Diagnostics DTO、错误和 ReaderBusy 语义 | Contracts 不引用 SDK/WPF，架构测试通过 |
| P1 | SDK Adapter | `IReaderSession` 的 Connect/Disconnect、Query/Apply Settings、Inventory、Tag Access、GPI/GPO、异常投影 | FakeSession 可模拟完整生命周期；SDK Adapter 有真实 API 映射测试 |
| P2 | Services Runtime | ReaderManager 单 Session/Gate、短连接、Inventory 长连接租约、Stop 清理、设备断连、CapabilityRevision | 不同 Reader 并行运行；Inventory 期间其他操作返回 ReaderBusy |
| P3 | Infrastructure | EF Core SQLite、Profile/Preset/TagList/Run/AppSettings Store、迁移、启动恢复 | 临时 SQLite CRUD/迁移测试通过，应用可以恢复 Reader Profile |
| P4 | Extensions | Impinj 能力/设置/TagReport/序列化贡献；未来厂商模块接口和降级路径 | R420 两阶段连接和 L4 字段测试通过；未知设备不加载厂商参数 |
| P5 | Settings/Inventory Services | 真实 Query→布局、Apply→SDK→重新 Query；Start→长连接→TagReport→Stop→断开；高频报告管线 | Services.Tests 覆盖成功、失败、并发、缓存和 ReaderBusy |
| P6 | 应用服务 | Tag List、Inventory Run、日志、Diagnostics/GPO、Tag Memory 协调 | 不依赖 WPF 的服务测试和持久化测试通过 |
| P7 | WPF Consumer | 页面、ViewModel、DataTemplate、导航、ProgressBar、Dispatcher 批量刷新 | WPF 只调用服务接口；不存在假保存、假连接、手动 SDK 调用 |
| P8 | 真机与兼容性 | R420、标准 LLRP 设备、未知标准设备、多 Reader 并行、故障恢复 | 设备矩阵按 L1～L4 逐项签字验收 |

### 5.2 最终交付详细工作包

最终交付物不是单独一个 WPF 工程，而是一个可以在真实 Reader 上运行的完整解决方案：共享类库、基础设施、厂商扩展、WPF 消费者、自动化测试、设备验收记录和可迁移数据库全部完成。

#### WP0：交付基线和旧项目功能矩阵

- 以旧 `LlrpReaderStudio` README、`wpf-reader-ui-handoff.md`、旧 Core/Infrastructure/Wpf 源码为行为基线；
- 建立“旧功能 → 新 Contracts → 新 Services → Infrastructure/Extension → WPF 页面 → 测试 → 真机验收”的矩阵；
- 每项功能标记 `Planned / Implemented / AutomatedTested / HardwareVerified` 四种状态；
- 旧项目只作为参考，不添加 ProjectReference，不复制旧项目的 Impinj 依赖和大型设置 VM；
- 完成 ADR：架构边界、Inventory 长连接租约、EF SQLite、新平台 schema 演进、扩展模块和生命周期事件。

交付门槛：矩阵中所有旧 WPF 功能都有新平台归属；没有“以后再补”的核心链路占位实现。

#### WP1：Contracts 类库完整交付

完善 `LlrpReaderPlatform.Contracts`：

- Reader Profile、连接参数、协议版本策略、启用意图；
- Reader Identity、Runtime State、Operation State、Capability Snapshot；
- L1～L4 Feature Catalog 和能力来源；
- Settings Layout、Snapshot、Draft、Validation、Apply Result；
- 标准 Inventory Settings、Report Settings、Filter、Antenna、RF、GPI/GPO DTO；
- Tag Observation、Tag Access、Tag List、Inventory Run、Application Settings；
- 统一错误模型：`ReaderBusy`、`DeviceFailed`、`Unsupported`、`StaleCapability`、`InvalidSettings`；
- Profile/Preset/TagList/Run/AppSettings 持久化接口；
- UI 无关的消息和状态事件参数。

约束：Contracts 不引用 `LlrpSdk`、`LlrpNet.Protocol`、WPF、Dispatcher、MahApps 或 Impinj 类型。

#### WP2：SDK Adapter 和 Session 类库完整交付

完善 `LlrpReaderPlatform.Services.Sdk`：

- Connect/Disconnect、协议版本协商和超时；
- Identity、Capabilities、Negotiated Version 读取；
- Query 当前 `ReaderSettings`；
- Query SDK Defaults；
- Validate/Apply `ReaderSettings`；
- Start Inventory with Settings；
- Start Inventory with current device configuration；
- Stop Inventory、ROSpec 清理和 InventorySession Dispose；
- Tag Memory Read/Write；
- GPO 控制；
- SDK ReaderException、ConnectionChanged、DeviceInitiatedClose 统一投影；
- SDK TagReport 转换为 Services 内部 DTO；
- SDK `ReaderSettings`、`InventorySettings`、`ReaderCapabilities` 映射测试。

`IReaderSession` 只在 Services 内部使用，SDK 类型不得穿透到 Contracts 或 WPF。

交付门槛：FakeSession 可以完整模拟 Query/Apply、Inventory Start/Stop、TagReport、异常和设备断连；真实 SDK Adapter 可以对 R420 完成 Connect/Query/Start/Stop/Disconnect。

#### WP3：ReaderManager 生命周期类库完整交付

完善 `ReaderManager`：

- `Probe → Persist → Register → Optional Activate` 补偿式添加；
- 单 Reader 单 `ReaderHandle`；
- 单 TCP Session 所有权；
- 每 Reader 独立异步 Gate；
- 短连接操作统一租约；
- Inventory 长连接租约；
- Enable 意图与连接状态分离；
- Disable/Remove 时停止 Inventory、断开、Dispose；
- Settings/TagAccess/GPO 在 Inventory 运行时返回 ReaderBusy；
- DeviceInitiatedClose、网络失败、取消和超时处理；
- DisposeAsync 正确释放 Session、Channel、Gate 和后台任务。

Inventory 必须严格遵循：

```text
Start
  → Gate
  → Connect
  → Query/Apply Inventory Settings
  → Start ROSpec/InventorySession
  → State=Inventorying
  → 持续 TagReport
Stop
  → Stop InventorySession/ROSpec
  → Disconnect 同一 Session
  → Release Lease
  → State=Disconnected
```

交付门槛：不会按标签、按报告、按 UI Refresh 重新连接；设置和 Tag Access 不会抢占 Inventory Session。

#### WP4：Infrastructure 和 EF Core SQLite 类库完整交付

完善 `LlrpReaderPlatform.Infrastructure`：

- `PlatformDbContext` 和 `IDbContextFactory`；
- 数据库目录初始化；
- EF migrations；
- Reader Profile Repository；
- Settings Preset Repository（以版本化语义 JSON 保存设置与 Inventory 字段）；
- Tag List Repository；
- Inventory Run Repository；
- App Settings Repository；
- Zeroconf/mDNS Discovery；
- 应用日志和 SDK 日志基础设施。

默认数据库：

```text
%LocalAppData%\\LlrpReaderPlatform\\llrp-reader-platform.db
```

交付门槛：应用重启后可以恢复 Reader Profile、启用意图和 Preset；Inventory 预设属于同一份 `ReaderSettingsPreset` 的语义 JSON，不另建旧库导入或历史兼容路径；设置缓存只能作为离线只读回退，不能伪装为实时设备配置。

#### WP5：Capabilities 和 Extension 类库完整交付

完善扩展模块契约：

- 标准 Probe 后的厂商匹配；
- Builder 配置；
- 能力贡献；
- Settings Layout 贡献；
- Settings Query/Apply 映射；
- TagReport 扩展字段投影；
- Preset 序列化；
- 固件/型号/能力范围匹配；
- 未匹配设备的标准降级路径。

Impinj 模块必须覆盖旧项目已使用的：

- Search Mode；
- FastID/Serialized TID；
- RF Phase；
- Doppler；
- Fixed Frequency；
- Low Duty Cycle；
- GPI Debounce；
- Impinj Report Options。

未来厂商扩展只能新增 `Extensions.VendorName`，不能修改 ReaderManager 的核心连接编排。

#### WP6：Settings Services 完整交付

实现真实设置闭环：

- 从设备 Query 当前配置；
- 从设备能力生成可用设置项；
- 设置项有稳定 Key、类型、当前值、默认值、选项、范围、来源和只读原因；
- 标准设置编译为 SDK `ReaderSettings`；
- Impinj 设置由扩展模块编译；
- Query 失败时使用 SQLite Preset 只读显示；
- CapabilityRevision 过期时拒绝 Apply；
- Apply 前执行 SDK Validate；
- Apply 成功后重新 Query 验证；
- 验证成功后才更新 SQLite Preset；
- SQLite Preset 写入失败只影响离线缓存，不把已经成功下发到设备的 Apply 误报为失败；
- Inventory 运行时返回 ReaderBusy。

覆盖旧设置页全部功能：

- 天线和全局/逐天线 Tx/Rx；
- RF Mode、Session、Population、Report Interval；
- Gen2 Filter 1/2；
- State-aware Singulation；
- Search Mode；
- Frequency/Channel List；
- Low Duty Cycle；
- GPI Start/Stop；
- GPI Debounce；
- FastID/TID、Phase、Doppler；
- GPI/GPO Reader Configuration。

#### WP7：Inventory、TagAccess、Diagnostics Services 完整交付

- Standard Inventory Settings；
- Report metadata 和列选项映射；
- TagReport 有界 Channel；
- 后台聚合和丢弃统计；
- EPC/TID/PC/RSSI/天线/信道/时间统计；
- Tag Access Read/Write；
- GPO 1～4；
- Inventory Run 记录；
- Tag List 匹配和显示；
- 设备主动断连后的运行状态恢复。

高频报告约束：SDK 消息泵线程只能 `TryWrite`；聚合在后台任务；WPF 使用 DispatcherTimer 批量消费；所有队列和显示集合有上限。

#### WP8：WPF 完整消费者交付

WPF 页面必须建立在 WP1～WP7 完成后：

- Data Sources：Profile、启用、状态、删除、mDNS Discovery；
- Add Data Source：Probe、协议版本、扩展匹配结果和进度；
- Reader Settings：旧项目完整设置分组和能力驱动 DataTemplate；
- Inventory：Start/Stop、实时 TagReport、速率、计时、Unique Tags、列配置和按时长自动停止（0 表示持续运行）；
- Tag Memory：读写和 ReaderBusy 错误；
- Reader Settings Tab2：旧项目四路 GPO 控制；GPI 配置留在 Tab1 的 GPI CONFIGURATION 分组；
- Tag Lists：列表、条目、匹配；
- Inventory Runs：历史和统计；
- App Settings：盘存数据记录模式、原始报告目录和应用参数；
- About；
- WPF 原生 `ProgressBar`；
- Dispatcher 批量 UI 更新；
- Async DI Container Dispose。

ViewModel 不得：

- 直接创建 Session；
- 直接调用 SDK；
- 自己实现 Connect/Disconnect；
- 自己访问 SQLite；
- 在 TagReport 事件线程修改 ObservableCollection；
- 显示“保存成功”但没有设备 Apply。

#### WP9：自动化测试、真机验收和发布交付

自动化测试：

- Contracts 序列化和边界测试；
- Services 生命周期、Gate、ReaderBusy、取消、断连测试；
- SDK Adapter 映射测试；
- Settings Query/Apply/Cache/Revision 测试；
- Inventory 长连接和 Stop 清理测试；
- 高频 TagReport 压力测试；
- EF SQLite CRUD 和基础 schema 初始化测试；
- Impinj 扩展测试；
- WPF DI、DataTemplate、ViewModel 行为测试；
- Architecture Tests 保证依赖方向。

真机验收顺序：

1. 标准 LLRP 1.0.1：连接、身份、能力、Inventory；
2. Impinj R420：连接、能力、设置 Query/Apply、Inventory、Tag Memory、GPO；
3. R420 Impinj 扩展：FastID、TID、Search Mode、Phase、Doppler、频率、Low Duty；
4. 多 Reader 同时运行；
5. 设备断连、重启、Stop/Start、应用关闭；
6. 未知标准 Reader 的 L1/L2 降级；
7. 真实配置修改后重新读取验证并恢复原值。

最终交付必须包含：

- 可构建的完整 `.slnx`；
- 所有依赖类库；
- WPF 可执行应用；
- EF migrations；
- 自动化测试；
- 设备兼容性矩阵；
- 数据库迁移说明；
- 真机验收记录；
- 用户操作和故障排查文档。

当前代码已经覆盖 P0～P7 的首版交付链路：Contracts、Services、EF Core SQLite、Impinj 扩展、Inventory/Settings/TagAccess/GPI 服务和旧项目主要页面均已落地；WPF 组合根已集中注册页面 ViewModel，Shell 不再负责创建页面对象；P8 真机已验证 WPF Settings Apply、GPO 回写、有界 Inventory Start/Stop/Disconnect、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复和 Impinj FastID/Phase 扩展字段，2026-08-11 又确认 WPF 设备列表状态刷新不会取消在途设置查询，R420 设置页可稳定显示 62 个回读值、`Loaded from Reader`、Save 入口、Impinj 字段、4 个天线和 4 个 GPI 行；WPF 用户操作与故障排查文档也已纳入交付入口；服务代码已补齐设备主动断连、一般 Connection Faulted、ReaderException 以及匹配 GPI Stop 触发器的 Inventory 收尾和重新 Start 自动化验证，最新 `win-x64` 发布包还已从新平台 SQLite 恢复 R420、完成 LLRP 1.0.1 协商并以正常退出码结束；仍需完成 GPI 物理事件/触发、其它 Memory Bank 写入、多 Reader 与断网/重启现场恢复验收。平台 TagReport 扩展投影和 UI 无关 `ExtensionFields` 已由自动化测试覆盖。SQLite 只维护新平台自身数据，早期 schema 变化允许清空数据库重建，不把历史数据导入作为交付项。

2026-08-11 又完成一次发布运行时验收：`win-x64` WPF 直接启动并激活 R420，寻卡页实际收到真实 EPC，界面显示 5～6 个唯一标签和约 269～300 tags/s；手动 Stop 最终回到 `Start`/已同步能力，正常关闭窗口后 `DisposeAsync` 退出释放路径完成。该证据补强首个 WPF 消费者的真实运行闭环，但不替代 GPI 物理触发、多 Reader、断网/重启和其它 Memory Bank 写入的现场验收。随后补齐主窗口底部 `Inventory.Status` 展示，GPI/定时/断连等平台生命周期停止原因可直接从 WPF 状态栏观察；设备列表和设置页也会显示持久化的 LLRP 版本策略，Force101 Reader 在离线或重启恢复时仍可见其强制 1.0.1 约束。

2026-08-11 再完成一次发布包回归：寻卡约 36 秒收到 7 个唯一 EPC、约 9655 次读取，手动 Stop 后回到 `Start` 且活动 TCP 连接数为 0；Inventory Runs 加载 17 条记录，最新为 `Manual / 9655 reads / 7 unique`；设备设置 Tab1 仍为 `Loaded from Reader`/可保存，Tab2 回读 4 路 GPI、4 路 GPO，GPI 均为低电平且 GPO1 恢复 OFF。该证据补强 WPF 的真实 Start→TagReport→Stop→RunStore→Settings Query→Disconnect 链路，P8 仍只剩物理 GPI、多 Reader、断网/重启和其它 Memory Bank 写入等现场项。

2026-08-11 第二台标准 Reader 证据：`192.168.41.148:5084` 使用强制 LLRP 1.0.1 完成 Probe→Add→Activate，实际协商 `Version101`，Model `57690:40`、Firmware `1.0.0.233`，Settings Query 生成 57 个可编辑项；设备未接天线，Inventory、TagAccess 和带天线多 Reader 并行仍待现场验收。

同一第二台 Reader 已完成一次设置回写闭环：`Report Every N Tags` 真实执行 `1→2→1` Apply/回读并恢复原值；对能力表未列出但设备当前正在使用的 RF Mode，标准设置布局会追加当前值作为兼容 Choice，避免无关设置修改被错误拒绝。

同一设备的 GPIO 状态短操作随后返回 4 个 GPI 和 4 个 GPO 端口；由于能力快照没有声明端口数量，这属于未知能力兼容回退的 Query 证据，不替代物理 GPI 事件/触发验收。

随后以 R420（Auto→`Version101`）和 148（Force101→`Version101`）并行完成 Activate、Settings Query、GPIO Query：R420 返回 69 个设置项并启用 Impinj 扩展，148 返回 57 个标准设置项，两台均回到 `Disconnected`、能力非陈旧、无 `Faulted`，活动 TCP 均为 0；这只证明多 Reader 短连接隔离，不替代双 Reader 同时 Inventory。

## 6. 验收标准

- Impinj R420 达到 L1～L4；至少一台标准 LLRP 1.0.1 设备达到 L1～L2，L3 功能按设备能力逐项记录验收结果；
- R420 和标准 LLRP 1.0.1 设备可分别及同时运行；每台 Reader 只有一个活动 TCP Session；
- 未知厂商设备：至少连接、显示身份/能力、执行标准盘存，不出现或发送 Impinj 参数；
- 能力驱动 UI：不同能力快照生成不同语义布局，无法选择或发送不支持参数；CapabilityRevision 过期时拒绝保存并要求刷新；
- Settings UI 只提交 `SettingsDraft`，不直接调用 Compiler，不引用 SDK 或厂商类型；
- Contracts/Services/Infrastructure 不引用 `UseWPF`；架构测试阻止 Contracts 暴露 SDK/WPF 类型，未来其他 UI 框架可复用；
- 短连接、Inventory 长连接租约、单 Session、异步 Gate 和 ReaderBusy 由服务层统一管理，ViewModel 不直接 Connect/Disconnect；
- 每次 Inventory 必须满足 Start 建立唯一 LLRP Session、运行期间持续接收报告、Stop 停止并断开同一 Session；不得按标签、报告或 UI 刷新重新连接；
- 设置页由 `EffectiveSettingsLayout` 驱动，无冻结项目的 1613 行单 VM。

## 7. 风险与对策

| 风险 | 对策 |
|---|---|
| SDK 源码仓库缺失或路径不同 | 默认 NuGet 构建不依赖源码仓库；仅启用本地模式时要求有效的 `LlrpSdkSourceRoot` |
| Seuic 等厂商扩展未发 NuGet | 不在首版验收范围；服务层只提供模块接口，按实际 SDK 包和设备逐一接入 |
| 标准 LLRP 设备实机差异大（不同厂商实现偏差） | 用 L1~L4 能力分级 + 实测矩阵（冻结项目已验证 1.0.1 基本链路） |
| 能力驱动 UI 过度设计 | 首版只抽象实际需要的 EditorKind；天线、Filter、频率集合允许专用语义编辑器，不追求所有设置都由通用文本字段生成 |
| 厂商模块需要二次连接 | 把标准 Probe -> 模块匹配 -> 扩展连接作为显式激活流程，并记录两次连接各自的错误和耗时 |
| 能力或设置 Draft 过期 | RuntimeSnapshot 带 CapturedAt/CapabilityRevision；保存前复核，过期则拒绝并要求刷新 |
| 其他 UI 框架接入 | 通过 Contracts/Services 的稳定 DTO 和服务接口接入，不把 WPF 控件、Dispatcher 或 ViewModel 类型下沉到共享层 |
| 旧项目 Profile/Preset 是否需要保留 | 不纳入首个 WPF 消费者交付；新库从空数据目录开始 |
| 重构 Scope 膨胀 | 按 F1~F9 分批，每阶段有独立验收，不一次铺开 |

本轮边界收口：Reader 端点归一化已放入 Contracts，程序化添加、SQLite Profile、SDK Session Factory 和 WPF IPv6 展示共享同一 Host 规则，避免方括号地址在不同层产生重复注册或传输差异。

WPF 发现入口也已统一：主设备页和添加数据源页共享发现记录清洗、非法端口回退、空 Host 过滤、IPv6 展示和端点去重规则。

Virtual Reader 已作为独立开发替身接入：它复用同一 Session、ReaderManager、Settings、Inventory、Tag Memory、GPI/GPO 和 WPF 边界，不增加 WPF 特殊业务分支；详见 [Virtual Reader 开发模式](development/virtual-reader.md) 与 [ADR-0015](decisions/ADR-0015-virtual-reader-development-mode.md)。

### 7.1 平台级虚拟设备长期规划（未排期）

本节是长期架构方向，不属于当前发布、近期迭代或默认下一任务。现阶段继续使用已经实现的
`LLRP_VIRTUAL_SCENARIO` 单场景平台替身；报文级 TCP Virtual Device Manager UI 首版已经在当前
解决方案实现，2.0.0 起默认消费 SDK 的 Hosting 顶层 NuGet，不进入主客户端 Services；它与主客户端分开
作为独立 Windows 资产发布。
只有用户重新明确启动平台级 Data Source 工作包后，才按 VP1～VP6 执行。

虚拟设备分成两个互不混合的产品边界：

```text
LlrpReaderPlatform 主 WPF
├── TCP Reader Data Source（真机或外部报文虚拟设备）
└── Platform Virtual Reader Data Source（进程内 Session）

LLRPCSharp 独立 Virtual Reader Manager
└── 报文级 TCP/LLRP 虚拟设备
```

主 WPF 只维护平台级虚拟设备。报文级虚拟设备由相邻 SDK 仓库的独立 Manager
创建和管理，主 WPF 只按普通 Host/Port 添加，不保存其虚拟类型、预设或 Host 生命周期。

**用户入口与预设约束**：

- 平台虚拟设备在 Data Sources 的添加流程中选择内置预设和实例名称，不显示 Host/Port；
- 用户不能创建、编辑、复制或导入任意虚拟配方，能力、标签、GPIO 和故障行为由受测预设固定；
- 首批平台预设为 Standard 1.0.1、Strict Standard、高速 Inventory 和生命周期故障；
- 预设使用稳定 `PresetId`/`PresetVersion`，实例只持久化预设引用和用户意图；
- 厂商预设当前不交付，但 Catalog 从第一版采用 Contributor 注册。未来 Impinj/Zebra 平台预设
  通过独立模块贡献身份、能力、设置和报告行为，核心、Contracts 和 WPF 不增加厂商 `switch`；
- 未安装或版本不匹配的预设显示“模块不可用”并禁止启动，不静默降级成标准设备。

**平台实现阶段**：

| 阶段 | 范围 | 出口 |
|---|---|---|
| VP1 决策与契约 | 落实 [ADR-0016](decisions/ADR-0016-platform-virtual-reader-data-sources.md)；把 `ReaderProfile` 的端点改为 `TcpReaderSource`/`PlatformVirtualReaderSource` 有类型来源 | Contracts 不含 SDK/WPF/厂商类型；平台虚拟来源不要求 Host/Port |
| VP2 持久化与路由 | SQLite 保存 SourceKind、PresetId、PresetVersion、InstanceId；端点唯一索引只约束 TCP；增加 Routing SessionFactory | 同进程真实 Reader 与平台虚拟 Reader 可同时 Probe/Activate |
| VP3 预设目录 | 将当前场景中的实例身份与行为模板分离；标准预设也通过 Contributor 注册；Catalog 为每个 InstanceId 维护独立状态 | 同一预设可创建两个不串设置、GPIO、Tag Memory 的设备实例 |
| VP4 Data Sources UI | 添加来源选择；平台虚拟表单只显示名称、预设和启用状态；列表显示 `Platform Virtual · Preset` | 添加、启用、停用、删除和状态展示不依赖伪造端点 |
| VP5 启动恢复 | 应用先加载虚拟实例和预设，再恢复 ReaderManager；删除时按 Session→Catalog→SQLite 顺序补偿清理 | 应用重启后实例可恢复，缺失预设给出结构化错误 |
| VP6 首版验收 | 多平台虚拟、多真实/虚拟混合、设置、Inventory、Tag Memory、GPIO、Runs、错误状态 | WPF 全链路不增加页面级虚拟设备业务分支 |

长期项目布局沿用当前解决方案规则：所有产品项目位于 `src/`，所有 `*.Tests` 和 TestKit
位于顶层 `tests/`。未来厂商虚拟预设若需要独立程序集，产品模块放在
`src/LlrpReaderPlatform.VirtualReader.Extensions.*`，对应测试放在
`tests/LlrpReaderPlatform.VirtualReader.Extensions.*.Tests`；不得在 `src` 下嵌套测试工程。

`LLRP_VIRTUAL_SCENARIO` 在 VP2 后不再全局替换 SessionFactory，只保留为开发期创建或
导入单个平台虚拟实例的启动快捷方式。当前版本在 VP2 完成前仍是单场景开发模式，文档和 UI
不得把平台级 Data Source 规划能力表述为已经支持；这不影响独立报文级管理 UI 的已交付功能。

**跨仓库边界**：SDK 仓库负责把现有最小 `LlrpVirtualReader` 拆成可复用 Core、内置报文预设
与独立 Manager。Manager 用户只选择预设、设备名称和 TCP 监听地址/端口；端口被占用即失败，
不自动换端口。标准和未来厂商报文预设通过协议模块/Handler Contributor 扩展，设备端实现不
依赖客户端 `LlrpSdk.Extensions.*`。详细工作包和验收标准以 SDK 仓库 `docs/roadmap.md` 及对应 ADR 为准。

## 8. 文档关系

- 本规划为**新仓库中的共享服务框架 + WPF/MAUI Blazor 消费者**开发计划；WPF 仍是当前真机验收主客户端，Blazor 是共享服务的跨平台消费者；
- [冻结项目说明](legacy/README.md) 仅作为旧仓库地址和当前架构参考，不能替代本规划中的 Contracts、Infrastructure、扩展注册和测试契约；
- 现有实现的验证基线为标准 LLRP 1.0.1 和 Impinj R420；更多厂商/型号必须通过设备矩阵逐步提升支持等级；
- 本规划随开发推进持续更新（每阶段完成回填状态）。

## 9. 实施进度回填

> 按 AGENTS.md 约定在开发推进中回填。本仓库当前处于 P0～P8 软件首版交付、硬件现场验收持续进行状态；发布配置和 WPF 便携交付已收口，剩余工作主要是设备覆盖、物理事件和现场恢复证据。

| 阶段 | 内容 | 状态 |
|---|---|---|
| F1 | 骨架、契约、依赖、架构测试 | 完成 |
| F2 | ReaderManager 生命周期、单 Session/Gate、FakeSession | 完成 |
| F3 | 能力驱动设置模型（Compiler/SettingsService） | 完成 |
| F4 | 盘存、有界 Channel 聚合、ReaderBusy、TagAccess/GPI | 完成 |
| F5 | Impinj 扩展模块与两阶段匹配 | 完成 |
| F6～F8 | 首个 WPF 消费者（设备/设置/寻卡三页） | 完成首版：旧布局、原生 ProgressBar、Tab1 旧项目分组设置、Tab2 四路 GPO/GPI 状态、动态设置编辑器、实时 TagObserved、Tag Memory、Tag Lists、Inventory Runs、App Settings、About 均已接入真实服务；频率表已支持能力驱动的多选编辑，少量 L4 细节仍待增强 |
| F9 | 测试与真机验收 | 自动化测试 385 项全绿（含 Virtual Reader 场景/回放/Session/ReaderManager 链路、本轮日志模式、快照、WPF 操作日志、Zebra 扩展语义投影和架构边界回归）；真机已完成标准 Probe/Settings Query、Impinj 扩展连接、有界 Inventory Start/Stop/Disconnect、WPF Settings Apply、设备列表刷新期间设置查询不被取消、GPI/GPO、GPI 状态查询和 GPI debounce 回写、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复，以及按固件/SDK 能力画像完成 FastID/Phase/Search/Low Duty/固定频率 Apply/回读、FastID/Phase 扩展 TagReport 和 Doppler 隐藏；代码级 Connection Faulted、ReaderException、匹配 GPI Stop 触发器收敛与重新 Start、Activate/Inventory/短操作取消后的 Session 清理与取消后重新 Probe 恢复、旧 Session 迟到故障/GPI/定时停止事件和 TagReport 队列跨 Run 隔离已自动化验证；GPI/GPO 无能力查询的 Unsupported 降级已自动化验证；GPI 物理事件/触发、多 Reader、断网/重启现场恢复及其它 Memory Bank 写入仍待现场验收 |
| F10 | EF SQLite、启动恢复与日志 | Reader Profile、Reader Settings JSON 快照、TagList、InventoryRun、AppSettings、基础 EF Migration、启动恢复和 CRUD 测试已完成；早期 schema 变化允许清空数据库重建，不承诺历史数据兼容；WPF 已分离 ui/platform/sdk 日志并过滤 EF SQL，盘存支持 Off/FinalSnapshot/RawReports |
| F11～F14 | 完整设置、完整 Inventory、全应用迁移、多设备扩展 | 首版代码完成，自动化覆盖多 Reader 并行和设备异常生命周期；平台 Virtual Reader 已支持场景导入、确定性回放、跨 Session 状态和 WPF 显式开发模式；报文级 Virtual Device Manager UI 已完成首版并进入独立 Windows 发布资产；`LlrpReaderManager` MAUI Blazor 消费者已落地首版页面、响应式布局和 Virtual Device widget，Linux GTK4 Head 已复用同一套页面并进入 Ubuntu CI/`.deb` 发布资产；项目目标覆盖 Windows、Mac Catalyst、Android 和 Linux GTK4；硬件深度验收、平台签名和各平台故障恢复证据持续补齐 |
| F15 | 平台虚拟设备 Data Source 化 | 长期规划、未排期；已记录 ADR-0016 与 VP1～VP6，当前继续使用 `LLRP_VIRTUAL_SCENARIO` 单场景平台工厂模式；报文级管理 UI 不属于本阶段 |

**测试基线**：既有平台/WPF 基线为 `dotnet test LlrpReaderPlatform.slnx --no-build` 385 项全绿（Contracts 5、Services 194、Infrastructure 10、App.Wpf 134、Architecture 9、Extensions.Impinj 17、Extensions.Zebra 6、VirtualReader 10）；`LlrpReaderManager` Windows/Android 目标已以 0 警告、0 错误编译，Mac Catalyst 和 Linux GTK4 目标分别由 macOS/Ubuntu CI 构建验证；Linux Release 使用 `linux-x64` Runtime Identifier 生成 framework-dependent `.deb`，平台打包和真机验收仍按各自矩阵推进。

本轮补充的运行时边界：退出清理由注册表 Gate 与释放闸门统一保护，关闭期间拒绝晚到 Add/Probe；已知能力下拒绝超范围 Inventory 天线；Tag Access 选择条件拒绝 LLRP `ushort` 长度溢出；GPO 端口 0 在 WPF Tab2 入口拦截；Impinj GPI debounce 按能力快照的 GPI 数量生成和回写，明确为 0 的设备不发送不存在的端口配置。
Services/应用测试使用 `TestKit/FakeSession`，Infrastructure 测试使用内存 SQLite。

Tab2 状态投影补充：主窗口状态刷新会重复向 Diagnostics 投影同一 Reader 上下文；Diagnostics 仅在 Reader 或 GPIO 能力上下文变化时清空 GPI/GPO 状态，普通刷新保留已确认 GPO 和已收到的 GPI 事件表，避免 UI 状态被列表刷新错误重置。

能力上下文补充：设置页在重新捕获能力后重新计算可保存门禁；同一 Reader 能力版本或可用性变化时，设置 Query/Defaults 与 Tag Memory 的晚到结果会被丢弃，保留旧的离线只读回显但不把旧能力结果当作当前设备状态。

本轮运行时收尾补充：直接从寻卡页启动时，扩展探测取消会清理当前/候选 Session 并恢复为 `Disconnected`；`ActivateAsync`、短操作和 Inventory Start 在标准 Probe、连接或扩展 Session 替换阶段被取消时，也会回收并重建干净的标准 Session，标记能力快照陈旧，下一次操作重新探测；WPF 收到停止生命周期事件后会继续排空已进入有界 UI 队列的最后一批 TagObserved，避免服务端 Drain 与 UI 投影之间丢失尾部标签。

**已知待办**（供后续阶段）：
- F9 真机 LLRP 验收（GPI 物理事件/触发、其它 Memory Bank 写入、多 Reader 和故障恢复）；匹配 GPI Stop 触发器的服务层自动收敛已有自动化测试，故障 Session 回收/重建与重新 Start 也已有生命周期自动化测试，真实 TagReport、EPC/TID/User/Reserved Memory Bank 读取、Tag Access 写入恢复、Settings Apply、寻卡 Start→Stop、GPI/GPO、GPI debounce、FastID/Phase/Search/Low Duty/固定频率设置及 FastID/Phase 扩展 TagReport 已有 R420 验证记录；执行步骤见 [真机验收运行手册](development/hardware-validation-runbook.md)；
- `ImpinjReaderExtensionModule.ImpinjManufacturerId` 已按真机标准 Probe 校准为 0x651A，Impinj 扩展连接已实测通过；平台扩展字段投影已完成，R420 已实际观察到 `serializedTid`、RF phase 和 peak RSSI 字段；
- 标准设置布局已消费 TxPowers/RxSensitivities/RfModes，并将 Tx/Rx index 直接写回 SDK；多天线 RF、Gen2 Filter、state-aware、GPI 启停和标准报告字段已接入；Impinj FastID/Phase/Doppler/Search/Low Duty/Fixed Frequency/GPI debounce 通过扩展贡献点接入，R420 已完成 GPI debounce、FastID、Phase、Search、Low Duty、固定频率保存/回读和 FastID/Phase 扩展 TagReport；Doppler 按 SDK 能力画像隐藏。
- Tx/Rx 能力项同时向旧 WPF Tab1 提供“index（具体描述）”下拉选项，例如 `7 (30.5 dBm)` 和 `2 (6 dB offset)`；Draft、CompiledSettings 和设备写入值始终是能力表的 index，不再把显示的 dBm/offset 映射到邻近 index。未提供能力列表时才保留 index 范围文本编辑回退；Rx 的 dB offset 仅为描述，不是写入值。
- Settings Apply 编译器优先使用 Query 返回的 managed ROSpec Inventory 作为基线，避免设备把 Inventory 与 ReaderSettings 分开返回时保存单个字段导致 Tab1 其他配置回退；旧布局固定控件、扩展/频率/低占空比控件在对应能力项只读时同步禁用。
- Reader 激活失败时 WPF 仍打开设备设置页：`SettingsService` 在设备 Query 成功时缓存完整 Tab1 语义布局，随后优先投影新平台 SchemaVersion=2 结构化语义 Preset 为只读缓存，没有缓存或格式过旧才显示能力未就绪占位；`REFRESH SETTINGS`/`LOAD DEFAULTS` 会重试标准激活。SQLite 只维护新平台数据，旧库/旧扁平语义 JSON 不在兼容范围内，早期变更可清空数据库重建。
- Reader 的实际协商协议版本由 SDK 边界映射到 Contracts 的 Probe/Runtime/Capability 快照，并在设备设置页显示 LLRP 1.0.1 或 1.1；未知未来版本保留为未知，不把连接策略误当成设备已支持的协议版本。
- WPF 设备列表和设置页同时显示持久化连接策略、实际协商协议和连接生命周期；短操作完成后的 `Disconnected` 明确解释为“能力已同步，短连接已释放”，Inventory 运行时则显示长连接已建立，避免把正常资源释放误判为连接失败。
- 设置保存校验已收口：`SettingsService.Validate` 优先使用最近一次实时 Query/Defaults 的完整布局，覆盖 Tab1 的 Filter/GPI/Report/扩展项；标准或扩展编译阶段的十六进制、数值范围等异常统一转换为 `SettingsApplyResult`，WPF 只显示失败状态，不把编译异常直接抛出到窗口层。
- Tab2 的 GPO/GPI 短操作在 WPF 侧增加单操作忙碌状态和原生进度条；四路 GPO 开关允许按旧 WPF 交互快速连续切换并在 VM 内按输入顺序排队，GPI/GPO 刷新按钮在忙碌时禁用；服务层仍以 Reader Session Gate 作为最终串行化边界。

**已完成迭代**（服务层补齐，均有自动化测试）：
- `TagAccess`（读/写 TagMemory）的 Services 到 SDK 映射已实现（`SdkTagAccessMapper`：EPC TagSelection、WriteData word 列表、ReadData 大端 hex）；服务边界会在建立短连接前校验目标、密码、读字数和写入 word，避免用户输入错误被误报为设备故障；
- `ReaderCapabilityCapture.Antennas` 已从 `ReaderCapabilities.MaxNumberOfAntennas` 解析填充（`ReaderAntennaFactory`），设置页天线选项由能力驱动。
- `LlrpReaderSession` 已实现 SDK Settings Query/Default/Validate/Apply 和按 `InventorySettings` 启动盘存；ReaderManager 负责短租约/长租约边界。
- Tag Access 短连接在 Reader 将当前 Inventory 放入 managed ROSpec 时会沿用该配置；没有当前 ROSpec 时只借用 SDK 默认 Inventory，保留 Query 得到的当前 ReaderConfiguration/Extensions。
- SDK Adapter 的 `InventorySpec` 路径也会基于当前设置应用天线与报告字段覆盖，避免绕过 ReaderManager 的消费者得到“参数被忽略”的不一致行为。
- EF Core SQLite 已实现 Profile 与 Reader Settings JSON 快照；五类 SQLite Store 通过同一 `DbContextFactory` 维度的迁移闸门串行初始化 schema，同时保持每次读写使用独立 `DbContext`；WPF 已接入实时 `TagObserved` 有界 UI 队列、时长/速率/耗时和设备设置 Tab2 GPO/GPI 状态。
- InventoryRun 在 ROSpec 启动前建立运行上下文，Stop/断连/退出会等待已入队报告和 TagLog 写入完成后再落库，避免首批或尾批标签丢失。
- TagReport 聚合使用非阻塞 `TryWrite` + 容量 8,192 的有界 Channel；单消费者每批最多处理 512 条，原始报告只原位更新聚合状态，批次末尾按 Reader/EPC 创建一次快照并发布最新累计状态；满载时明确计数并丢弃新报告，Stop/断连仍会等待已入队报告完成；启用日志时使用独立有界 TagLog Channel 和单消费者串行写入，TagLog 仍保留每条原始读取的累计记录，不让业务日志关闭实时数据管线优化；WPF 按 EPC 合并最多 2,000 个待刷新项，以 250ms/25 行的节奏原地更新最多 1,000 个 DataGrid 行，状态栏显示服务层与 UI 层合并丢弃计数，并有高频回归测试覆盖。
- Reader 公开操作在等待单 Reader Gate 前先经过 registry gate，和 Remove 的清理顺序一致；同时将异常 SDK 时间戳降级为本地接收时间，避免一条畸形 TagReport 终止聚合消费；Activate/Start/Stop/Initialize 以及 Settings/Tag Access/GPIO 短操作的调用方取消会先完成连接/运行上下文清理，必要时重建干净标准 Session、标记能力快照陈旧，再原样传播 `OperationCanceledException`，不会误报为设备失败或复用半开连接。
- WPF 寻卡页的 Start/Stop/全局 Start/Stop 共享生命周期忙碌门闩和原生进度反馈，停止请求在首个停止完成前不会重复进入同一 Reader 的平台服务；设备主动断连仍由状态事件收敛 UI 运行状态。
- 直接从寻卡页启动时，ReaderManager 也会为离线恢复的标准 Session 执行一次标准 Probe → 扩展匹配 → 会话替换，避免必须先打开设备设置页才能启用 Impinj 等厂商协议扩展；设备列表把短连接激活后的 `Disconnected + 非陈旧能力` 投影为“已同步能力”。
- 直接从寻卡页启动成功后，同一长连接会刷新身份、天线能力、CapabilityRevision 和 FeatureCatalog，再进入 `Inventorying`；因此“寻卡先于设置”不会留下能力陈旧的运行时快照。
- Tag Memory、GPI/GPO 等短操作对启动恢复或故障后的陈旧 Session 也执行必要的标准 Probe、扩展匹配和能力捕获；页面不依赖用户先打开设置页，成功后仍按短租约断开并回到 `Disconnected`。

> 2026-08-21 平台发布流水线收口后，当前自动化基线以本文件 F9 表格为准：385 项全绿；新增
> `Extensions.Zebra.Tests` 6 项，Architecture.Tests 9 项。历史小节中的早期测试数字仅保留
> 为阶段记录，不代表当前基线。厂商 Feature 归属和 WPF 语义投影见 [ADR-0014](decisions/ADR-0014-vendor-feature-ownership-and-ui-semantics.md)。

### 9.1 多版本与多厂商推进计划（1.1 / 2.0 / 1.0.1+Zebra）

> 决策依据：[ADR-0011](decisions/ADR-0011-settings-page-dual-axis-gating.md)（一页 UI、双轴正交门控：标准轴按 `NegotiatedProtocolVersion`、厂商轴按 `ManufacturerId` + 能力画像）、[ADR-0012](decisions/ADR-0012-semantic-feature-keys-and-graduation.md)（语义能力键 + `StandardizedSince` 毕业机制 + 标准优先仲裁）与 [ADR-0013](decisions/ADR-0013-report-capability-ownership.md)（报告字段走寻卡页联动、数据能力留在设置页）。本小节是本仓库当前唯一的多版本推进计划，不另建第二份阶段计划。

**SDK 1.5.0 能力边界**（已核实，决定平台可做范围）：

- 三版本协议适配器（26 成员接口）齐全：Settings Query/Apply、Inventory、Tag Access、报告、事件均可经 SDK 托管层按协商版本下发；
- 块级 Tag Access：`BlockErase` 与块写（`useBlockWrite`）三版本托管；`BlockPermalock`/Recommission 未托管；
- `ReaderCapabilities.MaximumReceiveSensitivityDbm` 已托管（1.1/2.0 适配器填充；1.0.1 规范无此参数 → null），实际灵敏度 dBm = Max + 偏移；
- 2.0 安全 Tag Access（Authenticate 族）、标准 XPC 投影（C1G2XPCW1/W2）未托管；
- Zebra 扩展：`UseZebra()` + 7 个配置字段 + 6 个报告开关 + phase/gps/xpc 报告投影；SDK 自述可信度风险（官方 ICG 与固件字节系统性偏移，仅 FX9600 部分参数标定），平台按实验性门控。

**执行顺序**（阶段间依赖，每阶段结束保持构建 0 警告 0 错误与全量测试全绿；`✅ 已完成`= dev 分支首版代码/文档落地，`⬜ 待办`= 后续工作）：

1. ✅ **阶段 D：2.0 策略与版本模型**（完成）——Contracts 枚举增 `Force20`/`Version20`；`ReaderManager` 双向版本映射；`LlrpReaderSession` 策略映射；WPF 下拉增「LLRP 2.0」、Auto 文案统一为 “Auto”（协商链路为询问设备支持版本后取最高，不写死链路）；设备矩阵 2.0 标 `PendingHardware`。
2. ✅ **阶段 A：双轴门控与语义键基础设施**（完成，ADR-0011/0012）——`Feature` 增稳定语义键与 `StandardizedSince` 元数据（现有 Feature 全部补齐语义键）；版本作用域标准 Feature 按协商版本聚合；同语义标准优先仲裁唯一收口在 `ReaderFeatureCatalog`；守护测试覆盖语义键唯一、仲裁与版本过滤。
3. ✅ **阶段 C0：Rx 灵敏度实际值显示**（完成）——`BuildRxSensitivityOptions` 按 `MaximumReceiveSensitivityDbm` 双分支：非空显示 “offset (实际 dBm)”，null 保持 “offset dB offset”；写入值始终是能力表 index（与 Tx Power “33 (33 dBm)” 同款模式）；纯显示增强，补 WPF 回写测试。
4. ✅ **阶段 B：1.0.1+Zebra 扩展模块（实验性）**（完成）——新项目镜像 Impinj 四件套（本地 ProjectReference `LlrpNet.Protocol.Zebra`/`LlrpSdk.Extensions.Zebra`，NuGet 包双模式）；模块适用性 = 厂商 161 且 Version101；`UseZebra()`；报告投影 phase/gps/xpc 挂语义键；7+6 设置行仅 FX9600 画像（161/96008 + 固件 3.32.37.0）门控，未知 Zebra 只投影不给设置行；设置页 Zebra 分组标注 Experimental；寻卡页 Phase/GPS/XPC 三个可选列（默认隐藏，走列头选择器）；配套测试；未标定前不声明支持。
5. ✅ **阶段 C：块级 Tag Access**（完成，版本无关）——Contracts 增块擦除/块写请求模型与 `StandardBlockTagAccess` 能力；Services 映射 `BlockEraseTagRequest` 与 `useBlockWrite`；Tag Memory UI 操作区按能力显隐；R420 真机验收块擦除/块写后回填矩阵。
6. ✅ **阶段 E：厂商轴扩展性**（完成）——厂商模块 `IsApplicable` 统一带协商版本判定；新厂商接入步骤清单写入 [架构文档](architecture/extensions-and-settings.md)，为未来 1.1+其他厂商、2.0+厂商组合做准备。
7. ⬜ **阶段 F：现场验证收口**（自动化完成，现场待办）——本地 SDK 与 NuGet 2.0.1 双模式 build/test 全绿；R420、标准 Reader、Zebra 现场剩余项按 [真机验收运行手册](development/hardware-validation-runbook.md) 逐项标定；设备矩阵与文档持续回填。

**SDK 未托管项的三层纪律**（“上层先做、留接口给 SDK”）：Contracts 契约与语义键先落地；Services 只留方法挂点；UI 行按 Feature 隐藏，不得以“页面可操作”冒充完成。**SDK 缺口跟踪清单**：BlockPermalock/Recommission、2.0 安全 Tag Access、标准 XPC——SDK 托管后按语义键点亮，不做迁移。

**不在本轮**：1.1/2.0 专属参数的实机验收（无对应设备，矩阵保持 `PendingHardware`）；任何未经实机验收的支持声明。
### 9.2 寻卡联动上报设置（ADR-0013 落地）

> 决策：ADR-0013。实施进度：R1–R5 已完成（build 0 错误，测试全绿），R6 真机验证待设备现场。
> 寻卡联动上报的当前状态与后续现场项，以本主计划、[ADR-0013](decisions/ADR-0013-report-capability-ownership.md) 和[设备矩阵](compatibility/device-matrix.md)为准。

在初版三页 Tab 基础上，为对齐旧 `LlrpReaderStudio.Wpf` 的功能面已补充：

- **mDNS 设备发现**：`IReaderDiscoveryService`（`_llrp._tcp.local.`）扫描 + 选用回填 Host/Name/Port（对齐 `DiscoveredReaderViewModel`/`SelectDiscoveredDevice`）；
- **Tag 内存读/写页**：EPC/TID 目标匹配、Memory Bank、Word pointer / Word count、Access pwd、Data hex 与结果（对齐 `TagMemoryViewModel`）；
- **关于页**与**应用设置（Tag Logging）页**：对齐旧 `AboutViewModel` 与 `SettingsViewModel` 的只读展示；
- **寻卡标签表**：按旧项目保留 `#` 行号、EPC、TID、Count、First/Last Seen、Reader、Antenna、Peak RSSI、Channel、PC Bits 的列顺序；列头右键菜单提供列显示开关，Tag List 作为平台附加列保留；全局寻卡按 EPC 去重，Reader 列保留来源名称；启动栏提供按秒自动停止，`0` 表示持续运行，底部使用 WPF 原生 ProgressBar 反馈异步操作。
- **设置页编辑器**：Tab1 按旧项目双栏分组（Manual、Power/Sensitivity、GPI、Gen2 Filter、State-aware、Frequency、Low Duty、Report），补齐设备信息、Preset/Settings Origin、CANCEL、每天线 RF 展开区、GPI 四行矩阵和 Filter 1/2 双栏；组内仍按 SettingsEntry.EditorKind 渲染专用编辑器（Choice 下拉 / Boolean 开关 / Text 文本框），并缓存完整布局用于离线只读回显；Tab2 使用旧项目四路 GPO ToggleSwitch，并在同一 Tab 内提供 GPI 状态刷新，底层仍走平台 DiagnosticsViewModel/IInventoryService。
- **添加数据源**：补齐 LLRP 版本选择（Auto/1.0.1/1.1，随提交写入 ReaderProfile.LlrpVersion）；Probe 和提交结果都会回显标准协商协议、型号/固件和匹配扩展 Id，未匹配时明确显示标准 LLRP 路径；版本下拉在 WPF 中显示用户语义标签，内部仍保存协议策略枚举；
- **状态栏**：显示 Selected Reader 与 Unique Tags 计数（对齐旧底部状态栏）。
- **实时寻卡**：后台 TagObserved 不直接触碰 ObservableCollection，WPF 用 DispatcherTimer 分批投影；Start/Stop 为同一 Session 的完整连接生命周期。
- **标准设置**：动态行覆盖 Antenna、Session、Population、Report Every、RF Mode、Tari、Tx Power、Rx Sensitivity，并通过原生 WPF 编辑控件渲染。
- **SQLite 恢复**：应用启动调用 `IReaderManager.InitializeAsync` 从 EF SQLite 恢复 Profile，启用设备执行 Probe/激活，离线设备保留在列表。
以上均为 WPF 消费者层实现，全部通过 Contracts/Services 接口消费，ViewModel 不直接碰 SDK 或厂商类型；能力目录已随 Reader 运行时快照和设置布局发布，扩展模块可贡献稳定厂商能力标识和 TagReport 字符串字段，并按固件能力画像隐藏未验证的 L4 设置；标准 Tag Access 以 ReaderCapabilities 明确能力为准，设备报告不支持时服务和 Tag Memory 页均降级为不可用；标准 GPIO 端口数量优先来自 General Device Capabilities，解析器同时覆盖 LLRP 1.0.1 与 1.1，明确为 0 的 GPI/GPO 不生成可编辑触发器或控件，部分端口设备只启用实际存在的 GPO；若设备能力响应未声明端口数量，成功的 GPI/GPO 状态查询会按返回端口补充当前运行时快照，未知能力仍不被误判为物理接线通过；GPI/GPO 状态查询在设备明确无对应能力时返回稳定 `Unsupported`，不把能力缺失误报为连接故障；Tab1 的旧固定分组和 Tab2 的 GPO/GPI 区域也按语义行、实际端口能力隐藏空控件；Inventory 的手动停止、GPI 触发、定时结束、设备断连和异常均通过平台 `LifecycleChanged` 事件统一收敛 WPF 状态，Inventory Runs 在选中 Reader 的运行记录完成落库后自动刷新，设备列表提供 Faulted Reader 的重新连接/能力刷新入口；Reader 探测/添加/激活、Settings、Tag Access、Inventory 失败状态由稳定的 `PlatformErrorCode` 投影为 WPF 文本，其中持久化失败、重复注册和平台注册失败可区分显示，不解析服务层错误字符串；SQLite 仅负责新平台自身数据，早期 schema 变化允许清空数据库重建；Settings Preset 的版本化语义 JSON 同时承载 Inventory 字段，不引入旧库导入；短连接查询若断开失败会把设置布局转为只读并要求重新激活；设置页在能力快照过期或 Reader 故障时禁用编辑和保存门禁；添加数据源页的 Host/Port 校验、发现记录归一化（重复端点、非法端口、IPv6 展示）、Probe/发现/提交互斥和发现条目输入门禁，离开页面会取消在途操作；WPF 全部页面的在途网络/数据库操作在窗口退出时由页面生命周期取消；应用设置页显示并持久化盘存数据记录模式和原始报告目录；Tag List 保存/删除会通过 WPF 变更事件即时刷新现有 Inventory 行的名称，不重启 Reader 生命周期；能力解析使用整数索引避免 `ushort` 能力上限回绕；Inventory 服务入口拒绝无效时长、重复天线和混合全部天线/指定天线参数；设备列表重建期间保持选中 Reader，避免取消在途设置查询；当前构建 0 警告 0 错误，自动化测试 385 项全绿。

## 10. 报文级虚拟设备管理 UI

`src/LlrpVirtualDevice.App.Wpf` 是解决方案中已完成首版的辅助 WPF 工具，主要用于没有真机时提供可抓包、可连接、
可重复启动的 TCP/LLRP 虚拟 Reader。它不是 `LlrpReaderPlatform.App.Wpf` 的第二套客户端业务实现，
也不消费平台的 `ReaderManager`、TOI 或真实 Reader SQLite 数据。

### 10.1 管理边界

- 一个管理器实例可以维护多个虚拟 Reader，每个实例拥有自己的 TCP 监听地址、端口、能力档案和 Host 生命周期；
- UI 可查看设备概览、连接客户端和已观察到的 LLRP 报文；真实客户端通过 IP/端口把它当作普通 LLRP Reader；
- Tag Pool 是虚拟设备的盘存数据源，和客户端 TOI 完全分离；当前支持添加标签、快速生成 20 张和删除所选标签；
- Tag Pool 还提供 Static/MovingTags/Noisy 场景、读取概率和 RSSI 抖动配置；虚拟 Reader 运行中这些配置锁定，停止后可修改并保存；
- 下次点击运行时释放旧 Host，按已保存的 `Config.Tags` 重建 Host；事件与故障注入页目前只是交互入口，尚未实际向 Host 下发注入事件；
- 管理器配置保存在 `%LocalAppData%\\LlrpVirtualDeviceStudio\\virtual-devices.json`，虚拟设备本身不另建运行时数据库。

### 10.2 SDK 边界

管理 UI 只负责交互、实例配置和 Host 生命周期；TCP 监听、LLRP 报文编解码、设备能力档案和标签盘存行为由
相邻 `LLRPCSharp` SDK 提供。2.0.0 起正式构建使用 `LlrpDevice.Virtual.Hosting` 顶层
NuGet 包；跨仓库开发联调仍可显式切换为 SDK 项目引用，其他依赖由 NuGet 传递解析。

### 10.3 `LlrpReaderManager` Blazor 消费者

`src/LlrpReaderManager` 是一个独立的 MAUI Blazor Hybrid 应用，不替代 WPF，也不复制 Reader
生命周期逻辑。当前首版页面覆盖 Reader 添加/发现、设备设置、Inventory、Tag Memory、Inventory Runs、
Tag Lists 和 GPI/GPO；页面通过 `ReaderManagerState` 消费平台 Contracts/Services，使用同一套
Probe、Activate、Session Gate、短操作和 Inventory 生命周期。

报文虚拟设备在该应用中只是 Reader 页面上的挂件：用户选择 SDK 能力预设、端口和名称后，应用启动
`LlrpDevice.Virtual.Hosting` 的 loopback TCP Host，再把实际绑定的 IP/端口作为普通 `ReaderProfile`
加入 `IReaderManager`。停止挂件时按“停用 Reader → 移除注册 → 释放 Host”顺序收尾；不会把虚拟 Host
类型泄漏到 Contracts 或 Services。

布局沿用归档 `LLRPReaderManagement` 的深色顶栏、侧边导航、卡片和绿色强调色，并通过 CSS media
queries 在桌面双栏和手持设备窄屏之间切换。该消费者当前属于跨平台首版，Windows、Mac Catalyst、
Android 和 Linux GTK4 已纳入独立发布流水线；Linux 通过独立 Head 生成 framework-dependent `.deb`，
签名、安装和真机适配仍按单独验收矩阵推进。
