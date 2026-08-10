# ADR-0005：由平台统一发布 Inventory 生命周期事件

- 状态：已接受
- 日期：2026-08-10

## 背景

旧 WPF 需要同时处理手动停止、GPI Stop、定时结束、ReaderException、设备主动断连
和应用退出。底层 SDK 当前提供 TagReport、GPI、ReaderException 和连接状态事件，
但没有独立的“设备已开始盘存/设备已停止盘存”事件。如果 WPF 通过按钮返回值、
`ReaderState` 或 GPI 输入自行推断盘存结束，不同入口会产生重复收尾或状态不一致。

## 决策

1. `IInventoryService.LifecycleChanged` 是平台层 Inventory 长租约的唯一生命周期事实源；
   `IReaderManager.StateChanged` 只描述连接和能力快照，不承担盘存结束通知。
2. `StartInventoryAsync` 成功接受 SDK 的启动请求、平台已建立运行上下文后发布
   `InventoryLifecycleState.Started`。
3. 手动 Stop、匹配的 GPI Stop、定时结束、连接故障、ReaderException、Deactivate、
   Remove 和应用退出在 Stop/排空报告/完成运行记录/断开连接的收尾完成后发布
   `InventoryLifecycleState.Stopped`，并携带稳定的 `InventoryStopReason`。
4. `GpiChanged` 仍表示输入状态变化，先投影给需要显示 GPI 状态的消费者；只有
   `ReaderManager` 根据当前 Reader 的活动 StopTrigger 匹配后，才排队执行一次平台 Stop。
   UI 不把任意 GPI 变化当作盘存结束。
5. 当标准设置启用 Start GPI 或 Stop GPI 触发器时，Settings 编译器同时打开
   `Configuration.Events.GpiEventEnabled`，并保留其他事件通知开关；LLRP 触发器定义
   与 GPI_EVENT 通知是两个独立配置，不能只下发前者。
6. 如果未来 SDK 提供独立的 Inventory 生命周期事件，必须由 SDK Adapter 转换为
   平台事件，不能把 SDK 类型泄漏到 WPF 或 Contracts。

## 后果

- WPF、未来的其他 UI 和后台消费者可以用同一事件收敛运行按钮、计时器、TagLog 和
  InventoryRun 展示，不需要复制连接状态判断；
- 每个 Reader 的 Stop 仍由其 Session Gate 串行化，GPI Stop 不会影响其他 Reader；
- 当前 SDK 无法观察“连接仍在但外部管理工具单独删除/停止 ROSpec”的独立设备事实。
  在没有对应 SDK 事件或协议轮询实现前，这类状态只能在下一次 Reader 操作、连接故障
  或重新 Query 时发现，不能宣称实时可观测；
- 真实 GPI 触发和跨设备行为仍必须通过设备矩阵现场验收，FakeSession 测试只证明
  平台事件和并发边界。

## 验证

- Services 自动化覆盖 Started、匹配 GPI Stop、ConnectionFaulted、ReaderException、
  多 Reader GPI 隔离和重新 Start；
- WPF 自动化覆盖 LifecycleChanged 驱动 UI 收尾，不依赖按钮轮询或连接状态推断；
- 架构约束见 [Reader 生命周期与连接所有权](../architecture/reader-runtime.md)。
