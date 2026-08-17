# ADR-0017：Probe 到 Activate 的 Session 交接

- 状态：Accepted
- 日期：2026-08-17

## 背景

部分 LLRP Reader（尤其是只允许一个客户端的设备或虚拟 Reader）会把同一时刻的第二条 TCP/LLRP
连接关闭。WPF 添加或启动恢复原先先 Probe、释放临时 Session，再由 Activate 创建并连接另一个
Session；两个连接的目标端口虽然相同，但客户端本地端口不同，不能把它们视为同一连接。

## 决策

- 成功的标准 Probe 由一个明确的 `ProbeSessionLease` 持有连接；WPF 添加并立即启用、以及启动恢复
  的已启用标准 Reader，直接把该 Session 交接给 `ReaderHandle`，随后 Activate 复用这条连接。
- 如果需要在 SDK Builder 阶段装配厂商扩展，或 Reader 离线导致 Probe 失败，必须先等待 Probe
  Session 的 `DisposeAsync` 完成，再创建替代 Session；Session 替换同样先断开并释放旧 Session，
  然后才连接新 Session。
- 每个 Reader 的后续操作仍由 `ReaderHandle.Gate` 串行化；不固定、不复用客户端本地临时端口，
  `5084` 仍是 Reader 端口。
- 公开 `ProbeAsync` 仍是临时探测契约，返回前会释放自己的 Session；只有能够成为 Reader 生命周期
  所有者的 Add/Initialize 流程才允许交接 Probe Session。

## 影响

- 单客户端设备不会在 Probe 与紧随其后的 Activate 之间出现重叠连接。
- 厂商扩展仍保持“标准探测 → 匹配扩展 → 创建扩展 Session”的两阶段边界，不把扩展类型泄漏到
  Contracts 或 WPF。
- `FakeSession` 的服务测试覆盖启用添加、启动恢复、取消和激活失败路径；真实设备仍需按设备矩阵
  验证设备端单客户端行为。
