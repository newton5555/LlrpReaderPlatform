# LlrpReaderPlatform

<p align='center'>
  <img src='src/LlrpReaderPlatform.App.Wpf/Assets/LlrpReader_Pro_Icon.png' alt='LlrpReaderPlatform' width='160' />
</p>

<p align='center'>
  <strong>面向真实 LLRP Reader 的 WPF 操作工具与可扩展应用平台</strong>
</p>

<p align='center'>
  <strong>v2.0.1</strong> · Windows x64 · 自包含单文件便携发布 · <code>LlrpSdk</code> 2.0.1
</p>

<p align='center'>
  <a href='README.md'>English</a> · <strong>中文</strong>
</p>

---

## 概览

LlrpReaderPlatform 是一个新的 LLRP 应用平台，首个交付物是 Windows WPF 应用，可连接 Reader、读取设备能力、修改配置、启动寻卡和执行 Tag Access。界面沿用冻结旧仓库 `LlrpReaderStudio` 的操作习惯，由新的服务层、SDK 适配和 EF Core SQLite 数据层提供实现。

冻结的 `../LlrpReaderStudio` 仓库仅作参考，只记录现有能力和迁移边界，不作为运行时依赖。

当前主要交付对象是面向真实 Reader 的客户端 `LlrpReaderPlatform.App.Wpf`。解决方案同时包含
独立的 `LlrpVirtualDevice.App.Wpf` 报文级虚拟设备管理 UI；它是辅助工具，不属于真实 Reader
客户端的服务链路。

**当前基线：** `2.0.1` · Windows x64 · 自包含单文件便携发布。构建 0 警告、0 错误，自动化测试 385 项全绿（含 Virtual Reader 场景与生命周期测试）；服务测试主要使用 `FakeSession`，Virtual Reader 套件覆盖确定性设备行为，真机结论单独记录。

## 架构

依赖单向流动。`Contracts` 保持 UI、SDK、厂商无关；`Services` 负责设备语义；`Infrastructure` 与 `Extensions.*` 提供实现；WPF 应用只是消费者。

<img src='docs/assets/architecture.svg' alt='LlrpReaderPlatform 分层与依赖关系图' width='960' />

```text
UI consumer -> Services -> Contracts
                       -> Infrastructure
                       -> Extensions.*
```

- `Contracts` — UI 无关的 DTO、状态、设置编辑模型和服务接口；不得引用 WPF、SDK 或厂商扩展类型。
- `Services` — Reader 生命周期、连接租约、能力聚合、设置、寻卡与 Tag Access。
- `Infrastructure` — SQLite、预设/Profile、发现和日志实现。
- `Extensions.*` — 可插拔厂商模块（Impinj 为基线，Zebra 为实验性）；不污染通用契约。
- `App.Wpf` — 首个 UI 消费者（WPF、CommunityToolkit.Mvvm、MahApps.Metro）；未来 UI 框架可复用同一套服务与契约。
- `LlrpVirtualDevice.App.Wpf` — 独立的报文级虚拟设备管理 UI，直接消费 SDK 的虚拟设备 Host 边界，不向客户端服务层增加虚拟设备分支。

SDK 侧有两个出口：核心 `LlrpSdk`（标准 LLRP 适配，由 `Services` 消费）与 `LlrpSdk.Extensions.Impinj` / `LlrpSdk.Extensions.Zebra`（厂商适配，由 `Extensions.*` 消费）。

**Reader 所有权规则** —— 每个 `ReaderHandle` 只拥有一个 TCP `ReaderSession`；对同一 Reader 的命令经过该 Session 的单一 `Gate` 串行化。Inventory 是长期租约；Settings、Tag Access、GPO 等短操作在冲突时返回明确的 `ReaderBusy`，不会隐式停止或重启 Inventory。

## 兼容性分层

平台按四层兼容性推进，支持等级逐层来自真机测试，不能仅由厂商名称或 SDK 包推导。

<img src='docs/assets/compatibility.svg' alt='LLRP 兼容性分层 L1 至 L4' width='960' />

