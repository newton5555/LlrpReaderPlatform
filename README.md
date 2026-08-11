# LlrpReaderPlatform

LLRP Reader Platform 是面向多种 LLRP Reader 的应用框架：厂商无关的共享服务层 + 首个 WPF 消费者。

现有 `LlrpReaderStudio` 仓库已经冻结，作为标准 LLRP 1.0.1 和 Impinj R420 的行为、测试和迁移参考，不作为本仓库的项目依赖。

## 文档入口

从 [docs/README.md](docs/README.md) 开始阅读。

建议顺序：

1. [总体规划](docs/llrp-framework-vision.md)
2. [架构总览、解决方案结构与 UI 边界](docs/architecture/overview.md)
3. [设备生命周期与连接所有权](docs/architecture/reader-runtime.md)
4. [设备兼容性矩阵](docs/compatibility/device-matrix.md)
5. [开发路线图](docs/development/roadmap.md)
6. [旧 WPF 功能迁移矩阵](docs/development/legacy-feature-matrix.md)
7. [真机验收运行手册](docs/development/hardware-validation-runbook.md)
8. [WPF 用户操作与故障排查](docs/development/wpf-user-and-troubleshooting.md)
9. [ADR 索引](docs/decisions/README.md)

## 项目结构

```text
LlrpReaderPlatform.slnx
├── src/                       业务实现
│   ├── LlrpReaderPlatform.Contracts/          平台契约（UI/SDK/厂商无关）
│   ├── LlrpReaderPlatform.Services/           生命周期、设置、盘存、扩展模块抽象
│   ├── LlrpReaderPlatform.Infrastructure/     持久化、发现等实现细节
│   ├── LlrpReaderPlatform.Extensions.Impinj/  Impinj 扩展模块
│   └── LlrpReaderPlatform.App.Wpf/            首个 WPF 消费者
├── tests/                     测试项目（集中管理）
│   ├── LlrpReaderPlatform.TestKit/            可控 FakeSession/FakeProfileStore 测试替身
│   ├── LlrpReaderPlatform.Contracts.Tests/   契约 JSON 序列化与边界测试
│   ├── LlrpReaderPlatform.Services.Tests/
│   ├── LlrpReaderPlatform.Extensions.Impinj.Tests/
│   ├── LlrpReaderPlatform.App.Wpf.Tests/
│   ├── LlrpReaderPlatform.Infrastructure.Tests/
│   └── LlrpReaderPlatform.Architecture.Tests/ 依赖方向与公开 API 边界
└── docs/                      规划、架构、兼容性、ADR、开发流程
```

## 当前状态

- 当前已完成服务框架、标准 Probe、单 Session/Gate、Inventory 长连接、真实 Settings Query/Apply/Defaults、TagAccess 映射、EF SQLite Profile/Preset/TagList/Run/AppSettings、可选 JSONL Tag Logging、扩展匹配和 WPF 实时事件投影；剩余重点是更多设备深度验收和少量专用设置编辑器；
- WPF 页面已对齐旧 `LlrpReaderStudio.Wpf` 的布局和功能入口，设备设置 Tab1/Tab2、寻卡、Tag Memory、Tag Lists、Inventory Runs、App Settings、About 已接入真实服务，并使用原生 `ProgressBar`；最终交付仍要求完成真实设备闭环；
- WPF 组合根已按旧项目拆分应用日志与 SDK/LLRP 日志，分别写入 `%LocalAppData%\\LlrpReaderPlatform\\logs\\platform-*.log` 和 `sdk-*.log`，按天/50 MB 滚动并保留 14 个文件；
- 平台为更多设备类型适配做准备：厂商无关 Contracts、单 Session/Gate、Inventory 长连接租约、能力分级和可插拔扩展模块；
- Reader 连接故障或短连接释放不可靠时，服务层会回收旧 Session，下一次激活/寻卡/短操作创建干净 Session，不复用故障连接；
- `dotnet build LlrpReaderPlatform.slnx`：0 警告 0 错误；`dotnet test`：307 项全绿；
- 真机 `192.168.41.134` 已完成标准 Probe/Settings Query、Impinj 扩展连接、有界 Inventory Start/Stop/Disconnect，以及 WPF Tab1 Settings Apply、Tab2 GPO、GPI 状态查询和 Impinj GPI debounce 回写；2026-08-11 又确认设备列表状态刷新不会取消在途设置查询，WPF 设置页稳定显示 `Loaded from Reader`、Save 和 62 个回读值；随后用真实标签验证了新平台 ReaderManager 的 TagReport 聚合（10 秒、1533 条事件、8 个唯一 EPC）、TID 读取、User Bank 写入恢复和 FastID/Phase 扩展字段，详见[设备矩阵](docs/compatibility/device-matrix.md)与[总体规划](docs/llrp-framework-vision.md)。

## 构建、发布与运行

在安装 .NET 10 SDK 的 Windows 机器上：

```powershell
dotnet build LlrpReaderPlatform.slnx
dotnet test LlrpReaderPlatform.slnx --no-build
dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj `
  -c Release -r win-x64 --self-contained false `
  -o artifacts/publish/win-x64
& .\artifacts\publish\win-x64\App.Wpf.exe
```

应用首次运行会在 `%LocalAppData%\LlrpReaderPlatform\llrp-reader-platform.db` 创建新平台 SQLite 数据库；早期 schema 变化允许清空数据库重建。平台日志和 SDK/LLRP 日志分别写入 `%LocalAppData%\LlrpReaderPlatform\logs\platform-*.log` 与 `sdk-*.log`；启用 Tag Logging 且未指定目录时，JSONL 标签日志默认写入 `%LocalAppData%\LlrpReaderPlatform\tag-logs`。`artifacts/`、`bin/` 和 `obj/` 均为本地生成物，不应提交。
