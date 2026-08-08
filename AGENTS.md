# LlrpReaderPlatform 协作约定

## 仓库定位

- 本仓库是新的 LLRP 应用平台，当前阶段只维护规划、架构、兼容性和开发规范，不创建业务实现项目。
- `C:\Users\yankai\source\repos\LlrpReaderStudio` 是冻结的旧仓库，仅用于理解现有能力和迁移边界，不作为新仓库的运行时依赖。
- 旧基线已经覆盖标准 LLRP 1.0.1 设备和 Impinj R420；新平台必须在此基础上继续扩展更多标准设备和厂商设备。

## 解决方案与项目边界

解决方案文件为 `LlrpReaderPlatform.slnx`。未来项目按以下边界组织：

- `LlrpReaderPlatform.Contracts`：跨 UI、跨实现的领域契约和不可变数据模型；不得引用 WPF、SDK 或厂商扩展类型。
- `LlrpReaderPlatform.Services`：Reader 管理、生命周期、设置、标签流和业务编排；不得依赖具体 UI。
- `LlrpReaderPlatform.Infrastructure`：连接、持久化、发现、日志等实现细节。
- `LlrpReaderPlatform.Extensions.*`：可插拔的厂商扩展；扩展不得污染通用契约。
- `LlrpReaderPlatform.App.Wpf`：首个 WPF UI 消费者；未来可以有其他 UI 框架消费者，但 UI 不得下沉到平台核心。
- `LlrpReaderPlatform.*.Tests`：契约、服务、基础设施、扩展和消费者测试。

依赖方向必须保持为：

```text
UI consumer -> Services -> Contracts
                       -> Infrastructure
                       -> Extensions.*
```

实际实现中应避免反向引用和循环依赖；UI 只能通过平台公开服务和契约工作。

## Reader 连接与并发

- 每个 `ReaderHandle` 只允许一个拥有者和一个 TCP `ReaderSession`。
- 同一 Reader 的命令必须经过该 Session 的单一 `Gate` 串行化；LLRP TCP 连接是独占资源。
- Inventory 是长期租约；Settings、Tag Access、GPO 等短操作在冲突时返回明确的 `ReaderBusy`，不得隐式停止或重启 Inventory。
- 所有后台事件先转换为平台契约，再发布给 UI；不得把 SDK、厂商扩展或 TCP 类型泄漏到 UI。
- 厂商扩展采用“标准探测 -> 扩展匹配 -> 必要时重连”的两阶段流程，不能把设备识别绑定到单一厂商。

## 兼容性分层

- L1：连接、握手、协议版本、身份、能力探测。
- L2：标准 Inventory、EPC、RSSI、天线、信道、计数和时间信息。
- L3：标准设置、过滤器、Tag Access、GPI/GPO，按能力决定可用性。
- L4：厂商扩展，仅在明确模块和真实硬件验证后加入。

标准 LLRP 1.0.1 和 Impinj R420 是首批验收基线，但不是平台的最终设备范围。

## 文档维护

- 唯一的总体计划是 `docs/llrp-framework-vision.md`，不要另建第二份阶段计划。
- `docs/architecture/` 只记录稳定的架构决策和边界；`docs/compatibility/` 记录设备验证矩阵；`docs/development/` 记录开发与测试流程；`docs/decisions/` 记录 ADR。
- `docs/legacy/` 只记录冻结旧仓库的地址、当前架构和迁移参考，不复制旧规划文档。
- 新增或移动文档时，必须同步更新 `docs/README.md`、根 `README.md`（如涉及入口）和 `LlrpReaderPlatform.slnx`。
- 设计发生方向性变化时先补 ADR，再修改主计划和配套架构文档。

## 实现约定

- 契约项目保持 UI 无关、SDK 无关、厂商无关；设置编辑模型使用平台语义，不使用 WPF 控件名称。
- ViewModel 不直接创建连接、调用 SDK、操作窗口或承载协议编排；WPF 只消费平台服务。
- 新项目统一使用仓库根部的中央包版本管理；项目文件只声明 `PackageReference`，不重复写版本。
- 遵循现有 WPF 模板约定：CommunityToolkit.Mvvm、Microsoft.Extensions.DependencyInjection、MahApps.Metro 和 FontAwesome.Sharp 仅在实际创建 WPF 消费者项目时引入。
- 不提交 `bin/`、`obj/`、临时 `*_wpftmp.csproj` 或本地生成物。

## 验证要求

当前文档阶段至少验证：

1. `LlrpReaderPlatform.slnx` XML 可解析，登记的文件都存在。
2. Markdown 本地链接可解析，文档导航没有断链。
3. 文档中的项目边界、依赖方向、Reader 所有权和设备兼容性表述一致。

创建项目后，每次变更还应执行：

```powershell
dotnet build LlrpReaderPlatform.slnx
dotnet test LlrpReaderPlatform.slnx --no-build
```

