# 真机验收运行手册

本手册用于把代码能力转化为设备矩阵中的可追溯结论。自动化测试使用 `FakeSession`，不能替代本手册的真实设备结果；未完成记录前，不得把能力标记为已验收。

## 1. 验收前准备

1. 记录设备厂商、型号、固件、LLRP 端口、协议版本、天线和可用标签；
2. 确认设备处于可恢复的测试配置，保存原始设置或导出旧配置；
3. 确认 Windows 主机能访问 Reader TCP 端口，关闭会抢占该端口的旧工具；
4. 执行自动化基线：

```powershell
dotnet build LlrpReaderPlatform.slnx
dotnet test LlrpReaderPlatform.slnx --no-build
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj
```

现场也可以直接使用当前 Windows x64 自包含单文件包：运行
`src/LlrpReaderPlatform.App.Wpf/bin/Portable/LLRPReaderPlatform-win-x64/LlrpReaderPlatform.exe`。
该包不要求目标机预装 .NET Desktop Runtime；单文件运行时可能会将 native 组件临时解压到系统
临时目录。正式 GitHub Release 的 ZIP 还会附带 README 和发布说明。

真实验收的唯一交互入口是 `LlrpReaderPlatform.App.Wpf`；不要求、不引入独立硬件验收 CLI。寻卡停止后，优先查看主窗口底部状态栏的 `Inventory.Status` 和 Inventory Runs 中的 `StopReason`，再结合平台/SDK 日志记录原因。

5. 为本次验收复制 `docs/templates/device-validation-template.md`，使用设备和日期命名记录文件。

### 1.1 GPI 接线安全

- 只使用设备手册允许的干接点或隔离信号源，先断开 Reader 射频和外部控制线，再确认 GPI 电压、电流、公共端和有效电平；不得凭端口编号猜测电气定义；
- 不要把 Reader 的 GPO 直接短接到 GPI，除非设备手册明确允许且已确认电平兼容；优先使用隔离继电器或受控测试夹具；
- 触发验收前记录原始 GPI 配置和当前电平，测试结束恢复 Start/Stop 触发器、去抖时间和外部接线；
- 物理输入测试只在确认 Inventory 可安全停止、标签和射频区域可控后执行；Reserved Bank、EPC Bank 等写入必须另有可恢复标签和回读证据。

## 2. 基础连接与能力（L1）

1. 在 Data Sources 中输入 Host、Port、LLRP 版本和名称；
2. 执行 Probe，确认身份、型号、固件和协议错误均能显示；
3. 添加设备并启用，确认列表状态、能力版本和天线数量更新；
4. 禁用后确认 TCP 连接释放，再次启用确认可以重新激活；
5. 记录 Probe、Activate、Deactivate 的耗时、错误和最终状态。

## 3. Settings（L2/L3）

1. 打开 Settings，执行 Query，确认布局只显示设备能力支持的项；
2. 执行 Load Defaults，确认默认值只进入编辑器，不会直接下发；
3. 记录当前设置，先修改一个可安全回滚的字段（例如 Report Every 或测试天线）；
4. 保存并确认服务完成 Validate → Apply → Query 回读；
5. 关闭设置页重新打开，确认值与设备回读一致；
6. 恢复原值并再次 Query，确认恢复成功；
7. Inventory 运行期间尝试保存设置，必须显示 ReaderBusy，不能隐式停止盘存。

## 4. Inventory（L2）

1. 放置已知 EPC 标签并确认天线、射频和区域配置安全；
2. 点击 Start，确认同一次运行建立一个 TCP Session 并进入 Inventory 状态；
3. 观察 EPC、Count、First/Last Seen、RSSI、天线、信道和 PC Bits；
4. 确认 UI 使用原生 ProgressBar，标签刷新不会触发重新连接；
5. 点击 Stop，确认 ROSpec 停止、TagLog/InventoryRun 写入完成、TCP 断开；
6. 用时长参数重复一次，确认自动 Stop 与手动 Stop 结果一致；
7. 记录是否收到 TagReport、唯一标签数、总读取数和日志文件路径。

