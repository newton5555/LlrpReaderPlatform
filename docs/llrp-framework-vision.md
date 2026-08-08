# LlrpReaderPlatform 应用框架与首个 WPF 消费者开发计划

> 状态：规划稿（2026-08，面向新仓库）
> 基线仓库：`LlrpReaderStudio` 将冻结，不作为新项目的 ProjectReference 或运行时依赖；仅作为已验证行为、迁移经验和测试样例的参考。
> 目标：在新仓库中建设可被多个 UI 消费者复用的 LLRP 应用框架。第一个消费者是新的 `LlrpReaderPlatform.App.Wpf`，未来可增加其他 UI 框架，而不复制设备生命周期、能力判断和协议编译逻辑。
> 当前验证基线：现有项目已验证标准 LLRP 1.0.1 设备和 Impinj R420；新项目以此为回归基线，逐步扩展到更多 LLRP 设备和厂商能力。

## 0. 目标与定位

- 新建**厂商无关的 LLRP 应用服务层（独立类库）**，作为正式产品长期维护；
- 新建 **WPF UI 应用**（`App.Wpf`），作为第一个消费者；未来其他 UI 框架复用相同的 Contracts/Services/Infrastructure；
- 依赖底层 `LlrpSdk`（标准 LLRP 核心 + 厂商扩展架构），**不重造协议层**；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过扩展模块接入；
- 服务层、Contracts 和 Infrastructure 不引用 WPF 或其他具体 UI 框架；WPF 只是第一个 UI 适配层，未来其他 UI 框架通过相同的服务契约接入。

## 1. 关键前提（已核实）