- **L1** — 连接、LLRP 握手、协议版本、身份、能力探测。
- **L2** — 标准 Inventory：EPC、RSSI、天线、信道、SeenCount、时间戳。
- **L3** — 标准设置、Gen2 过滤器、Tag Access、GPI/GPO（按能力决定可用性）。
- **L4** — 厂商扩展（如 Impinj Search Mode、FastID、Phase），仅在独立模块与真机验证后声明。

## 核心能力

- **Reader 生命周期** — 发现 → 探测 → 激活 → 能力/设置查询 → 寻卡或短操作 → 停止 → 断开；每台 Reader 独占自己的 LLRP Session 和命令队列。
- **标准 LLRP** — 支持 LLRP 1.0.1 基线，Auto / Force 1.0.1 / Force 1.1 连接策略，以及 1.1 / 2.0 门控基础设施（暂无真机，标为 `PendingHardware`）。
- **标准配置** — 从能力表生成可编辑设置；Tx Power、Rx Sensitivity、RF Mode、Session、Tag Population、Report 和天线配置使用设备实际 index/id 写入。
- **Inventory** — 统一接收标签事件；多 Reader 并行、生命周期停止原因、计数聚合、TID、RSSI、天线、信道和时间信息。
- **Tag Access** — 按设备能力执行 EPC/TID/User/Reserved Memory Bank 读写；不支持的设备或 Bank 不会在 UI 中误报为可用。
- **GPI/GPO** — 端口状态查询、GPO 控制和 GPI 事件；控件按真实端口能力生成。
- **厂商扩展** — Impinj R420（Search Mode、FastID、Phase、Low Duty、固定频率、GPI Debounce、扩展标签字段）通过独立模块接入；Zebra 作为实验性模块接入，未真机标定前不声明支持。
- **本地数据** — EF Core SQLite 保存 Reader Profile、设置预设、TOI、Inventory Runs 和应用设置。
- **诊断与记录** — UI、平台服务和 SDK/LLRP 报文分层记录日志；盘存快照与可选原始 JSONL 报告。

## 应用页面

| 页面 | 功能 |
|---|---|
| **Data Sources** | 自动发现或手动添加 Reader；配置 IP、端口、LLRP 版本策略；启用/停用；查看连接、能力和错误状态。 |
| **Reader Settings** | 按 Tab1/Tab2 组织设置；读取、编辑、保存和加载默认值；按能力显示 RF、天线、功率、Report、GPI/GPO。 |
| **Inventory** | 单台或多台同时盘存；Start/Stop、持续时间、自动停止；实时显示 EPC、TID、次数、RSSI、天线、信道、时间和 TOI。 |
| **Tag Memory** | 选择已启用 Reader 和寻卡得到的 EPC/TID，读写 EPC、TID、User、Reserved Memory Bank；操作超时在本页反馈。 |
| **Tags of Interest (TOI)** | 维护 EPC、名称和颜色；寻卡表格直接显示匹配的名称和颜色；支持新增、删除、编辑、保存。 |
| **Inventory Runs** | 查看历史记录：开始/结束时间、持续时间、读取次数、唯一标签数、停止原因。 |
| **Software Settings** | 应用级选项：数据库、日志、盘存记录模式。 |
| **About** | 应用版本和产品信息。 |

界面使用原生 WPF `ProgressBar`、MahApps.Metro 和 FontAwesome 图标；表格、设置分组、GPI/GPO 和天线配置保持旧 WPF 的操作风格，并根据 Reader 实际能力隐藏不适用选项。

## 典型使用流程

1. 启动应用，在 **Data Sources** 中发现或添加 Reader。
2. 选择协议策略，执行 Probe 并启用 Reader。
3. 打开 **Reader Settings**，读取当前设置，按能力调整 Tab1/Tab2 后保存。
4. 进入 **Inventory**，选择持续时间或自动停止条件，启动一台或多台 Reader。
5. 在实时表格查看 EPC/TID、RSSI、天线、TOI 和统计；访问标签时转到 **Tag Memory**。
6. 停止寻卡后在 **Inventory Runs** 查看本次运行与快照。