## 5. Tag Access 与 GPI/GPO（L3）

前置：先记录 ReaderCapabilities 中的 Tag Access 标志和 General Device Capabilities 的 GPI/GPO 数量；明确报告不支持或数量为 0 时，平台应显示降级状态，不把服务拒绝当成网络故障。
1. 使用明确的 EPC、Memory Bank、Word Pointer、Word Count 和 Access Password 执行 Read；
2. 对测试标签写入可恢复的测试数据，读取回验证后恢复原数据；
3. Inventory 运行期间执行 Read/Write，必须返回 ReaderBusy 且不影响正在运行的 Inventory；
4. 读取 GPI 状态，逐个测试 GPO 开关；
5. 如设备支持，在 Settings 中启用并保存 GPI Start/Stop 触发器；平台会同时开启标准 GPI_EVENT 通知；
6. 重新启动 Inventory，按下面顺序分别验证 GPI Start/Stop 触发和去抖行为：先保持输入在非触发电平，启动一次运行；改变指定端口到触发电平，确认先出现 GPI 状态事件，再出现 `InventoryLifecycleState.Stopped`；恢复非触发电平后等待去抖窗口，再重复一次，确认短脉冲不会误触发；
7. 确认 `LifecycleChanged`、运行记录、TagLog 和 UI 状态统一收尾；GPI Stop 的期望顺序是 Stop → 排空报告/日志 → 完成运行记录 → 断开 Session，不能只看按钮状态；
8. 记录 GPI 事件的端口、状态、Reader 时间戳，以及平台日志中的触发器匹配行，确保 UI 状态、生命周期和设备事件可以对齐；
9. 记录设备拒绝、不支持或权限错误的原始信息。

### 5.1 Tag Access 写入分级

按以下顺序执行写入，任一步无法回读原值就停止后续写入：

1. User Bank：读取原值，写入临时值，回读临时值，写回原值并再次回读；
2. EPC Bank：只有在已准备备用标签、记录原 EPC 且设备允许安全恢复时执行；恢复后必须重新用新旧 EPC 各验证一次；
3. Reserved Bank：默认不执行；只有明确拥有访问密码、设备手册和恢复方案时才允许单独批准；
4. 每个 Bank 写入都要记录 Selection Bank、目标 EPC/TID、Memory Bank、Offset、Word Count、原值、临时值、恢复值和最终回读。

## 6. 异常、多 Reader 与退出

1. 运行 Inventory 时先记录当前 RunId 和 Reader 状态，再拔网线或重启 Reader，确认运行记录以 `DeviceDisconnected`/`ConnectionFaulted`/`ReaderException` 结束、状态变为 Faulted、后台任务不再增长；
2. 设备恢复后执行一次显式 Activate，再 Start；确认使用干净 Session，旧 Session 的晚到 Tag/GPI/生命周期事件不会污染新 Run；
3. 同时启动两台 Reader，确认各自的标签、运行记录和连接互不混淆；单独停止其中一台时，另一台仍继续运行；
4. 在两台 Reader 并行时让其中一台断网，确认只有对应 Reader 进入故障收敛，另一台仍可接收 TagReport；
5. 在 Inventory、TagLog 写入和设置操作期间关闭 WPF，确认异步 DI 容器释放完成且无未观察异常；
6. 删除 Reader 后重新启动应用，确认设备和关联运行状态不会被旧事件重新创建。

## 7. 验收记录与放行门槛

每个用例记录：日期、设备身份、软件版本、操作步骤、结果、错误、截图/日志路径和是否恢复原配置。只有当设备矩阵和验收记录同时更新后，才允许提升设备的 L1～L4 状态。

Impinj 的 FastID、TID、Search Mode、Phase、Doppler、Low Duty、固定频率和 GPI debounce 必须单独记录；标准 Reader 不得因为 Impinj 扩展字段未出现而判定标准能力失败。
