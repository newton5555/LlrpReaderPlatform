# LlrpReaderPlatform

<p align="center">
  <img src="src/LlrpReaderPlatform.App.Wpf/Assets/LlrpReader_Pro_Icon.png" alt="LlrpReaderPlatform" width="160" />
</p>

<p align="center">
  面向真实 LLRP Reader 的 WPF 操作工具与可扩展应用平台
</p>

LlrpReaderPlatform 的首个交付物是一个可以连接 Reader、读取设备能力、修改配置、启动寻卡和执行 Tag Access 的 Windows WPF 应用。界面沿用旧 `LlrpReaderStudio` 的操作习惯，同时由新的服务层、SDK 和 SQLite 数据层提供实现。

当前正式版本：**v1.0.0** · Windows x64 · .NET 10 Desktop Runtime

## 应用界面

| 页面 | 能做什么 |
|---|---|
| **Data Sources** | 自动发现或手动添加 Reader；配置 IP、端口和 LLRP 版本策略；启用/停用设备；查看连接、能力和错误状态 |
| **Reader Settings** | 按旧 WPF 的 Tab1/Tab2 组织设备设置；读取、编辑、保存和加载默认值；按设备能力显示 RF、天线、功率、Report、GPI/GPO 等设置 |
| **Inventory / 寻卡** | 单台或多台 Reader 同时盘存；Start/Stop、持续时间、自动停止；实时显示 EPC、TID、次数、RSSI、天线、信道、时间和 TOI |
| **Tag Memory** | 选择已启用 Reader 和寻卡得到的 EPC/TID，读取或写入 EPC、TID、User、Reserved 等 Memory Bank；操作超时会在本页反馈 |
| **Tags of Interest (TOI)** | 维护 EPC、TOI 名称和颜色；寻卡表格直接显示匹配的名称和颜色；支持新增、删除、编辑和保存 |
| **Inventory Runs** | 查看历史寻卡记录、开始/结束时间、持续时间、读取次数、唯一标签数和停止原因 |
| **Software Settings** | 配置数据库、日志和盘存记录模式等应用级选项 |
| **About** | 查看应用版本和产品信息 |

界面使用原生 WPF `ProgressBar`、MahApps.Metro 和 FontAwesome 图标；表格、设置分组、GPI/GPO 和天线配置保持旧 WPF 项目的操作风格，并根据 Reader 实际能力隐藏不适用的选项。

## 核心能力

- **Reader 生命周期**：发现 → Probe → Activate → 能力/设置查询 → Inventory 或短操作 → Stop → Disconnect；每台 Reader 独占自己的 LLRP Session 和命令队列。
- **标准 LLRP**：支持 LLRP 1.0.1 基线，并保留 1.1 协议策略和扩展空间；支持 Auto、Force 1.0.1、Force 1.1 的设备连接策略。
- **标准配置**：从 Reader 能力表生成可编辑设置；Tx Power、Rx Sensitivity、RF Mode、Session、Tag Population、Report 和天线配置使用设备实际 index/id 写入。
- **Inventory**：统一接收标签事件并更新 UI；支持多 Reader 并行、生命周期停止原因、计数聚合、TID、RSSI、天线、信道和时间信息。
- **Tag Access**：按设备能力执行 EPC/TID/User/Reserved Memory Bank 读取和写入；不支持的设备或 Bank 不会在 UI 中误报为可用。
- **GPI/GPO**：支持端口状态查询、GPO 控制和 GPI 事件；Tab2 位于设备设置页面，并按真实端口能力生成控件。
- **Impinj 扩展**：支持 R420 的 Search Mode、FastID、Phase、Low Duty、固定频率、GPI Debounce 以及扩展标签字段，扩展通过独立模块接入。
- **本地数据**：EF Core SQLite 保存 Reader Profile、设置预设、TOI、Inventory Runs 和应用设置；早期数据库结构变化允许清空数据库重建。
- **诊断与记录**：UI、平台服务和 SDK/LLRP 报文分层写入日志；盘存停止后生成最终快照，原始 JSONL 报告可按需开启。

## 典型使用流程

