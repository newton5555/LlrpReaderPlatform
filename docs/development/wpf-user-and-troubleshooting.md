# WPF 用户操作与故障排查

本文面向首个验收项目 `LlrpReaderPlatform.App.Wpf`。WPF 只通过平台服务执行 Reader 操作；短连接设置、诊断和 Tag Access，以及寻卡期间的长连接生命周期，均由 Services 统一管理。

## 1. 启动与数据位置

开发运行：

```powershell
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj
```

现场便携运行：

```text
src/LlrpReaderPlatform.App.Wpf/bin/Portable/LLRPReaderPlatform-win-x64/LlrpReaderPlatform.exe
```

当前便携包为 Windows x64 自包含单文件，不需要目标机预装 .NET Desktop Runtime；正式 Release
ZIP 还包含 README 和发布说明。单文件启动时 native 组件可能临时解压到系统临时目录，属于正常行为。

默认数据和日志位置：

- SQLite：`%LocalAppData%\LlrpReaderPlatform\llrp-reader-platform.db`；首次访问持久化服务时自动执行 EF Core migration；
- WPF 操作日志：`%LocalAppData%\LlrpReaderPlatform\logs\ui-*.log`；
- 平台日志：`%LocalAppData%\LlrpReaderPlatform\logs\platform-*.log`；
- SDK/LLRP 日志：`%LocalAppData%\LlrpReaderPlatform\logs\sdk-*.log`；
- 盘存最终快照：默认位于 `%LocalAppData%\LlrpReaderPlatform\inventory-snapshots`；原始 JSONL 目录由 Software Settings 配置。

应用退出时会先取消各页面尚未完成的网络/数据库操作，再停止运行中的 Inventory、等待已入队的 TagReport/TagLog 完成，最后断开 Reader 并异步释放 DI 容器。取消不会被显示为设备故障。不要在应用仍运行时复制或删除 SQLite 文件。

## 2. 添加和激活 Reader

1. 打开 Data Sources，输入名称、Host、LLRP 端口和协议版本策略；
2. 点击 Probe，确认厂商、型号、固件和能力信息；
3. 点击 Add/Save，设备 Profile 会写入 SQLite；
4. 选中 Reader 后执行 Activate。Activate 是一次短连接，用于同步身份、能力和厂商扩展；完成后连接会释放，列表显示“已同步能力”，这不是 Inventory 长连接；
5. 只有点击寻卡 Start 后才会建立持续的 Inventory Session；Stop 会停止 ROSpec、完成排空并断开同一 Session；
6. 删除或禁用 Reader 前先停止寻卡。

如果 Activate 失败，先查看列表中的错误详情和 `ui-*.log`；平台生命周期/持久化问题查看 `platform-*.log`，协议或厂商扩展问题查看 `sdk-*.log`。

EF Core SQL 和参数默认不写入文件日志，仅保留数据库 Warning/Error。

## 3. 设备设置 Tab1 和 Tab2

设备设置以旧 `LlrpReaderStudio.Wpf` 的分组为操作入口，但值的编辑和下发由平台 Contracts/Services 完成。

Tab1（Inventory）包括：

- Manual、天线集合、RF Mode、Session、Tag Population、Report Every；
- 全局 Tx/Rx 与 Individual Antennas；
- GPI Start/Stop、触发电平、超时和 Debounce；
- Gen2 Filter 1/2、State-aware Singulation；
- Frequency/Channel List、Low Duty Cycle、Report、Other；
- 已识别 Impinj Reader 的 FastID/TID、Search Mode、Phase、Doppler、固定频率等扩展项。

Tab2（Diagnostics）包括四路 GPO 开关和 GPI 状态刷新/事件显示。GPI 配置仍在 Tab1 的 GPI CONFIGURATION 分组中，GPO 控制不需要另开独立页面。

设置操作遵循以下顺序：Query → 编辑 → Validate → Apply → Query 回读。Load Defaults 只更新编辑器，不会直接下发。切换左侧 Reader 时，旧 Reader 的 Query/Defaults/回读会取消或丢弃，避免慢响应覆盖当前设备页面；如果能力快照过期或 Reader 进入故障/断开状态，设置行仍可查看但编辑和 SAVE 会暂时禁用，先从左侧重新 Activate。若 Apply 已成功但回读只能得到 SQLite 只读缓存，页面会明确显示“保存成功，但设备回读失败”，不会把缓存冒充成设备当前值。Inventory 运行时保存设置、GPO 和 Tag Access 会返回 ReaderBusy，不能隐式停止或重启寻卡。

## 4. 寻卡与标签数据

1. 在 Inventory 页点击 Start；默认使用 Reader 当前天线配置，不会偷偷限制为天线 1；
2. 在 DataGrid 任一列头上右键，使用列头菜单选择 EPC、TID、PC Bits、Peak RSSI、天线、信道、时间和 Count；Tag List 是平台附加列；
3. 运行期间观察旧 WPF 风格的 ELAPSED TIME、Unique Tags、Read Rate 和 Dropped 状态；原生 ProgressBar 表示正在执行异步生命周期操作；
4. 点击 Stop 或达到时长后，服务停止 Inventory、排空已接收报告、写入 Inventory Run/最终快照，并断开 Reader；若应用设置选择 `RawReports`，还会写入原始 JSONL Tag Log；
5. Clear 只清除当前展示和内存聚合，不删除历史 Inventory Runs。