| 前提 | 事实 |
|---|---|
| 底层 SDK | `LlrpSdk 1.2.0` 包含标准 LLRP API 和 `LlrpSdk.Extensions.Abstractions.dll`；Abstractions 是 SDK 包内程序集，不是额外 NuGet 包 |
| SDK 引用方式 | Services 直接引用 `LlrpSdk 1.2.0`；Impinj 模块额外引用 `LlrpSdk.Extensions.Impinj 1.2.0`。Seuic 或其他厂商包需在实际接入时单独验证 |
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
├── LlrpReaderPlatform.Contracts/         ★ UI 无关的公开模型、状态和设置契约
│     目标框架：net10.0；不引用 WPF、SDK 或厂商扩展
├── LlrpReaderPlatform.Services/          ★ 应用服务层（独立类库，正式产品）
│     定位：多个 UI 消费者共享的生命周期、能力、设置和盘存服务
│     目标框架：net10.0；依赖 Contracts + LlrpSdk (PackageReference)
│     ├── Lifecycle/      Reader 注册 / 激活 / 短连接 / Enable 语义
│     ├── Settings/       能力驱动设置模型（ReaderFeatureCatalog / EffectiveSettingsLayout / SettingsCompiler）
│     ├── Inventory/      标准 Inventory / TagReport / TagAccess 协调层
│     ├── Capabilities/   ReaderCapabilities 内存缓存（不从 DB 持久化）
│     ├── Modules/        IReaderExtensionModule 抽象和模块注册
│     └── Persistence/    Profile/Snapshot/操作接口，不放具体 SQLite 实现
├── LlrpReaderPlatform.Infrastructure/   ★ 持久化、发现、日志等基础设施实现
│     目标框架：net10.0；依赖 Contracts + Services + EF Core/SQLite/Zeroconf 等
├── LlrpReaderPlatform.Extensions.Impinj/ ★ Impinj R420 首个扩展模块
│     目标框架：net10.0；依赖 Contracts + Services + LlrpSdk.Extensions.Impinj
├── LlrpReaderPlatform.App.Wpf/          ★ 第一个 WPF 消费者
│     目标框架：net10.0-windows；依赖 Contracts + Services + Infrastructure + 已启用的扩展模块 + WPF/MVVM/UI 库
│     ├── Views/          页面视图（纯 UI）
│     ├── ViewModels/     页面状态 / 命令（只消费服务层，不直接碰 SDK）
│     ├── Messages/       ViewModel 间消息
│     ├── Converters/     值转换
│     └── Assets/         图标等
├── LlrpReaderPlatform.Services.Tests/   xunit + FakeSession/TestKit
├── LlrpReaderPlatform.Extensions.Impinj.Tests/
├── LlrpReaderPlatform.App.Wpf.Tests/     ViewModel/DataTemplate/DI 冒烟测试
├── LlrpReaderPlatform.Architecture.Tests/ 依赖方向和公开 API 边界测试
└── LlrpReaderPlatform.TestKit/           可控的虚拟 Session/Reader 测试替身
```

**设计铁律**：
- Contracts 是 UI 与服务之间的稳定边界，**不暴露任何 `LlrpSdk`、`LlrpNet.Protocol`、WPF 控件、Dispatcher 或 ViewModel 类型**；
- Services 只依赖 Contracts 和 `LlrpSdk`（不依赖 UI 框架）；`LlrpSdk.Extensions.Abstractions` 由 `LlrpSdk` 包提供，不单独添加 PackageReference；服务层本身不引用 `UseWPF`；
- 服务层**不直接依赖** `LlrpSdk.Extensions.Impinj/.Seuic`，厂商能力通过注册 `IReaderExtensionModule` 加载，避免冻结项目中 Core 直接依赖 Impinj 类型的问题；
- Infrastructure 负责 Profile、Snapshot、Preset、发现和日志等外部资源；Services 只依赖接口；
- 每个 UI 应用只负责展示与交互，设备生命周期/能力聚合/设置编译全部在共享服务层；**ViewModel 不直接碰 SDK**，也不使用 Service Locator 或直接 new Service/ViewModel。
- 共享 DI 注册由 `AddLlrpReaderPlatform()`、`AddLlrpInfrastructure()` 和 `AddImpinjExtension()` 等扩展方法提供；各 UI 只在自己的组合根选择并注册模块。

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

- **短连接**：启动/新增/设置/Tag Access/GPO 操作用完即断，仅寻卡运行时保持连接（沿用冻结项目已验证方向）；这些操作必须由服务层提供统一的 `RunWithConnectionAsync`/连接租约，不能由各个 ViewModel 手动 Connect/Disconnect；
- **Enable=true**：启动时自动激活同步到缓存，不保持 Session（冻结项目中已验证的语义）。
- `ReaderManager` 实现 `IReaderManager`，并在每个 UI 应用组合根中注册为 Singleton；UI 层通过接口访问 Reader，不复制设备生命周期逻辑。
- **TCP 独占**：每个 `ReaderHandle` 在任一时刻最多拥有一个活动 Session，并使用独立异步 Gate 串行化 Probe 之外的 Connect、Settings、Inventory、Tag Access、GPO、Disable 和 Remove 操作；
- **操作冲突策略**：Inventory 持有长连接租约。Inventory 运行时，Settings、Tag Access 和 GPO 默认返回明确的 `ReaderBusy` 结果，不隐式停止盘存；调用者必须先显式 Stop。Disable/Remove 可以取消当前操作，随后停止 Inventory、断开并释放 Session；
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
    // 第一次标准 Probe 后匹配模块；返回 NotMatched/Matched/ForcedButUnavailable 等明确结果
    ExtensionMatchResult Match(ReaderExtensionProbeContext context);
    // 在创建/连接 SDK Reader 前注册协议扩展，例如 Impinj 的 Builder 配置
    void ConfigureReader(ReaderBuilderContext context);
    // 第二次扩展连接后，解析能力/设置并参与保存编译
    void ContributeCapabilities(ReaderExtensionReadContext context, IReaderCapabilityBuilder builder);
    void ParseSettings(ReaderExtensionReadContext context, ISettingsSnapshotBuilder builder);
    void ContributeSettings(ReaderExtensionReadContext context, ISettingsCatalogBuilder builder);
    void CompileSettings(SettingsDraft draft, ReaderExtensionCompileContext context,
        ICompiledSettingsBuilder builder);
    // 将 SDK TagReport 中的扩展字段投影到 UI 无关 DTO
    void ProjectTagReport(ReaderExtensionTagContext context, ITagReportBuilder builder);
    void ContributePresetSerialization(IPresetSerializerRegistry registry);
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

**技术栈**：WPF + `CommunityToolkit.Mvvm`（`[ObservableProperty]`/`[RelayCommand]`）+ `MahApps.Metro`（窗口/控件/ProgressRing）。沿用冻结项目已验证的组合，不引入新 UI 库。

**页面结构（ViewModel-first 导航）**：

```text
Views/  +  ViewModels/
├── DataSourcesView / DataSourcesViewModel      设备列表：名称/端点/开关(Enable)/状态/删除/新增入口
├── AddDataSourceView / AddDataSourceViewModel  新增：Host/Name/Port/LLRP版本/厂商选项
├── ReaderSettingsView / ReaderSettingsViewModel 能力驱动设置页（由 EffectiveSettingsLayout 生成）
├── InventoryView / InventoryViewModel           寻卡：Start/Stop、读速率/唯一tag计数、Tag表格
├── TagMemoryView / TagMemoryViewModel           Tag 读写
├── DiagnosticsView / DiagnosticsViewModel       GPO/状态诊断
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
- 启动/同步时忙碌遮罩（MahApps ProgressRing）。

