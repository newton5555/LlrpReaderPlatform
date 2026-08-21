# ADR-0019：MAUI Blazor Linux GTK4 独立 Head

## 状态

已接受（实验性后端、正式构建与发布资产，2026-08-21）

## 背景

`LlrpReaderManager` 当前是 MAUI Blazor Hybrid 消费者，正式目标包含 Android、Windows 和 Mac
Catalyst。MAUI 没有正式的 `net10.0-linux` TFM，但 `dotnet/maui-labs` 已提供 GTK4 后端和基于
WebKitGTK 的 BlazorWebView 实现。

## 决定

新增 `src/LlrpReaderManager.Linux` 作为独立 Linux Head，目标为 `net10.0`，使用 Linux GTK4、
BlazorWebView 和 Essentials 实现。Linux Head 复用现有 MAUI/Blazor 消费者的 Razor 页面、状态投影、
虚拟设备挂件和平台服务注册，不修改 Android、Windows 或 Mac Catalyst 的目标框架，也不创建第二套
Reader 生命周期。

## 约束

- Linux GTK4 后端仍是实验性依赖，但已纳入 Ubuntu CI 和 Tag Release 的构建验证；
- 运行时需要 GTK4 4.12+ 和 WebKitGTK 6.x 原生库；
- Linux 项目采用独立 Head，不伪造 `net10.0-linux` TFM；
- Linux 页面必须继续通过 Contracts/Services 工作，不增加 Linux 专属协议分支；
- 后续若共享文件链接造成维护成本，应把消费者状态和 Razor 页面提取到独立共享 RCL。

## 结果

当前项目可以在 Linux 桌面以 GTK4 原生窗口运行同一套 Blazor UI。Ubuntu Runner 安装原生依赖和
`maui-tizen` workload 后，使用 `linux-x64` Runtime Identifier 构建；Deb target 将 framework-dependent
安装包输出到 `src/LlrpReaderManager.Linux/bin/Deb/`，再由 Release Job 上传。该资产不等于 MAUI
官方稳定 Linux TFM，目标机仍需兼容的 .NET Runtime、GTK4 和 WebKitGTK。

