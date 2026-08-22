# LlrpReaderPlatform UI 介绍

本文是 UI 入口说明，帮助使用者快速区分桌面客户端、移动端客户端和虚拟设备工具。各 UI 共用
`LlrpReaderPlatform.Contracts`、`Services`、`Infrastructure` 以及厂商扩展；UI 不直接编排 SDK
协议，也不自己持有 Reader TCP Session。

## 应用组成

| UI | 项目 | 定位 |
| --- | --- | --- |
| LLRP Reader Studio | `src/LlrpReaderPlatform.App.Wpf` | Windows 主客户端，面向真实 Reader 和 TCP 虚拟 Reader |
| LlrpReaderManager | `src/LlrpReaderManager` | MAUI Blazor Hybrid 客户端，复用同一套平台服务 |
| Virtual Device Manager | `src/LlrpVirtualDevice.App.Wpf` | Windows 虚拟设备管理器，用于启动和管理 LLRP TCP 虚拟设备 |
| LlrpReaderManager Linux | `src/LlrpReaderManager.Linux` | 实验性的 Linux GTK4 Head，承载相同的 Blazor 页面 |

## Windows：LLRP Reader Studio

这是当前现场操作和真机验收的主 UI。它适合大屏桌面操作，主要页面包括：

- **寻卡 / Inventory**：启动或停止 Reader 盘存，查看 EPC、RSSI、天线、信道、时间和聚合计数；
- **Tag Memory**：按 EPC/TID 目标执行标签存储区读写；
- **TOI**：维护标签列表、颜色和名称，并在盘存结果中投影；
- **Inventory Runs**：查看历史盘存运行、停止原因和汇总结果；
- **Reader Settings**：按设备能力编辑 RF、天线、过滤器、报告、GPI/GPO 和厂商设置；
- **Data Sources**：发现、添加、启用、停用和移除 Reader。

### Windows UI 截图

> 截图占位：当前文档先保留 Windows 截图位置。截图应使用真实的 `LLRP Reader Studio` 窗口，不能使用
> CI 日志或宿主桌面截图。

建议文件名：`docs/assets/ui/windows/llrp-reader-studio.jpg`。

## MAUI Blazor 跨平台客户端

`LlrpReaderManager` 与 WPF 使用同一套 Contracts、Services 和 Reader 生命周期。平台差异只改变窗口、
导航和信息排布，不增加另一套协议实现。

| 平台 | UI 形态 | 交付/使用说明 | 截图 |
| --- | --- | --- | --- |
| Windows | MAUI Blazor Hybrid 桌面窗口 | GitHub Release 中的 Windows 目录包；适合响应式桌面布局 | 待补充 |
| Android | MAUI Blazor Hybrid APK | 触摸友好的 Reader、Inventory、Tag Memory 和运行记录页面；需要 Android 安装与测试环境 | 待补充 |
| macOS | Mac Catalyst `.app` | CI 生成 Intel 和 Apple Silicon 两种未签名包；解压后运行，首次打开按 Gatekeeper 提示处理 | 待补充 |
| Linux | GTK4 独立 Head | 以 `.deb` 交付，依赖兼容的 .NET Runtime、GTK4 和 WebKitGTK；当前为实验性路径 | 待补充 |

移动端和其他桌面端截图暂留空位。CI 的职责是构建和打包，不会自动生成产品宣传截图；截图应在对应平台
启动真实应用后单独采集，并与版本说明一起维护。

## 虚拟设备 UI

`Virtual Device Manager` 是独立的 Windows WPF 工具，直接使用 `LlrpDevice.Virtual.Hosting` 顶层包。
它负责创建、启动、停止和查看 TCP/LLRP 虚拟设备；主客户端把虚拟设备当作普通 Reader 连接，因此虚拟
设备 UI 不会复制主客户端的 Reader 生命周期。

这个工具主要用于：

1. 没有真机时验证连接、探测、能力同步和盘存流程；
2. 复现固定报文、标签池和设备状态；
3. 在 CI 或本地自动化测试之外，手工观察协议级虚拟设备行为。

## UI 与 SDK 的边界

```text
WPF / Blazor / GTK4 UI
        -> Platform Services
        -> Platform Contracts
        -> Infrastructure / Vendor Extensions
        -> LlrpSdk NuGet
```

客户端使用平台公开服务和契约即可。只有需要扩展厂商能力、协议能力或虚拟设备能力时，才由平台服务层
引入对应扩展包；UI 不应直接引用或暴露 SDK 的报文类型。

## GitHub Release 产物

平台仓库发布的是应用资产，不发布平台 NuGet。一个版本的 Release 会按平台拆分为：

- Windows 主客户端 WPF 自包含单文件 ZIP；
- Windows 虚拟设备管理器 WPF 自包含单文件 ZIP；
- Windows MAUI Blazor 包；
- Android APK；
- macOS Intel / Apple Silicon 的 Mac Catalyst `.app` ZIP；
- Linux x64 GTK4 `.deb`；
- 各资产对应的 SHA256 校验文件。

Mac Catalyst 当前是未签名、未公证的内部测试包；Android 当前的 APK 也不等于已完成商店签名或上架。具体
下载和安装步骤见[发布规范](development/release.md)，开发调试见
[LlrpReaderManager 开发模式](development/reader-manager.md)。
