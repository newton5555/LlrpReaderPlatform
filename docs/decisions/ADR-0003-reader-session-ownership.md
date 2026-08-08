# ADR-0003：Reader Session 所有权

- 状态：Accepted
- 日期：2026-08

## 决策

每个 `ReaderHandle` 负责一台 Reader 的唯一活动 TCP Session。Services 使用 per-reader 异步 Gate 串行化操作；Inventory 持有长连接租约，其他短连接操作在冲突时返回 `ReaderBusy`。

## 原因

LLRP Reader 的 TCP 控制连接是独占的。将 Session 所有权集中在 ReaderHandle，可以避免多个页面各自连接、重复断开和状态竞态。

## 影响

- UI 不直接调用 Connect/Disconnect；
- Disable/Remove 必须包含取消、停止、断开和 Dispose 清理；
- 未来若产品需要跨进程代理控制，需要另行设计 Host/IPC，不属于当前框架范围。