## 首批设备支持边界

| 设备 | 状态 |
|---|---|
| 标准 LLRP 1.0.1 Reader | 已完成连接、身份/能力查询和部分标准设置验收；Inventory、Tag Access、GPI/GPO 按具体设备能力继续现场验收。 |
| Impinj R420 | 首批真机基线；已验证连接、标准/Impinj 设置、寻卡、EPC/TID/User/Reserved 读取、User 写入恢复、GPO 和部分 GPI/扩展字段。 |
| 标准 Reader `192.168.41.148` | 已验证强制 LLRP 1.0.1 的 Probe、Activate、Settings Query 和部分设置回写；补天线后进行 Inventory/Tag Access 现场验收。 |
| 其它厂商 Reader | 先按标准 LLRP 能力工作；厂商扩展仅在独立模块和真实设备验证后才声明。 |

完整结论以[设备兼容性矩阵](docs/compatibility/device-matrix.md)为准。代码测试、协议映射或 SDK 能力表不能替代真实 Reader、天线和标签验收。

## 文档入口

### 使用与验收

- [WPF 用户操作与故障排查](docs/development/wpf-user-and-troubleshooting.md)
- [真机验收运行手册](docs/development/hardware-validation-runbook.md)
- [硬件测试命令行项目](tests/LlrpReaderPlatform.Hardware.Tests/LlrpReaderPlatform.Hardware.Tests.csproj)
- [设备兼容性矩阵](docs/compatibility/device-matrix.md)
- [v2.0.1 发布说明](docs/releases/v2.0.1.md)
- [v2.0.0 发布说明](docs/releases/v2.0.0.md)
- [v1.4.0 发布说明](docs/releases/v1.4.0.md)
- [发布规范与应用流水线](docs/development/release.md)

### 开发与扩展

- [总体规划](docs/llrp-framework-vision.md)
- [架构总览](docs/architecture/overview.md)
- [Reader 生命周期与连接所有权](docs/architecture/reader-runtime.md)
- [厂商扩展与设置模型](docs/architecture/extensions-and-settings.md)
- [旧 WPF 功能迁移矩阵](docs/development/legacy-feature-matrix.md)
- [测试策略](docs/development/testing-strategy.md)
- [Virtual Reader 与虚拟设备管理器](docs/development/virtual-reader.md)
- [ADR 索引](docs/decisions/README.md)

## 项目边界

本仓库包含新的平台服务、基础设施、真实 Reader 客户端 WPF 应用、独立的虚拟设备管理 UI 和自动化测试。冻结的旧 `LlrpReaderStudio` 只用于行为和迁移参考，不作为运行时依赖。不发布平台类库 NuGet 包；平台使用的 SDK NuGet 只是输入依赖。

## 虚拟设备管理 UI

`src/LlrpVirtualDevice.App.Wpf` 是独立的 WPF 虚拟设备管理工具，管理相邻 SDK 提供的 TCP/LLRP
虚拟 Reader。当前 UI 支持创建/删除多个虚拟设备实例、配置监听 IP/端口、选择能力档案和协议版本、
启动/停止/重启 Host、保存配置、查看连接客户端和已观察到的 LLRP 报文，以及维护标签库和射频模拟参数。

标签库当前支持添加标签、快速生成 20 张、删除所选标签，并支持 Static/MovingTags/Noisy 场景、读取概率
和 RSSI 抖动设置；虚拟设备运行时这些控件会锁定。配置由管理器保存到自己的 `virtual-devices.json`，
下次点击运行时按已保存的 `Config.Tags` 重建 Host。真实 Reader 客户端通过 IP 和端口连接该 Host，
使用方式与连接真实 Reader 一致。事件与故障注入页目前只有交互入口和提示，尚未作为实际注入能力交付。

虚拟设备管理器的源码运行、构建和打包命令见[Virtual Reader 开发模式](docs/development/virtual-reader.md)，
主客户端的下载和正式发布流程见[发布规范与应用流水线](docs/development/release.md)。
