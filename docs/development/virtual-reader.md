# Virtual Reader 开发模式

Virtual Reader 是平台的开发/验收替身，不是第二套业务实现。它复用 `IReaderSession`、`ReaderManager`、设置编译、盘存聚合、Tag Access、GPI/GPO 和 WPF 页面，因此可以在没有真机的情况下验证完整的上位机链路。

当前文档涉及两种不同的虚拟 Reader：平台内进程 Session 与独立的报文级 TCP 虚拟设备。前者通过
`LLRP_VIRTUAL_SCENARIO` 验证主客户端链路；后者由 `src/LlrpVirtualDevice.App.Wpf` 管理 UI 和相邻
`LLRPCSharp` SDK 的 Virtual Device Hosting 提供 TCP/LLRP 服务。

当前状态：平台内进程 Virtual Reader 已作为主客户端的显式开发模式交付；报文级管理 UI 已完成首版
实例管理、Host 启停、客户端/报文观察和 Tag Pool 配置。平台级 Virtual Reader Data Source 化仍按
ADR-0016 保留为长期未排期工作。

> 当前实现状态：本页“启用方式”描述的是已实现的单场景开发模式。将平台虚拟设备纳入
> Data Sources、允许真实/虚拟 Reader 并存和改用内置预设，属于 [ADR-0016](../decisions/ADR-0016-platform-virtual-reader-data-sources.md)
> 与[主计划 7.1](../llrp-framework-vision.md)中的长期 VP1～VP6，尚未实现且未排期。

## 启用方式

在启动 WPF 应用前设置场景文件路径：

```powershell
$env:LLRP_VIRTUAL_SCENARIO = "F:\Projects\LLRP\LlrpReaderPlatform\docs\development\samples\virtual-reader.json"
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj
```

这是显式开发开关。未设置时，应用仍使用真实 LLRP SDK Session；发布流程不会设置该环境变量，也不会把 Virtual Reader 当作真实设备支持声明。

启动时应用会：

1. 加载场景和标签数据；
2. 注册一个虚拟 `ReaderProfile` 到当前 SQLite profile store（只在该 ID 不存在时写入）；
3. 用 Virtual Reader SessionFactory 替换真实 TCP SessionFactory；
4. 让现有 WPF 页面按正常路径执行 Probe、Activate、Settings、Inventory、Stop、Disconnect。

## 场景与真实数据

场景文件是设备画像和回放策略，标签数据不写入新的专用数据库：

- `Inventory.TagLogPath` 优先读取平台的 `tag-logs` JSONL，按文件顺序回放；
- 没有 JSONL 时使用 `Inventory.SnapshotPath` 的 `inventory-snapshots/*.json` 作为回退数据；
- JSONL 中的 `TagObservation`、snapshot 中的 `tags` 和 `VirtualTagMemorySeed` 共用平台的 EPC/TID/时间/RSSI/天线字段；
- 时间戳无效、为 Unix epoch 或不单调时使用配置的回退间隔，不会把 1970 年时间直接当作长延迟；
- 标签 User/Reserved/TID/EPC 内存以及 Access Password 由场景种子定义，写入后跨短连接和 Session 重建保留。

建议为场景固定填写 `readerId`；如果省略，模型会生成新的 ID，适合一次性测试，不适合反复启动同一个 SQLite 数据目录。

最小场景：

```json
{
  "schemaVersion": 1,
  "name": "captured-reader",
  "readerName": "Virtual captured reader",
  "host": "virtual-reader",
  "port": 5084,
  "protocolVersion": "Force101",
  "inventory": {
    "tagLogPath": "..\\..\\..\\tag-logs"
  },
  "replay": {
    "mode": "Accelerated",
    "speed": 20,
    "fallbackIntervalMilliseconds": 50
  },
  "tagMemory": [
    {
      "epc": "3000AABB",
      "tidHex": "E20001",
      "userHex": "11223344",
      "accessPasswordHex": "01020304"
    }
  ]
}
```

## 回放模式

- `RealTime`：按采集时间间隔回放；
- `Accelerated`：按 `speed` 倍速回放；
- `Step`：每调用 `VirtualReaderSession.AdvanceOneReplayEvent()` 才释放一条事件，适合服务测试；
- `Loop`：完成一轮后继续从头回放；`replay.loop=true` 也会启用循环。

盘存不会因为数据集回放完毕就隐式停止，和真实 Reader 一样由服务层的手动停止、时长、GPI、设备断连或故障事件结束本轮租约。

## 能力与故障注入

场景可以控制最大天线数、显式天线 ID 要求、Tx/Rx index 表、RF mode、Tag Access、块擦除、GPI/GPO 数量，以及连接、配置查询、配置写入、启动盘存失败和设备主动断开。这样可以验证 UI 的错误状态和停止原因，而不是只验证成功路径。

## 边界

Virtual Reader 是平台内进程 Session，用于 WPF 和 Services 全链路验收；它不模拟真实射频，也不替代 `LLRPCSharp` 中的 TCP LLRP 协议虚拟主机。协议编解码和真实 SDK 的 TCP 互操作继续由 SDK 的虚拟 Reader/协议测试覆盖。

规划完成后，主 WPF 只管理平台级虚拟 Data Source；平台用户从内置预设中选择，不编辑任意
场景参数，也不为无 TCP 的实例伪造 Host/Port。独立 Virtual Reader Manager 管理报文级 TCP
设备，主 WPF 将其视为普通 LLRP 端点。平台预设与报文预设不共享运行时管理或强制共用格式。

报文级管理 UI 的 Tag Pool 是虚拟设备自身的盘存源，不是主客户端 TOI。当前可通过添加、批量生成、删除
标签，以及修改射频场景、读取概率和 RSSI 抖动来维护它；运行中这些控件锁定，停止后修改会保存到管理器
配置。下次启动时由管理 UI 按已保存的标签配置重建 TCP Host。管理 UI 的配置与主客户端 SQLite 数据目录分离。

## 报文级虚拟设备管理 UI：运行与构建

`src/LlrpVirtualDevice.App.Wpf` 在正式发布流水线中作为独立的
`LlrpVirtualDeviceManager-v<version>-win-x64.zip` 资产发布，已按正式依赖方式消费
`LlrpDevice.Virtual.Hosting` 2.0.3 NuGet 包。只有跨仓库联调时显式设置
`UseLocalLlrpSdk=true`，才会切换到相邻 `LLRPCSharp` 项目。

从仓库根目录以 NuGet 模式运行管理 UI：

```powershell
dotnet run --project src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj
```

需要源码联调时再显式启用本地 SDK：

```powershell
$llrpSdkRoot = 'F:\Projects\LLRP\LLRPCSharp'
dotnet run --project src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj `
  -p:UseLocalLlrpSdk=true -p:LlrpSdkSourceRoot=$llrpSdkRoot
```

构建 Release（NuGet 模式）：

```powershell
dotnet restore src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj `
  -p:UseLocalLlrpSdk=false
dotnet build src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj `
  -c Release --no-restore -p:UseLocalLlrpSdk=false
```

构建自包含单文件便携包：

```powershell
dotnet publish src/LlrpVirtualDevice.App.Wpf/LlrpVirtualDevice.App.Wpf.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -p:UseLocalLlrpSdk=false `
  -o artifacts/publish/virtual-device-manager
```

输出程序名为 `LlrpVirtualDeviceStudio.exe`。管理器配置和日志位于
`%LocalAppData%\LlrpVirtualDeviceStudio\`；主客户端使用管理器中配置的 IP/端口连接虚拟 Reader。
