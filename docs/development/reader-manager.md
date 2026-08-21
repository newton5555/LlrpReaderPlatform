# LlrpReaderManager 设计与开发模式

`src/LlrpReaderManager` 是本仓库的 MAUI Blazor Hybrid 消费者。它复用当前平台的
Contracts、Services、Infrastructure 和厂商扩展，不复制 WPF 的协议编排，也不直接拥有真实
Reader Session。视觉语言沿用归档 `LLRPReaderManagement` 的深色顶栏、侧栏、卡片和绿色强调色，
但页面结构按当前平台能力重新设计。

## 产品定位

- 桌面端用于多 Reader 管理、配置、盘存、Tag Access、GPIO 和运行记录；
- 手持端使用相同业务状态和服务，只改变导航、卡片排列和明细呈现；
- WPF 仍是当前真机验收主客户端，Blazor 消费者不得增加专属协议分支；
- Reader 页面可启动一个 SDK 报文级 Virtual Device Host，并把实际 loopback endpoint 当作普通
  Reader 注册。虚拟 Host 是开发挂件，不形成第二套 Reader 生命周期。

## Reader 生命周期

Reader 是否启用与 TCP 是否连接是两个不同状态：

1. 添加只保存 `ReaderProfile`；选择启用时执行一次 Activate，完成 Probe、能力同步和扩展匹配后断开；
2. Settings、Tag Access 和 GPIO 是短操作租约：连接、执行、收敛结果、断开；
3. Inventory 是长操作租约：Start 后保持唯一 Session，直到手动、定时、GPI、设备事件或故障停止；
4. 页面导航和 Reader 选择只读取内存快照，不隐式连接设备；
5. 停用、移除和应用退出负责停止活动租约并释放 Session/Host。

平台 Services 是上述生命周期的唯一事实来源。Razor 页面只发送命令并显示快照、生命周期事件和错误，
不创建 SDK Reader、不复用 TCP 对象，也不根据按钮点击自行猜测设备状态。

## 当前问题与设计结论

首版已覆盖 Reader 发现/添加、Settings、Inventory、Tag Memory、Runs、TOI、GPI/GPO 和 Virtual
Device widget，但 UI 状态仍需收敛：

- `ReaderManagerState` 的全局 Gate 和 `IsBusy` 会让一台 Reader 的短操作阻塞全部 Reader 的按钮；
- 页面各自保存 Reader 选择，缺少稳定的当前 Reader 上下文和每台 Reader 的操作状态；
- Settings 的通用 `SettingsEntry` 映射适合作为能力驱动底座，但天线、过滤器、GPI 和频点需要专用卡片编辑器；
- Inventory 的多 Reader 启停需要逐台反馈，不能等全部任务完成后才更新整个页面；
- Tag Memory 只有最终文本，无法审阅一次 Tag Access 的请求、连接生命周期、耗时和平台错误码；
- 全局 busy 提示不能表达“哪台 Reader 正在执行什么”，移动端也缺少稳定的主操作区。

按钮无响应问题已经定位为 `_Imports.razor` 缺少 `Microsoft.AspNetCore.Components.Web`，导致
`@onclick` 等 Web 事件未建立有效绑定；该导入属于 Hybrid 交互管线，不是 Reader 服务问题。

## 目标 UI 结构

### 应用外壳

- 顶栏显示平台状态、活动 Reader 数和当前 Reader；窄屏下 Reader 选择器进入页面标题区；
- 侧栏只负责导航，不触发连接；移动端转换为横向主导航或底部导航；
- 全局只显示启动失败、持久化失败等应用级错误。设备操作状态显示在对应 Reader 卡片和页面动作区；
- 每个 Reader 至少展示 `Enabled`、运行状态、当前操作、最近错误和最后能力捕获时间。

### Readers

发现、手动添加和已注册 Reader 分区展示。每台 Reader 是独立卡片，可启用、停用、移除并查看能力摘要。
Virtual Device 使用单独的开发卡片，Host 启动成功后仍通过普通 Reader 卡片管理。

### Inventory

单 Reader 操作区与多 Reader 批量操作分开。Start/Stop 逐台产生状态，已启动的 Reader 立即接收并显示
标签，不等待其他 Reader。实时表使用有界、合并刷新，页面切换只重新订阅现有聚合快照，不重建 Run。

### Reader Settings

页面采用能力驱动卡片，而不是平铺所有字段：

- Inventory 与 RF：Session、Population、Mode、Tari、报告策略；
- 功率与天线：共享设置和逐天线设置，显示能力表的 `index (display value)`，写入仍为 index；
- Tag Filters 与 State-aware：按启用状态展开相关字段；
- GPI triggers：开始/停止条件和设备支持的扩展项；
- Frequency：跳频/定频模式及频点多选；
- Vendor：仅显示适用扩展，optional 值不作为保存必填项。

顶部保留 Reader 选择、从设备刷新和加载 SDK 默认值；底部使用固定的“未保存修改 / 保存 / 放弃”动作条。
通用 Entry 渲染器继续作为兜底，专用卡片只负责编辑体验，不复制设置编译和协议校验。

### Tag Memory 操作工作台

Tag Memory 独立于 Inventory 页面，一次操作是完整的短连接租约。页面分为四个区域：

