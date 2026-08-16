# ADR-0016：平台虚拟 Reader 作为预设式 Data Source

- 状态：Accepted（长期规划，未排期）
- 日期：2026-08-16

## 决策

本决策定义长期演进方向，不进入当前发布或近期迭代。现有单场景开发模式继续作为当前基线，
只有在用户重新明确启动该长期工作包后，才按本文和主计划 VP1～VP6 实施。

平台级 Virtual Reader 继续属于 `LlrpReaderPlatform`，并作为主 WPF 的正式 Data Source
管理。它是进程内 `IReaderSession` 实现，不监听 TCP，不伪造 Host/Port，也不能被主程序
之外的客户端连接。

Data Source 使用有类型的来源描述：

```text
ReaderProfile
└── Source
    ├── TcpReaderSource(Host, Port)
    └── PlatformVirtualReaderSource(PresetId, PresetVersion, InstanceId)
```

真实 Reader 和外部报文级虚拟 Reader 都属于 `TcpReaderSource`。主 WPF 不记录、启动或
停止报文级虚拟 Reader；独立 Virtual Reader Manager 启动的 TCP 设备对主 WPF 与真机
完全等价。

平台虚拟设备只允许用户选择软件内置预设，不提供配方编辑、任意 JSON 导入或能力参数
编辑。用户输入限于实例名称、预设和启用状态。预设通过 Catalog/Contributor 注册，使用
稳定的 `PresetId` 与 `PresetVersion`；标准预设和未来厂商预设使用同一注册机制，核心代码
不得以厂商枚举或 `switch` 固化 Impinj、Zebra 等类型。

当前全局替换 `IReaderSessionFactory` 的 `LLRP_VIRTUAL_SCENARIO` 开关降级为开发期启动
快捷方式：它只能导入或创建一个平台虚拟实例，不能改变其他 Data Source 的 SessionFactory。
运行时由路由工厂根据 Source 创建真实 SDK TCP Session 或平台虚拟 Session。

## 背景

ADR-0015 已确定平台 Virtual Reader 是 WPF/Services 全链路开发替身，但首版实现通过环境
变量加载单场景并替换全局 SessionFactory。这使真实 Reader 与平台虚拟 Reader 无法在同一
进程中并存，也迫使无 TCP 的虚拟实例使用伪造的 `virtual-reader:5084` 端点。

相邻 `LLRPCSharp` 仓库已经拥有真正监听 TCP、编码和解码 LLRP 报文的 Virtual Reader。
报文级设备与平台级 Session 替身解决不同问题，不应由主 WPF 统一托管。

## 候选方案

1. 继续用环境变量全局切换：实现简单，但不能混合真实和虚拟 Data Source。
2. 给平台虚拟设备分配伪造 Host/Port：可以复用现有 Profile，但端点语义错误且会污染唯一性校验。
3. 主 WPF 同时托管平台虚拟和报文虚拟 Host：用户入口统一，但把协议服务器、端口生命周期和
   SDK 仓库职责带入平台产品。
4. 采用有类型的 Data Source，并把报文虚拟设备交给独立 Manager：职责清晰，选用本方案。

## 原因

- 平台虚拟设备必须进入 Data Sources 才能持久化、启停、删除和参与多 Reader 验收；
- 无 TCP 的设备不应被 Host/Port 模型强行表示；
- 报文级虚拟设备只有走真实 TCP/SDK 路径才有互操作和抓包价值；
- 内置预设比用户自由编辑配方更可验证，每个可见选项都能绑定自动化验收；
- Contributor 机制允许未来增加厂商预设，而不让 Contracts、WPF 或 VirtualReader 核心依赖厂商。

## 影响

- `ReaderProfile`、SQLite Entity、端点唯一性规则和 WPF Data Source 展示需要支持有类型的来源；
- 新增路由 SessionFactory，真实 TCP 与平台虚拟 Session 可在同一进程并存；
- 平台虚拟 Recipe 中的实例身份与设备行为分离，同一预设可创建多个独立实例；
- 应用启动时先恢复平台虚拟实例并注册 Catalog，再由 ReaderManager 激活启用的数据源；
- 平台首批只交付标准、严格标准、高速盘点和生命周期故障等预设；厂商预设只保留扩展架构，
  未实现完整高层行为前不出现在 UI；
- 报文级 Virtual Reader Core、预设和独立 Manager 的计划由 `LLRPCSharp` 仓库维护，平台不产生
  运行时项目引用。

长期新增项目继续遵守仓库现有目录边界：产品项目只放在 `src/`，测试项目只放在顶层
`tests/`，不得把 `*.Tests` 放进产品项目目录或与源码文件混放：

```text
src/
├── LlrpReaderPlatform.VirtualReader/
├── LlrpReaderPlatform.VirtualReader.Extensions.Impinj/   （未来）
└── LlrpReaderPlatform.VirtualReader.Extensions.Zebra/    （未来）

tests/
├── LlrpReaderPlatform.VirtualReader.Tests/
├── LlrpReaderPlatform.VirtualReader.Extensions.Impinj.Tests/  （未来）
└── LlrpReaderPlatform.VirtualReader.Extensions.Zebra.Tests/   （未来）
```

Data Sources/WPF 行为继续由现有 `tests/LlrpReaderPlatform.App.Wpf.Tests` 覆盖，跨层架构边界
继续由 `tests/LlrpReaderPlatform.Architecture.Tests` 守护，不为同一职责重复建立测试项目。
