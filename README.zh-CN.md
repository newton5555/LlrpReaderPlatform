# LlrpReaderPlatform

<p align="center">
  <img src="src/LlrpReaderPlatform.App.Wpf/Assets/LlrpReader_Pro_Icon.png" alt="LlrpReaderPlatform" width="144" />
</p>

<p align="center">
  <strong>面向 LLRP RFID 读写器的操作端应用与可复用服务层</strong>
</p>

<p align="center">
  版本 2.0.3 · .NET 10 · <a href="README.md">English</a>
</p>

<p align="center">
  <a href="https://newton5555.github.io/LlrpReaderPlatform/"><strong>📖 访问官方文档与多平台操作手册站点 (GitHub Pages)</strong></a>
</p>

LlrpReaderPlatform 是构建在 [LLRPCSharp](https://github.com/newton5555/LLRPCSharp) SDK 之上的应用平台。它提供可复用的 Reader 管理服务、持久化、厂商模块和多种 UI 消费者，用于发现读写器、编辑能力驱动设置、执行盘存、访问标签内存、控制 GPIO 和查看盘存历史。

当前主要现场客户端是 Windows WPF 应用。MAUI Blazor Hybrid 客户端在 Windows、Android 和 Mac Catalyst 上复用同一套平台服务，Linux 则使用独立的实验性 GTK4 Head。仓库还包含一个独立 WPF 管理器，用来操作 LLRPCSharp 提供的报文级虚拟 Reader。

这是应用仓库，不是第二套 LLRP SDK。协议编解码、传输、托管 Reader 行为和 TCP 虚拟设备运行时来自 LLRPCSharp NuGet 包，或者开发时显式启用的本地源码引用。

## 仓库中的应用

| 应用 | 用途 | 当前定位 |
|---|---|---|
| **LlrpReaderPlatform.App.Wpf** | 面向真实或 TCP 虚拟 Reader 的完整桌面操作客户端 | 主客户端与当前真机验收入口 |
| **LlrpReaderManager** | 响应式 MAUI Blazor Hybrid Reader 客户端 | Windows、Android 和 Mac Catalyst 上的共享服务消费者 |
| **LlrpReaderManager.Linux** | 承载相同 Blazor 页面与平台服务的 GTK4 Head | 实验性 Linux x64 路径；纳入 CI，并以 Framework-dependent Debian 包发布 |
| **LlrpVirtualDevice.App.Wpf** | 创建和管理 TCP/LLRP 虚拟 Reader 实例 | 独立辅助工具；不复用真实 Reader 的 Session 管理器 |

冻结的旧 **LlrpReaderStudio** 仓库只用于迁移参考，不是运行时依赖。

## 核心能力

- **Reader Fleet 生命周期**——发现、手动注册、Probe、启用/停用、激活、删除、状态快照和故障恢复。
- **能力驱动设置**——根据活动 Reader 的能力快照生成 RF Mode、功率、灵敏度、天线、Gen2 Session/Population、Filter、Report、GPI Trigger、GPO 状态和适用的厂商设置。
- **盘存**——支持单台或多台 Reader、长连接 Session、明确停止原因、有界聚合、EPC/TID/RSSI/天线/信道/时间字段、可选 Raw JSONL 日志和最终盘存快照。
- **Tag Access**——平台层提供 EPC、TID、User 和 Reserved Bank 的 Read/Write 工作流，并按 Reader 上报能力门控。
- **GPIO**——设备具备对应端口时，提供 GPI 状态与事件、GPI 触发盘存停止和 GPO 控制。
- **本地持久化**——EF Core SQLite 保存 Reader Profile、设置 Preset、Tag List、Inventory Run 和应用设置。
- **厂商模块**——Impinj 是维护中的主扩展路径；Zebra 已接入实验性模块，仍等待更完整的真机标定。
- **诊断**——分层的应用/服务/SDK 日志、稳定平台错误码、盘存历史，以及独立虚拟设备 Packet Inspector。

## 架构

![LlrpReaderPlatform 架构](docs/assets/architecture.svg)

核心层与 UI 无关：

~~~text
应用组合根
  -> Contracts
  -> Services -> Contracts + LlrpSdk
  -> Infrastructure -> Services + Contracts
  -> Extensions.* -> Services + Contracts + 厂商 SDK 包
~~~

- **Contracts** 包含不可变 DTO、能力与设置语义、稳定错误码、持久化合同和公开服务接口，不依赖 WPF、SDK 或厂商类型。
- **Services** 负责 Reader 生命周期、Session Lease、设置编译、盘存、Tag Access、GPIO、扩展解析，以及从 SDK 对象到平台合同的投影。
- **Infrastructure** 实现 SQLite 持久化、Zeroconf 发现、日志、快照和 Tag Log。
- **Extensions.Impinj** 与 **Extensions.Zebra** 贡献厂商匹配、Feature、设置和报告字段，不向 Contracts 引入厂商类型。
- UI 项目只是组合根与消费者。ViewModel 和 Razor Component 不创建 SDK Reader，也不持有 TCP Session。

架构测试会守护依赖方向，防止 SDK 或 UI 类型泄漏到公开 Contracts。

### Reader 所有权与并发

每个已注册 Reader 拥有一个 **ReaderHandle**、一个 Reader 级操作 Gate，并且任一时刻最多只有一个活动 TCP Session。

- **Probe** 使用临时 Session；成功的标准 Session 可以交接给激活流程。
- **Settings、Tag Access 和 GPIO** 使用短 Lease：连接、执行、规范化结果、断开。
- **Inventory** 从 Start 到 Stop 或故障始终持有一个长 Lease；所有报告都来自同一个 InventorySession。
- 冲突的短操作返回稳定的 **ReaderBusy** 结果。平台不会隐式停止或重启盘存。
- 不同 Reader 使用各自的 Gate，可以并行操作。
- Faulted 或 Stale Session 会先释放，后续操作再重新 Probe 并匹配扩展。

该生命周期只在 Services 中实现一次，由 WPF 和 Blazor 消费者共同复用。

## 设备与协议边界

兼容性按层级和真机证据声明：

| 层级 | 含义 |
|---|---|
| **L1** | TCP、LLRP 握手、协议版本、身份和标准能力查询 |
| **L2** | 标准盘存与标签观测 |
| **L3** | 标准设置、Gen2 Filter、Tag Access 和 GPI/GPO |
| **L4** | Impinj Search Mode、FastID、Phase、Low Duty Cycle 等厂商扩展 |

当前基线：

| 目标 | 状态 |
|---|---|
| **Impinj R420 / LLRP 1.0.1** | 主要真机基线。连接、标准与 Impinj 设置、盘存、FastID/Phase 报告、Tag Memory 读取、User Bank 写入恢复、GPI 状态和 GPO 控制均有记录证据。物理 GPI Trigger、多 Reader 同时盘存和部分故障恢复场景仍是明确验收项。 |
| **标准 LLRP 1.0.1 Reader** | 维护中的标准 Reader 基线已有 Probe、激活、能力/设置查询和设置回写证据。盘存与 Tag Access 仍取决于具体设备与天线条件。 |
| **LLRP 1.1 与 2.0** | 已接入连接策略和标准能力门控基础设施。在合适真机完成验收前，平台将其保持为 PendingHardware。 |
| **Zebra FX9600** | 标准连接与身份读取已有真机证据。平台模块仍是实验性，不声明 L4 支持。 |

[设备兼容性矩阵](docs/compatibility/device-matrix.md)是权威记录。自动化测试、虚拟 Reader、SDK 映射或厂商名称都不能提升设备支持等级。

## 运行主要 WPF 客户端

要求：

- 源码构建需要 Windows 和 .NET 10 SDK；
- 网络能够访问 LLRP Reader，默认 TCP 端口通常为 5084。

仓库默认引用已发布的 LLRPCSharp NuGet 包：

~~~powershell
dotnet restore src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj -p:UseLocalLlrpSdk=false
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj -p:UseLocalLlrpSdk=false
~~~

首次运行时，应用会在以下目录创建 SQLite 数据库、日志和盘存数据：

~~~text
%LocalAppData%\LlrpReaderPlatform\
~~~

典型工作流：

1. 在 **Data Sources** 中发现或添加 Reader。
2. 选择协议策略，Probe 端点并启用 Reader。
3. 打开 **Reader Settings**，读取当前值或 SDK 默认值，只编辑能力支持的字段并 Apply。
4. 在 **Inventory** 中启动一台或多台 Reader，观察实时标签和聚合统计。
5. 按需使用 **Tag Memory**、**Tag Lists**、**Inventory Runs** 和 **Diagnostics**。
6. 对同一 Reader 执行 Settings、Tag Access 或 GPIO 前，先显式停止盘存。

操作细节见 [WPF 用户与故障排查指南](docs/development/wpf-user-and-troubleshooting.md)。

## 其他客户端与虚拟 Reader

### MAUI Blazor Hybrid

主 MAUI 项目在 Windows 上目标为 Windows 与 Android，在 macOS 上还包含 Mac Catalyst。它复用 Contracts、Services、Infrastructure 和厂商模块；响应式页面只改变展示，不分叉 Reader 生命周期。

~~~powershell
dotnet build src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
dotnet run --project src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
~~~

Android 与 Mac Catalyst 需要对应 MAUI Workload 和平台工具链。Linux Head 还需要 GTK4、WebKitGTK、兼容的 .NET Runtime 和预览版 MAUI Linux 后端。详见 [ReaderManager 开发模式](docs/development/reader-manager.md)。

### 两种 Virtual Reader 开发路径

仓库有两种职责完全不同的虚拟 Reader：

- **LlrpReaderPlatform.VirtualReader** 是用于 Services/UI 确定性开发的进程内 IReaderSession 实现。它不监听 TCP，也不是外部 LLRP 端点。
- **LlrpVirtualDevice.App.Wpf** 管理来自 LLRPCSharp Virtual Device Hosting 包的真实 TCP/LLRP 虚拟端点。主客户端连接它们的方式与连接硬件 Reader 完全相同。

在 Windows 上运行报文级虚拟设备管理器：

~~~powershell
dotnet run --project src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj -p:UseLocalLlrpSdk=false
~~~

场景、持久化和打包说明见 [Virtual Reader 开发模式](docs/development/virtual-reader.md)。

## 构建与测试

完整解决方案包含 WPF、MAUI、Linux GTK4、共享库和测试。请安装待构建项目所需的 Workload。

~~~powershell
dotnet restore LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=false
dotnet build LlrpReaderPlatform.slnx --no-restore -p:UseLocalLlrpSdk=false
dotnet test LlrpReaderPlatform.slnx --no-build -p:UseLocalLlrpSdk=false
~~~

项目将警告视为错误。自动化测试覆盖 Contracts、Services、Infrastructure、WPF ViewModel、架构边界、厂商模块和进程内 Virtual Reader。真实 Reader 验收是独立流程，见[硬件验证 Runbook](docs/development/hardware-validation-runbook.md)。

### 使用本地 LLRPCSharp 源码联调

跨仓库调试时，可以显式传入属性：

~~~powershell
dotnet build LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=true -p:LlrpSdkSourceRoot=..\LLRPCSharp
~~~

也可以把 **Directory.Build.local.props.example** 复制为 Git 忽略的 **Directory.Build.local.props**，再修改源码路径。CI 与发布构建始终使用 **UseLocalLlrpSdk=false**，确保发布产物来自正式 SDK 包。

## 仓库结构

~~~text
src/
  LlrpReaderPlatform.Contracts/          对外领域合同
  LlrpReaderPlatform.Services/           Reader 与操作编排
  LlrpReaderPlatform.Infrastructure/     SQLite、发现、日志与快照
  LlrpReaderPlatform.Extensions.Impinj/  Impinj 平台模块
  LlrpReaderPlatform.Extensions.Zebra/   实验性 Zebra 模块
  LlrpReaderPlatform.VirtualReader/      进程内开发 Reader

  LlrpReaderPlatform.App.Wpf/            主要 Windows 客户端
  LlrpReaderManager/                     MAUI Blazor Hybrid 客户端
  LlrpReaderManager.Linux/               实验性 Linux GTK4 Head
  LlrpVirtualDevice.App.Wpf/             TCP 虚拟设备管理器

tests/                                   合同、服务、UI、架构、扩展、
                                         虚拟 Reader 与硬件验证项目
docs/                                    架构、ADR、开发、兼容性、
                                         发布与迁移文档
~~~

## 发布

本仓库发布应用，而不是平台 NuGet 包：

- 自包含 Windows x64 WPF 主客户端与虚拟设备管理器；
- MAUI Blazor Windows 包、Android APK 和 Mac Catalyst 应用压缩包；
- Framework-dependent Linux x64 Debian 包；
- 校验文件和发布说明。

Mac Catalyst 产物目前未签名、未公证。Linux 包依赖兼容的 .NET、GTK4 和 WebKitGTK Runtime。准确的产物与平台要求见[发布规范](docs/development/release.md)。

## 文档

- [文档导航](docs/README.md)
- [项目愿景](docs/llrp-framework-vision.md)
- [架构总览](docs/architecture/overview.md)
- [Reader 生命周期与所有权](docs/architecture/reader-runtime.md)
- [扩展与设置模型](docs/architecture/extensions-and-settings.md)
- [测试策略](docs/development/testing-strategy.md)
- [设备兼容性矩阵](docs/compatibility/device-matrix.md)
- [硬件验证 Runbook](docs/development/hardware-validation-runbook.md)
- [发布规范](docs/development/release.md)
- [UI 介绍与发布产物](docs/ui-overview.md)
- [ADR 导航](docs/decisions/README.md)