1. **目标**：Reader、EPC/TID 匹配类型、手动可编辑目标，以及当前 Reader 已观测 EPC/TID 的候选列表；
2. **请求**：Read/Write、Memory Bank、Word Pointer、Word Count、Data、Access Password；
3. **本次结果**：操作类型、Reader endpoint、开始/结束时间、耗时、目标、请求摘要、成功状态、
   `PlatformErrorCode`、错误消息和返回 Data；
4. **会话历史**：保留本次应用会话最近的操作，选择记录后查看完整明细，不把 UI 历史误当作设备配置。

首版只开放平台已经实现的 Read/Write。Lock、Kill、BlockErase 等只有在 Contracts/Services 有稳定能力声明和
真机验证后才加入，不在 Razor 中预留假按钮。页面需要明确显示 ReaderBusy、超时、未匹配标签和设备拒绝，
不能统一折叠成“操作失败”。

### GPIO、Runs 与 TOI

- GPIO：端口状态卡、GPO 命令区和 GPI 事件时间线；
- Runs：Reader/时间/停止原因筛选，汇总表与单次 Run 明细抽屉；
- TOI：只做名称、颜色和标签条目的 CRUD，并在 Inventory 表中投影颜色，不承担虚拟设备 Tag Pool。

## UI 状态边界

`ReaderManagerState` 只保存跨页面共享的 Reader 快照、当前选择和事件投影。后续以 ReaderId 为键维护轻量
`ReaderOperationState`，记录操作名称、开始时间和错误；同一 Reader 的冲突仍由 Services Session Gate
裁决，不在 UI 再建一套协议锁。发现和数据库维护可使用应用级操作状态。

Settings draft、Tag Memory 表单/历史、Runs 筛选等属于页面状态，不进入平台 Contracts。高频 TagObserved
继续在 UI 适配层合并刷新，页面渲染频率与 SDK 报告消费频率解耦。

## 实施顺序

1. **交互基线**：修复 Hybrid 事件导入，验证导航、普通按钮和异步按钮；
2. **状态基线**：用每 Reader 操作状态替代全局设备 busy，保留平台 Session Gate；
3. **Reader 与 Shell**：统一 Reader 上下文、状态卡和桌面/移动导航；
4. **Settings**：落地专用卡片、dirty 状态和固定保存动作条；
5. **Inventory**：逐 Reader 启停反馈、批量操作摘要和高频表格刷新；
6. **Tag Memory**：请求工作台、结构化结果、错误码和会话历史；
7. **GPIO / Runs / TOI**：补齐明细、筛选和响应式布局；
8. **验收**：Windows 桌面、Android 手持、虚拟设备和真实 Reader 按同一服务语义验证。

## 依赖、构建与启动

开发机需要 .NET 10 SDK、MAUI Android/Windows/Mac Catalyst workload。仓库默认通过中央包版本管理引用 SDK NuGet；
跨仓库联调可由被忽略的 `Directory.Build.local.props` 启用本地 `LLRPCSharp` 项目引用。

Windows 开发验证：

```powershell
dotnet restore src/LlrpReaderManager/LlrpReaderManager.csproj
dotnet build src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
dotnet run --project src/LlrpReaderManager/LlrpReaderManager.csproj -f net10.0-windows10.0.19041.0
```

Android 使用相同项目和 `net10.0-android` TFM。响应式 CSS 只改变布局，不分叉业务状态。

macOS 使用 `net10.0-maccatalyst` TFM，在 macOS 和 Xcode 环境中发布；当前正式流水线按
`maccatalyst-x64` 与 `maccatalyst-arm64` 生成未签名应用包。Windows、Mac Catalyst 和 Android
的发布入口统一维护在 `.github/workflows/release.yml`，不对整个解决方案执行 MAUI 发布。

### Mac Catalyst 本地运行/安装

开发或验收 Mac Catalyst 包时，按 Mac 芯片选择 `maccatalyst-arm64` 或 `maccatalyst-x64`。发布 ZIP
解压后直接打开 `LlrpReaderManager.app`；首次启动按 macOS 的 Gatekeeper 提示右键“打开”或在
“系统设置 → 隐私与安全性”中允许。当前未签名包只适合开发/内部测试，不能当作已完成签名、公证的 Mac
正式分发包。测试时确认 Mac 能访问 Reader 地址和 LLRP `5084` 端口；完整的下载、首次启动和 Gatekeeper
处理见[发布规范](release.md#下载与运行)。

## 代码边界

- `State/ReaderManagerState` 是 Blazor UI 状态投影，只持有平台接口，不持有 SDK Session；
- `VirtualDevices/VirtualReaderWidgetService` 只适配 SDK 虚拟 Host 生命周期和 Reader 注册交接；
- `Components/Pages` 负责交互、表单和渲染，不编译 LLRP 配置；
- 新增设备能力优先扩展 Contracts/Services，再由 WPF 和 Blazor 共同消费。

该项目已经进入独立发布流水线，但 Windows、Mac Catalyst 和 Android 的安装、签名、真机兼容性
仍需按平台分别验收；流水线的未签名 Mac Catalyst 包不等同于 App Store 或公证发布包。