## 4. 与冻结项目（LlrpReaderStudio）的迁移边界

**迁移原则**：旧仓库冻结后只作为参考，**新仓库不引用旧项目**；能证明与厂商无关的实现可以迁移，涉及 Impinj 类型、旧 ViewModel 和旧持久化耦合的部分必须重写：

| 冻结项目项 | 迁移方式 |
|---|---|
| ReaderFleetService 生命周期 | **选择性迁移**，提炼为 ReaderManager，并改为可被多个 UI 消费者调用的服务 |
| 短连接 / Enable 语义 | **直接沿用**（已修正文档语义） |
| 有界 Channel 防卡死 | **直接沿用**（泵线程入队 + 后台聚合） |
| capabilities 内存缓存 | **直接沿用** |
| per-reader 状态隔离 | **直接沿用**（字典 ReaderId→Handle） |
| Inventory / TagMemory / Diagnostics 页面 | **可迁移**，随 UI 重构落位 |
| DataSourceSettingsViewModel（1613 行） | **不迁移**，重写为能力驱动设置模型（EffectiveSettingsLayout） |
| MainWindow/导航/MahApps 风格 | 可沿用主题与导航模式，结构重构 |
| LlrpSdk 引用 | 新项目使用 PackageReference；不引用旧项目的 DLL 或源码 |
| SQLite Profile/Preset | 默认由新仓库建立新数据目录；如需导入旧 `studio.db`，作为独立的数据迁移任务，不混入 F1～F5 |
| mDNS Discovery/日志 | 迁移到 Infrastructure；不放入 WPF ViewModel |

## 5. 分阶段实施计划

### F1：新仓库骨架、契约与依赖验证（1~2 人日）
- 建 `LlrpReaderPlatform.slnx`、Contracts、Services、Infrastructure、App.Wpf、Tests 和 Impinj 扩展项目；
- 固定 Contracts/Services/Infrastructure/Extensions 为 `net10.0`，App.Wpf 为 `net10.0-windows`；
- `Services` 只添加 `PackageReference LlrpSdk`；确认 `LlrpSdk.Extensions.Abstractions.dll` 从该包提供，不添加不存在的独立包；
- 在 Contracts 定义 `IReaderManager`、`ReaderRuntimeSnapshot`、Settings Layout/Snapshot/Draft、`IReaderSettingsService` 和状态 DTO；在 Services 内部定义 `CompiledSettings`；
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
- 标准盘存、TagReport 有界 Channel 聚合、TagAccess、GPI/GPO。

### F5：Impinj R420 扩展模块（3~5 人日）
- `LlrpReaderPlatform.Extensions.Impinj` 独立项目，并通过 `AddImpinjExtension()` 由宿主组合根显式注册；
- 迁移 Search Mode/FastID/Phase/Doppler/定频/Low Duty/GPI debounce/Preset/TagReport 投影。
- 实现 Match/ConfigureReader/ParseSettings/CompileSettings/ProjectTagReport，并验证两阶段连接；
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

### F8：Inventory / TagMemory / Diagnostics UI（3~4 人日）
- 寻卡页（Start/Stop、速率/计数、Tag 表格批量渲染）；
- Tag Memory 读写页；
- GPO/状态诊断页。