1. 启动应用，在 **Data Sources** 中发现或添加 Reader。
2. 选择协议策略，执行 Probe 并启用 Reader。
3. 打开 **Reader Settings**，读取设备当前设置；按能力调整 Tab1 或 Tab2 后保存。
4. 进入 **Inventory**，选择持续时间或自动停止条件，启动一台或多台 Reader。
5. 在实时标签表格中查看 EPC/TID、RSSI、天线、TOI 和统计信息；需要访问标签时转到 **Tag Memory**。
6. 停止寻卡后，在 **Inventory Runs** 查看本次运行结果和快照。

## 首批设备支持边界

| 设备类型 | 当前状态 |
|---|---|
| 标准 LLRP 1.0.1 Reader | 已完成连接、身份/能力查询和部分标准设置验收；Inventory、Tag Access、GPI/GPO 按具体设备能力继续现场验收 |
| Impinj R420 | 首批真机基线；已验证连接、标准设置、Impinj 设置、Inventory、EPC/TID/User/Reserved 读取、User 写入恢复、GPO 和部分 GPI/扩展字段 |
| 标准 Reader `192.168.41.148` | 已验证强制 LLRP 1.0.1 的 Probe、Activate、Settings Query 和部分设置回写；补天线后继续进行 Inventory/Tag Access 现场验收 |
| 其它厂商 Reader | 先按标准 LLRP 能力工作；厂商扩展只有在独立模块和真实设备验证后才声明支持 |

完整结论以[设备兼容性矩阵](docs/compatibility/device-matrix.md)为准。代码测试、协议映射或 SDK 能力表不能替代真实 Reader、天线和标签验收。

## 下载与运行

正式发布由 GitHub Actions 生成：

- `LlrpReaderPlatform-v1.0.0-win-x64.zip`
- 对应的 `.sha256` 校验文件

运行要求：

- Windows x64；
- .NET 10 Desktop Runtime；
- Reader 网络可达，默认 LLRP 端口为 `5084`。

应用首次运行会在 `%LocalAppData%\LlrpReaderPlatform\` 创建 SQLite 数据库、日志和盘存快照目录。

## 本地构建与发布

在安装 .NET 10 SDK 的 Windows 机器上：

```powershell
dotnet restore LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=false
dotnet build LlrpReaderPlatform.slnx -c Release --no-restore -p:UseLocalLlrpSdk=false
dotnet test LlrpReaderPlatform.slnx -c Release --no-build --no-restore -p:UseLocalLlrpSdk=false
dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:UseLocalLlrpSdk=false `
  -o artifacts/publish/win-x64 --no-restore
```

默认使用 `LlrpSdk` `1.3.0` 和 `LlrpSdk.Extensions.Impinj` `1.3.0` NuGet 包。本地 SDK 联调时，才通过 `UseLocalLlrpSdk=true` 切换到相邻 `LLRPCSharp` 源码；正式 CI 和发布始终使用 NuGet 模式。

当前验证基线：**构建 0 警告、0 错误；自动化测试 336 项全绿**。这些测试不包含持续运行的真机测试。

## 文档入口

### 面向使用和验收

- [WPF 用户操作与故障排查](docs/development/wpf-user-and-troubleshooting.md)
- [真机验收运行手册](docs/development/hardware-validation-runbook.md)
- [设备兼容性矩阵](docs/compatibility/device-matrix.md)
- [v1.0.0 发布说明](docs/releases/v1.0.0.md)
- [发布规范与应用流水线](docs/development/release.md)

### 面向开发和扩展

- [总体规划](docs/llrp-framework-vision.md)
- [架构总览](docs/architecture/overview.md)
- [Reader 生命周期与连接所有权](docs/architecture/reader-runtime.md)
- [厂商扩展与设置模型](docs/architecture/extensions-and-settings.md)
- [旧 WPF 功能迁移矩阵](docs/development/legacy-feature-matrix.md)
- [测试策略](docs/development/testing-strategy.md)
- [ADR 索引](docs/decisions/README.md)

## 项目边界

本仓库包含新的平台服务、基础设施、WPF 应用和自动化测试。冻结的旧 `LlrpReaderStudio` 只用于行为和迁移参考，不作为运行时依赖。当前正式交付对象是 WPF 应用，不发布平台类库 NuGet 包；平台使用的 SDK NuGet 只是输入依赖。
