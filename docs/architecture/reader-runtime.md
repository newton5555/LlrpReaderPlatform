# Reader 生命周期与连接所有权

## Session 所有权

每个 Reader 对应一个 `ReaderHandle`。`ReaderHandle` 是该 Reader 在当前应用进程内唯一的 Session 所有者，任一时刻最多存在一个活动 TCP Session。

所有操作经过该 Reader 的异步 Gate。公开操作在获取 Reader Gate 前先短暂取得 registry gate，
因此 Remove 一旦开始清理，不会再有旧调用绕过注册表进入已释放的 Handle/Session；不同 Reader
之间仍只竞争各自的 Reader Gate，不会被全局 registry gate 串行化：

- Probe：使用临时 Session，不注册到 Fleet；
- Activate：连接、读取身份/能力、更新 RuntimeSnapshot、断开；设置 Query/Apply 由独立短租约完成；
- Settings、Tag Access、GPO：通过短连接租约完成，用完即断；启动恢复或故障后的陈旧 Session 在短操作入口自动执行必要的标准 Probe/扩展匹配和能力捕获，不要求先打开设置页；Tab2 的 GPI/GPO 组合状态读取在同一个短租约内完成一次 Query；
- Tag Access 是否出现在能力目录、以及读写操作是否允许执行，以 ReaderCapabilities 的
  `IsTagAccessAvailable` 为准；设备明确报告不支持时，服务返回稳定的“不支持”结果，不把
  设备拒绝误报为网络故障；
- GPI/GPO 是否出现在能力目录，以标准 General Device Capabilities 的 GPIO 数量为准；
  明确为 0 时分别移除对应能力，未返回该参数时保留未知能力的兼容回退；
- Inventory：`Start` 建立一个完整 LLRP 长连接租约，并一直持有到 `Stop`；期间所有 TagReport 都来自这一个 Session，不允许按报告或按批次重新连接。Services 的 SDK 适配器不注册 `LlrpReader.TagsReported`，而是在 `StartInventoryAsync` 返回后后台持续消费唯一的 `InventorySession.ReadReportsAsync()` 出口，再投影为平台 `TagReported` 事件；同一次盘存不得混用 SDK 的三种报告出口；
- Inventory 入口：服务层先拒绝无效时长、重复天线以及“全部天线(0)”与指定天线混用的参数，不为无效请求建立 LLRP 连接；
- TagReport 先进入容量 8,192 的有界 Channel，由单消费者按最多 512 条一批聚合；原始报告只原位更新聚合状态，同一批末尾才为每个 Reader/EPC 创建一次快照并发布最新累计状态，不把每条原始报告变成一次 UI 通知。选择 `RawReports` 时再进入独立有界 TagLog Channel，由单消费者串行写入；TagLog 保留每条原始读取的累计记录，但不改变正常 UI 路径的批量快照策略；默认 `FinalSnapshot` 只在停止排空后写入一次聚合快照；
- Inventory `Stop`：停止 ROSpec/InventorySession、断开 TCP、释放 Gate 后才发布 `Disconnected`；如果 Stop 或断开失败，则发布 `StopFailed` 并保留 `Faulted + IsStale`，不把未确认释放的长连接伪装为正常离线；
- Disable/Remove：取消操作、停止盘存、断开并释放 Session。

新增 Reader 会先对运行时端点做快速去重，再在 registry gate 内复核 Guid 和持久化端点；重复端点统一返回 `PlatformErrorCode.AlreadyExists`，不会落到 SQLite 唯一索引异常。Profile 持久化和 Session 注册仍在同一 registry gate 内完成，已注册的同一 Guid 不会被新 Profile 覆盖。Remove 也会持有同一 gate，直到旧 Handle 从注册表移除且 Profile 删除完成，避免同 Guid 的新 Add 被旧删除补偿误删。

