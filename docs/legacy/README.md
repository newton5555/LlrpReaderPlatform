# 冻结仓库：LlrpReaderStudio

## 仓库地址

旧仓库位于：

```text
C:\Users\yankai\source\repos\LlrpReaderStudio
```

解决方案：

```text
C:\Users\yankai\source\repos\LlrpReaderStudio\LlrpReaderStudio.slnx
```

该仓库已经冻结，不作为 `LlrpReaderPlatform` 的 ProjectReference、源码依赖或运行时依赖。需要核对旧实现时，直接打开上述仓库。

## 当前项目结构

```text
LlrpReaderStudio.slnx
├── src/LlrpReaderStudio.Core/
│   ├── ReaderFleetService.cs       Reader 注册、连接、盘存、TagReport 聚合
│   ├── ReaderSession.cs            LlrpSdk Session 适配和 Impinj Builder 配置
│   ├── ReaderProfile.cs            Reader Profile、状态和协议版本策略
│   ├── TagAggregation.cs           Tag 聚合和去重
│   └── HexCodec.cs                 十六进制编码辅助
├── src/LlrpReaderStudio.Infrastructure/
│   ├── Data/                       SQLite DbContext、Profile、Preset Repository
│   └── Discovery/                  Zeroconf/mDNS Reader 发现
├── src/LlrpReaderStudio.Wpf/
│   ├── App.xaml(.cs)               DI、日志、启动和退出清理
│   ├── MainWindow.xaml(.cs)        WPF Shell
│   ├── ViewModels/                 页面状态、命令和 Reader 交互
│   ├── Views/                      页面视图
│   ├── Converters/                 WPF 转换器
│   └── Assets/                     图标资源
└── tests/LlrpReaderStudio.Core.Tests/
    └── Core 单元测试
```

## 依赖方向

```text
LlrpReaderStudio.Wpf
  -> LlrpReaderStudio.Infrastructure
  -> LlrpReaderStudio.Core
       -> LlrpSdk
       -> LlrpSdk.Extensions.Impinj
```

Infrastructure 同时直接引用 `LlrpSdk` 和 `LlrpSdk.Extensions.Impinj`，旧项目的 Core/Infrastructure 仍然存在 Impinj 耦合；这正是新平台需要通过独立扩展模块和 Services 边界解决的问题。

## 当前技术基线

- .NET 10；
- WPF；
- CommunityToolkit.Mvvm；
- MahApps.Metro；
- Microsoft.Extensions.DependencyInjection；
- SQLite/EF Core；
- Zeroconf/mDNS；
- `LlrpSdk 1.2.0`；
- `LlrpSdk.Extensions.Impinj 1.2.0`。

当前已验证的设备基线为标准 LLRP 1.0.1 设备和 Impinj R420。新平台将以这两类设备作为回归起点，但不继承旧项目的架构边界。

## 旧仓库文档

旧仓库中的详细文档仍保留在旧仓库 `docs/` 目录。新平台只保留本说明，不复制旧规划；新平台的规范以 [总体规划](../llrp-framework-vision.md)、[架构文档](../architecture/overview.md) 和 ADR 为准。