若没有标签报告，按顺序检查：标签是否在天线有效范围内、天线是否接好、设备是否启用、Inventory 天线集合和区域设置、Reader 是否真的进入运行状态；随后查看 SDK 日志确认设备是否发出了 TagReport。自动化测试中的 FakeSession 报告不能替代真实标签验收。

### 盘存数据记录模式

应用设置中的盘存数据记录模式有三种：`Off` 不生成盘存数据文件；`FinalSnapshot` 为默认模式，
停止后保存最终聚合标签快照；`RawReports` 同时保存最终快照和原始报告 JSONL，适合短时间现场诊断。

## 5. Tag Memory 与 Tag Lists

Tag Memory Read/Write 需要提供 EPC、Memory Bank、Word Pointer、Word Count、数据和必要的 Access Password。平台会在发起 SDK 请求前校验 Bank、地址、长度、十六进制格式和写入数据长度；请求超时或未匹配标签不会被显示成成功。

Tag Lists 用于维护 EPC 与显示名称的匹配，启用后的列表会在寻卡展示中提供 Tag List 标签。修改 Tag List 不会修改 Reader 内部配置。

## 6. 常见故障排查

### 添加页操作被取消或不再自动切页

- Probe、mDNS 发现和提交是互斥操作；一个操作进行中，其他按钮不会再次启动同类流程；
- 点击 CANCEL 会先取消添加页的在途操作，再返回上一页；取消不会写入 Profile，也不会把取消显示为设备故障；
- 若需要查看刚刚的 Probe 结果，重新进入 Add Data Source 后再次 Probe。

### 显示“未发现设备”

- 手动 Probe 时确认 Host、端口和防火墙；
- mDNS 发现失败会显示发现错误，不要把网络异常当成“没有设备”；
- 确认没有旧工具占用 Reader 的 TCP 端口；
- 直接 Probe 使用标准 LLRP，Impinj 扩展只在标准身份识别后第二阶段启用。

### 显示“连接成功”但列表不是 Connected

这是短连接设计的正常表现。Activate 完成后会断开 TCP，只保留能力快照；列表应显示“已同步能力”。Connected/Inventory 状态只在长时间寻卡期间存在。

### 保存后值没有变化

- 确认没有处于 Inventory；
- 点击 Query 确认当前 Reader 配置；
- 检查该字段是否只读或设备能力不支持；
- 如果页面提示连接未可靠释放并进入只读，先从 Data Sources 重新 Activate，再刷新设置；不要在该状态下反复点击 SAVE；
- 保存后必须等待 Validate、Apply 和回读完成；
- 若回读失败，使用 `platform-*.log` 和 `sdk-*.log` 的同一时间戳定位原始错误。

### 操作提示“ReaderBusy”或“设备操作进行中，请稍候”

ReaderBusy 表示该 Reader 当前拥有 Inventory 长租约；后者表示同一 WPF 操作尚未完成。等待 ProgressBar 消失后重试，不要连续点击或强制关闭应用。

### GPI 状态没有变化

先点击 Refresh GPI 确认短连接读取结果，再确认设备 GPI 接线、配置和电平。若要验证寻卡期间的主动事件，必须先在 Tab1 保存 GPI Start/Stop 配置；平台会同时开启标准 GPI_EVENT 通知。设备主动事件只有在 Reader 会话仍由平台持有时才会投影到 UI；短操作结束后再次刷新是可靠的确认方式。

### 应用退出或设备断连后状态异常

等待故障收敛完成后重新 Activate；若仍失败，停止并重新开始 Inventory。查看运行记录的 StopReason、平台日志和 SDK 日志。

### SQLite 数据库无法打开或 schema 异常

新平台数据库只保存新平台自己的数据。开发和早期验收阶段，如果数据库 schema 异常，可以关闭应用后备份并删除
`%LocalAppData%\LlrpReaderPlatform\llrp-reader-platform.db`，重新启动让 EF SQLite 建立空库；这会丢失本地 Reader、设置快照、Tag Lists、运行记录和应用设置。

## 7. 真机验收边界

软件链路和自动化测试通过，不等于某个型号已经完成硬件支持声明。当前 R420 记录已覆盖标准 Probe/Settings Query、WPF 设置回写、GPO、Impinj GPI Debounce、真实 TagReport、EPC/TID/User/Reserved Memory Bank 读取、User Bank 写入恢复、FastID/Phase 扩展字段和 Inventory Start/Stop/Disconnect；GPI 事件/触发、其它 Memory Bank 写入、多 Reader 和故障恢复仍按[设备矩阵](../compatibility/device-matrix.md)与[真机验收运行手册](hardware-validation-runbook.md)逐项记录。