启动恢复时若 Reader 在 Probe 阶段离线，仍注册标准 Session 以保留用户配置；首次重新激活会重新执行标准 Probe，并在身份匹配后替换为带厂商扩展的 Session。故障、停用或 Inventory 启动失败也会使下一次激活/短操作重新解析扩展，因此同一网络地址更换 Reader 后不会复用旧设备的扩展判断。替换期间旧 Session 的迟到事件不得影响当前 Reader 生命周期。

`ReaderRuntimeSnapshot.ActiveExtensionIds` 与能力捕获同步更新，记录当前 Session 选择的扩展模块稳定 Id；空集合表示标准 LLRP 路径。该字段只用于运行时诊断和 UI 投影，不写入 SQLite，也不替代 `ReaderFeatureCatalog` 对具体能力的声明。

ReaderException、一般 Connection Faulted 和设备主动断连事件只负责把故障收敛投递到后台任务；Stop/Drain/Disconnect 不在 SDK 协议消息泵回调线程中执行，避免异常清理阻塞后续 LLRP 消息处理。故障收敛会结束当前 InventoryRun、释放长连接租约，并把保留的能力快照标记为 `IsStale=true`，同时要求下一次激活/短操作重新执行标准 Probe 与扩展匹配；后续 Activate 或 Start 可以重新建立连接，WPF 在重新激活前不得把陈旧能力当作当前可操作能力。故障 Session 不会被下一次操作复用：平台会先回收旧 Session，再创建干净 Session；若恢复 Probe 失败，则保留 Faulted 快照并在下一次显式 Activate/Start 重试。

短租约的 SDK 调用即使没有同步触发 `ConnectionFaulted`，服务也会在 Settings、Tag Access、GPI/GPO 操作抛出设备/传输异常时主动执行 Disconnect，并将快照标记为 `Faulted + IsStale`、要求下次短操作重新探测；如果操作完成但短租约断开失败，同样不能继续保留 `Connected`/新鲜能力快照。取消、`ReaderBusy`、不支持能力和本地设置编译/端口校验错误不进入该故障收敛路径。

## 状态

用户意图与运行状态分离：

```text
IsEnabled       用户意图，持久化
ConnectionState Disconnected / Connecting / Connected / Disconnecting / Faulted
OperationState  Idle / Configuring / Inventorying / Accessing / Stopping
```

## 冲突策略

Inventory 运行时，Settings、Tag Access 和 GPO 默认返回 `ReaderBusy`，不隐式停止盘存、不抢占长连接。用户需要先显式停止 Inventory。

`ReaderBusy` 等跨消费者可处理的异常语义使用 Contracts 中的
`PlatformOperationException` 携带 `PlatformErrorCode`；Services 可以保留具体异常类型，
但 WPF 或未来其他 UI 不需要引用 Services 实现程序集，也不需要解析底层错误文本。

`SetEnabled(false)`、Disable 和 Remove 属于控制操作，可以取消当前工作，随后执行清理；停用清理由 ReaderManager 统一编排，UI 不需要再补调用 `DeactivateAsync`。所有取消、断开和 Dispose 路径必须可重复执行。

## 状态事件

Services 不捕获 UI SynchronizationContext。状态事件可以在后台线程发布，WPF、Avalonia 或其他 UI 消费者负责切换到自己的 UI 线程。

高频 TagReport 的 UI 投影属于消费者职责。当前 WPF 按 Reader/EPC 合并最多 2,000 个待刷新项，
以 250ms 的 `DispatcherTimer` 每次最多更新 25 行，并最多保留 1,000 个可见 EPC；已存在行原地更新，
不得为每个报告调用 Dispatcher、线性扫描整个 DataGrid 或替换整行对象。这些上限只约束实时视图，
不会改变 Reader Session、后台聚合、InventoryRun 收尾或可选 TagLog 的生命周期。

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
`GpiChanged` 的平台投影保留设备事件时间戳；服务日志同时记录 Reader、端口、状态和
匹配到的 GPI Stop 触发器，便于 WPF 状态、生命周期记录和真机验收日志对齐。
