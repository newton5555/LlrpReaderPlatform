# Reader 生命周期与连接所有权

## Session 所有权

每个 Reader 对应一个 `ReaderHandle`。`ReaderHandle` 是该 Reader 在当前应用进程内唯一的 Session 所有者，任一时刻最多存在一个活动 TCP Session。

所有操作经过该 Reader 的异步 Gate：

- Probe：使用临时 Session，不注册到 Fleet；
- Activate：连接、读取身份/能力/设置、更新 RuntimeSnapshot、断开；
- Settings、Tag Access、GPO：通过短连接租约完成；
- Inventory：持有长连接租约；
- Disable/Remove：取消操作、停止盘存、断开并释放 Session。

## 状态

用户意图与运行状态分离：

```text
IsEnabled       用户意图，持久化
ConnectionState Disconnected / Connecting / Connected / Disconnecting / Faulted
OperationState  Idle / Configuring / Inventorying / Accessing / Stopping
```

## 冲突策略

Inventory 运行时，Settings、Tag Access 和 GPO 默认返回 `ReaderBusy`，不隐式停止盘存。用户需要先显式停止 Inventory。

Disable 和 Remove 属于控制操作，可以取消当前工作，随后执行清理。所有取消、断开和 Dispose 路径必须可重复执行。

## 状态事件

Services 不捕获 UI SynchronizationContext。状态事件可以在后台线程发布，WPF、Avalonia 或其他 UI 消费者负责切换到自己的 UI 线程。
