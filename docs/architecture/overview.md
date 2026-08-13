# 架构总览

## 目标

将 LLRP 协议能力、Reader 生命周期、能力模型、设置编译和厂商扩展集中在共享服务层，让不同 UI 只负责交互和展示。

## 分层

```text
UI Consumer
  -> Contracts
  -> Services
       -> LlrpSdk NuGet packages (default)
       -> local LLRPCSharp SDK projects (optional development override)
       -> Registered Extension Modules
       -> Persistence / Discovery interfaces
  -> Infrastructure implementations
```

实际依赖以项目引用为准：

- `Contracts`：UI 无关 DTO、状态、设置编辑模型和服务接口；不引用 SDK 或 WPF；
- `Services`：Reader 生命周期、连接租约、能力聚合、设置服务、盘存和 Tag Access；引用 `Contracts` 与 LlrpSdk，默认使用 NuGet、本地联调时使用条件项目引用；标准设置编译器通过 `ISettingsExtensionContributor` 接收已注册扩展的语义贡献；
- `Infrastructure`：SQLite、Preset、Profile、发现和日志实现；实现 Contracts/Services 定义的接口；
- `Extensions.*`：厂商模块，引用 Services 与对应本地 SDK 扩展项目；
- `App.*`：UI 组合根和展示层，注册 Services、Infrastructure 和扩展模块。

## 解决方案结构

```text
LlrpReaderPlatform.slnx
├── LlrpReaderPlatform.Contracts
├── LlrpReaderPlatform.Services
├── LlrpReaderPlatform.Infrastructure
├── LlrpReaderPlatform.Extensions.Impinj
├── LlrpReaderPlatform.App.Wpf
├── LlrpReaderPlatform.Services.Tests
├── LlrpReaderPlatform.Extensions.Impinj.Tests
├── LlrpReaderPlatform.App.Wpf.Tests
├── LlrpReaderPlatform.Architecture.Tests
└── LlrpReaderPlatform.TestKit
```

依赖规则：

```text
Contracts <── Services ──> LlrpSdk (NuGet default / local projects optional)
    ▲             ▲
    │             ├── Extensions.Impinj ──> Impinj SDK package/project
    │             └── Infrastructure
    └── App.Wpf ──┘
```

- `Contracts` 和 `Services` 使用 `net10.0`；`App.Wpf` 使用 `net10.0-windows`；
- `Contracts` 不引用任何 UI、SDK 或厂商程序集；
- `Services` 不设置 `UseWPF`，只依赖 Contracts 和 SDK 适配项目；
- Infrastructure 不把 SQLite、Zeroconf 或日志实现暴露给 Contracts；
- `Extensions.Impinj` 不得被 Services 反向引用；
- Services 内的 `Persistence/` 只放接口和边界模型，SQLite Entity、EF 配置和迁移放在 Infrastructure；
- Infrastructure 的各 SQLite Store 通过同一 `DbContextFactory` 维度的迁移闸门串行初始化 schema，查询和写入仍各自使用独立 `DbContext`；
- SDK 类型到 Contracts DTO 的转换集中在 Services/SDK Adapter 内部。

默认构建使用中央版本管理的 SDK NuGet 包；本地 SDK 联调通过 `UseLocalLlrpSdk=true` 和
`LlrpSdkSourceRoot` 显式启用，不通过长期分支维护。该边界见
[ADR-0008](../decisions/ADR-0008-switchable-sdk-references.md)。

## UI 消费者边界

`LlrpReaderPlatform.App.Wpf` 是第一个 UI 消费者，采用 WPF、CommunityToolkit.Mvvm 和 MahApps.Metro。

WPF 可以拥有自己的 View、ViewModel、DataTemplate、Dispatcher、导航和窗口行为，以及 WPF 控件到 `EditorKind` 的映射。这些内容不得下沉到 Contracts 或 Services。

未来 Avalonia、WinUI、MAUI 或其他 .NET UI 消费者复用 Contracts DTO、`IReaderManager`、`IReaderSettingsService` 等服务接口，以及 Services 和已注册的 Infrastructure/Extension 实现。它们不需要复刻 Reader 生命周期、能力判断、设置编译或厂商分支。

每个 UI 应用拥有自己的组合根，但使用相同的 `AddLlrpReaderPlatform()`、`AddLlrpInfrastructure()` 和扩展注册方法。持久化契约（包括应用设置、Tag List 和 Inventory Run）通过组合根注入，ViewModel 不自行创建 Store。UI 应用只决定使用哪些模块和具体 UI 技术，不改变共享层的设备语义。

## 核心原则

1. 标准 LLRP 是基础路径，厂商能力是可注册扩展。
2. Contracts 不暴露 SDK 类型、WPF 类型或厂商类型。
3. 一个 Reader 由一个 ReaderHandle 持有一个活动 TCP Session。
4. 连接、设置、盘存和断开由 Services 串行协调，UI 不直接操作 Session。
5. 故障或断开不可靠的 Session 不参与后续操作；Services 会在恢复时回收旧 Session 并创建干净 Session。
6. 能力等级来自实际能力和测试结果，不由厂商名称推断。
7. 服务结果保留面向用户的文本，同时用 Contracts 的 `PlatformErrorCode` 表达 Reader 探测/激活/添加、`ReaderBusy`、`DeviceFailed`、`Unsupported`、`StaleCapability`、`InvalidSettings`、`PersistenceFailed`、`AlreadyExists` 和 `RegistrationFailed` 等稳定语义，其他 UI 不需要解析本地化文本。

## 最终交付的实现顺序

WPF 只是最终消费者，完整功能必须按以下顺序建设：

```text
Contracts
  → SDK Adapter
  → ReaderManager / Settings / Inventory Services
  → EF Core SQLite / Discovery / Logging
  → Extensions.Impinj and future vendor modules
  → WPF ViewModels and Views
  → real-device qualification
```

Inventory 是一次完整的长连接租约：Start 建立唯一 Session，运行期间持续接收 TagReport，Stop 停止 Inventory/ROSpec 后断开同一 Session。Settings、Tag Access、GPO 是短连接操作，Inventory 运行期间不得抢占连接。
