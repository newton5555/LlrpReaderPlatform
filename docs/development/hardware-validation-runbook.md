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

5. 为本次验收复制 `docs/templates/device-validation-template.md`，使用设备和日期命名记录文件。

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

0. 先记录 ReaderCapabilities 中的 Tag Access 标志和 General Device Capabilities 的 GPI/GPO 数量；明确报告不支持或数量为 0 时，平台应显示降级状态，不把服务拒绝当成网络故障；
1. 使用明确的 EPC、Memory Bank、Word Pointer、Word Count 和 Access Password 执行 Read；
2. 对测试标签写入可恢复的测试数据，读取回验证后恢复原数据；
3. Inventory 运行期间执行 Read/Write，必须返回 ReaderBusy 且不影响正在运行的 Inventory；
4. 读取 GPI 状态，逐个测试 GPO 开关；
5. 如设备支持，在 Settings 中启用并保存 GPI Start/Stop 触发器；平台会同时开启标准 GPI_EVENT 通知；
6. 重新启动 Inventory，分别验证 GPI Start/Stop 触发和去抖行为，确认 LifecycleChanged、运行记录和 UI 状态统一收尾；
7. 记录设备拒绝、不支持或权限错误的原始信息。

## 6. 异常、多 Reader 与退出

1. 运行 Inventory 时拔网线或重启 Reader，确认运行记录结束、状态变为 Faulted、后台任务不再增长；
2. 设备恢复后执行 Stop/Activate/Start，确认可以重新建立 Session；
3. 同时启动两台 Reader，确认各自的标签、运行记录和连接互不混淆；
4. 在 Inventory、TagLog 写入和设置操作期间关闭 WPF，确认异步 DI 容器释放完成且无未观察异常；
5. 删除 Reader 后重新启动应用，确认设备和关联运行状态不会被旧事件重新创建。

## 7. 验收记录与放行门槛

每个用例记录：日期、设备身份、软件版本、操作步骤、结果、错误、截图/日志路径和是否恢复原配置。只有当设备矩阵和验收记录同时更新后，才允许提升设备的 L1～L4 状态。

Impinj 的 FastID、TID、Search Mode、Phase、Doppler、Low Duty、固定频率和 GPI debounce 必须单独记录；标准 Reader 不得因为 Impinj 扩展字段未出现而判定标准能力失败。
