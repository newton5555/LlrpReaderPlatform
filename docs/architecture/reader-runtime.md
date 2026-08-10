# Reader 生命周期与连接所有权

## Session 所有权

每个 Reader 对应一个 `ReaderHandle`。`ReaderHandle` 是该 Reader 在当前应用进程内唯一的 Session 所有者，任一时刻最多存在一个活动 TCP Session。

所有操作经过该 Reader 的异步 Gate。公开操作在获取 Reader Gate 前先短暂取得 registry gate，
因此 Remove 一旦开始清理，不会再有旧调用绕过注册表进入已释放的 Handle/Session；不同 Reader
之间仍只竞争各自的 Reader Gate，不会被全局 registry gate 串行化：

- Probe：使用临时 Session，不注册到 Fleet；
- Activate：连接、读取身份/能力、更新 RuntimeSnapshot、断开；设置 Query/Apply 由独立短租约完成；
- Settings、Tag Access、GPO：通过短连接租约完成，用完即断；Tab2 的 GPI/GPO 组合状态读取在同一个短租约内完成一次 Query；
- Tag Access 是否出现在能力目录、以及读写操作是否允许执行，以 ReaderCapabilities 的
  `IsTagAccessAvailable` 为准；设备明确报告不支持时，服务返回稳定的“不支持”结果，不把
  设备拒绝误报为网络故障；
- GPI/GPO 是否出现在能力目录，以标准 General Device Capabilities 的 GPIO 数量为准；
  明确为 0 时分别移除对应能力，未返回该参数时保留未知能力的兼容回退；
- Inventory：`Start` 建立一个完整 LLRP 长连接租约，并一直持有到 `Stop`；期间所有 TagReport 都来自这一个 Session，不允许按报告或按批次重新连接；
- TagReport 先进入有界 Channel，由单消费者聚合；启用 Tag Logging 时再进入独立有界 TagLog Channel，由单消费者串行写入，避免高频报告创建无界后台 Task；
- Inventory `Stop`：停止 ROSpec/InventorySession、断开 TCP、释放 Gate 后才发布 `Disconnected`；
- Disable/Remove：取消操作、停止盘存、断开并释放 Session。

新增 Reader 的重复注册检查、Profile 持久化和 Session 注册在同一个 registry gate 内完成；已注册的同一 Guid 不会被新 Profile 覆盖。Remove 也会持有同一 gate，直到旧 Handle 从注册表移除且 Profile 删除完成，避免同 Guid 的新 Add 被旧删除补偿误删。

启动恢复时若 Reader 在 Probe 阶段离线，仍注册标准 Session 以保留用户配置；首次重新激活会重新执行标准 Probe，并在身份匹配后替换为带厂商扩展的 Session。替换期间旧 Session 的迟到事件不得影响当前 Reader 生命周期。

ReaderException、一般 Connection Faulted 和设备主动断连事件只负责把故障收敛投递到后台任务；Stop/Drain/Disconnect 不在 SDK 协议消息泵回调线程中执行，避免异常清理阻塞后续 LLRP 消息处理。故障收敛会结束当前 InventoryRun、释放长连接租约并保留 Handle，后续 Activate 或 Start 可以重新建立连接。

## 状态

用户意图与运行状态分离：

```text
IsEnabled       用户意图，持久化
ConnectionState Disconnected / Connecting / Connected / Disconnecting / Faulted
OperationState  Idle / Configuring / Inventorying / Accessing / Stopping
```

## 冲突策略

Inventory 运行时，Settings、Tag Access 和 GPO 默认返回 `ReaderBusy`，不隐式停止盘存、不抢占长连接。用户需要先显式停止 Inventory。

Disable 和 Remove 属于控制操作，可以取消当前工作，随后执行清理。所有取消、断开和 Dispose 路径必须可重复执行。

## 状态事件

Services 不捕获 UI SynchronizationContext。状态事件可以在后台线程发布，WPF、Avalonia 或其他 UI 消费者负责切换到自己的 UI 线程。

`IReaderManager.StateChanged` 描述 Reader 的连接/能力快照；`IInventoryService.LifecycleChanged`
是 Inventory 长租约的唯一生命周期事实来源。手动 Stop、GPI 触发、定时结束、设备断连、
ReaderException、Deactivate、Remove 和应用退出都发布停止事件，并携带稳定的
`InventoryStopReason`。UI 不应从 `Disconnected` 状态或按钮返回值推断盘存是否结束，
而应消费该事件统一收敛运行状态、计时器和运行记录展示。

当前 SDK 没有独立的“设备已开始盘存/设备已停止盘存”事件；平台将 SDK 成功接受
`StartInventoryAsync` 视为长租约已建立并发布 `Started`，将平台发出的 Stop、匹配的
GPI Stop、定时结束或连接/ReaderException 故障收敛视为 `Stopped`。SDK 的 `GpiChanged`
仅表示输入状态变化，服务层会先把它投影给 UI，再由当前 Reader 的触发器匹配结果决定
是否排队停止。设置编译器在启用任一 GPI 启停触发器时同步打开标准
`GPI_EVENT` 通知，避免设备只保存触发器而不发送状态事件。因此 GPI 状态展示和盘存
生命周期展示都走事件，但职责分别由 `GpiChanged` 与 `LifecycleChanged` 承担。