### F9：测试与实机验收（3~5 人日，取决于设备可用性）
- 使用 `LlrpReaderPlatform.TestKit` 的 FakeSession 覆盖生命周期、设置验证、扩展选择和错误回滚；
- Services.Tests 不依赖真实网络，Extensions.Impinj.Tests 覆盖模块注册、探测和设置编译；
- 覆盖补偿式 Add、单 Session/Gate、ReaderBusy、CapabilityRevision 过期和扩展两阶段连接；
- App.Wpf 做 DI 创建、页面 DataTemplate 和关键 ViewModel 状态投影的冒烟测试；
- Impinj R420 与标准 LLRP 1.0.1 设备分别及同时运行；
- LLRP 1.1 只有在取得真实 1.1 设备后才提升为已支持能力，否则标记为待验收。

## 6. 验收标准

- Impinj R420 达到 L1～L4；至少一台标准 LLRP 1.0.1 设备达到 L1～L2，L3 功能按设备能力逐项记录验收结果；
- R420 和标准 LLRP 1.0.1 设备可分别及同时运行；每台 Reader 只有一个活动 TCP Session；
- 未知厂商设备：至少连接、显示身份/能力、执行标准盘存，不出现或发送 Impinj 参数；
- 能力驱动 UI：不同能力快照生成不同语义布局，无法选择或发送不支持参数；CapabilityRevision 过期时拒绝保存并要求刷新；
- Settings UI 只提交 `SettingsDraft`，不直接调用 Compiler，不引用 SDK 或厂商类型；
- Contracts/Services/Infrastructure 不引用 `UseWPF`；架构测试阻止 Contracts 暴露 SDK/WPF 类型，未来其他 UI 框架可复用；
- 短连接、单 Session、异步 Gate 和 ReaderBusy 由服务层统一管理，ViewModel 不直接 Connect/Disconnect；
- 设置页由 `EffectiveSettingsLayout` 驱动，无冻结项目的 1613 行单 VM。

## 7. 风险与对策

| 风险 | 对策 |
|---|---|
| `LlrpSdk.Extensions.Abstractions` 被误认为独立包 | 由 `LlrpSdk 1.2.0` 提供程序集；Services 只引用 `LlrpSdk` |
| Seuic 等厂商扩展未发 NuGet | 不在首版验收范围；服务层只提供模块接口，按实际 SDK 包和设备逐一接入 |
| 标准 LLRP 设备实机差异大（不同厂商实现偏差） | 用 L1~L4 能力分级 + 实测矩阵（冻结项目已验证 1.0.1 基本链路） |
| 能力驱动 UI 过度设计 | 首版只抽象实际需要的 EditorKind；天线、Filter、频率集合允许专用语义编辑器，不追求所有设置都由通用文本字段生成 |
| 厂商模块需要二次连接 | 把标准 Probe -> 模块匹配 -> 扩展连接作为显式激活流程，并记录两次连接各自的错误和耗时 |
| 能力或设置 Draft 过期 | RuntimeSnapshot 带 CapturedAt/CapabilityRevision；保存前复核，过期则拒绝并要求刷新 |
| 其他 UI 框架接入 | 通过 Contracts/Services 的稳定 DTO 和服务接口接入，不把 WPF 控件、Dispatcher 或 ViewModel 类型下沉到共享层 |
| 旧项目 Profile/Preset 是否需要保留 | 默认新仓库新数据目录；旧 `studio.db` 导入单独评估和验收 |
| 重构 Scope 膨胀 | 按 F1~F9 分批，每阶段有独立验收，不一次铺开 |

## 8. 文档关系

- 本规划为**新仓库中的共享服务框架 + 第一个 WPF 消费者**开发计划；
- [冻结项目说明](legacy/README.md) 仅作为旧仓库地址和当前架构参考，不能替代本规划中的 Contracts、Infrastructure、扩展注册和测试契约；
- 现有实现的验证基线为标准 LLRP 1.0.1 和 Impinj R420；更多厂商/型号必须通过设备矩阵逐步提升支持等级；
- 本规划随开发推进持续更新（每阶段完成回填状态）。
