# Virtual Reader 开发模式

Virtual Reader 是平台的开发/验收替身，不是第二套业务实现。它复用 `IReaderSession`、`ReaderManager`、设置编译、盘存聚合、Tag Access、GPI/GPO 和 WPF 页面，因此可以在没有真机的情况下验证完整的上位机链路。

## 启用方式

在启动 WPF 应用前设置场景文件路径：

```powershell
$env:LLRP_VIRTUAL_SCENARIO = "F:\Projects\LLRP\LlrpReaderPlatform\docs\development\samples\virtual-reader.json"
dotnet run --project src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj
```

这是显式开发开关。未设置时，应用仍使用真实 LLRP SDK Session；发布流程不会设置该环境变量，也不会把 Virtual Reader 当作真实设备支持声明。

启动时应用会：

1. 加载场景和标签数据；
2. 注册一个虚拟 `ReaderProfile` 到当前 SQLite profile store（只在该 ID 不存在时写入）；
3. 用 Virtual Reader SessionFactory 替换真实 TCP SessionFactory；
4. 让现有 WPF 页面按正常路径执行 Probe、Activate、Settings、Inventory、Stop、Disconnect。

## 场景与真实数据

场景文件是设备画像和回放策略，标签数据不写入新的专用数据库：

- `Inventory.TagLogPath` 优先读取平台的 `tag-logs` JSONL，按文件顺序回放；
- 没有 JSONL 时使用 `Inventory.SnapshotPath` 的 `inventory-snapshots/*.json` 作为回退数据；
- JSONL 中的 `TagObservation`、snapshot 中的 `tags` 和 `VirtualTagMemorySeed` 共用平台的 EPC/TID/时间/RSSI/天线字段；
- 时间戳无效、为 Unix epoch 或不单调时使用配置的回退间隔，不会把 1970 年时间直接当作长延迟；
- 标签 User/Reserved/TID/EPC 内存以及 Access Password 由场景种子定义，写入后跨短连接和 Session 重建保留。

建议为场景固定填写 `readerId`；如果省略，模型会生成新的 ID，适合一次性测试，不适合反复启动同一个 SQLite 数据目录。

最小场景：

```json
{
  "schemaVersion": 1,
  "name": "captured-reader",
  "readerName": "Virtual captured reader",
  "host": "virtual-reader",
  "port": 5084,
  "protocolVersion": "Force101",
  "inventory": {
    "tagLogPath": "..\\..\\..\\tag-logs"
  },
  "replay": {
    "mode": "Accelerated",
    "speed": 20,
    "fallbackIntervalMilliseconds": 50
  },
  "tagMemory": [
    {
      "epc": "3000AABB",
      "tidHex": "E20001",
      "userHex": "11223344",
      "accessPasswordHex": "01020304"
    }
  ]
}
```

## 回放模式

- `RealTime`：按采集时间间隔回放；
- `Accelerated`：按 `speed` 倍速回放；
- `Step`：每调用 `VirtualReaderSession.AdvanceOneReplayEvent()` 才释放一条事件，适合服务测试；
- `Loop`：完成一轮后继续从头回放；`replay.loop=true` 也会启用循环。

盘存不会因为数据集回放完毕就隐式停止，和真实 Reader 一样由服务层的手动停止、时长、GPI、设备断连或故障事件结束本轮租约。

## 能力与故障注入

场景可以控制最大天线数、显式天线 ID 要求、Tx/Rx index 表、RF mode、Tag Access、块擦除、GPI/GPO 数量，以及连接、配置查询、配置写入、启动盘存失败和设备主动断开。这样可以验证 UI 的错误状态和停止原因，而不是只验证成功路径。

## 边界

Virtual Reader 是平台内进程 Session，用于 WPF 和 Services 全链路验收；它不模拟真实射频，也不替代 `LLRPCSharp` 中的 TCP LLRP 协议虚拟主机。协议编解码和真实 SDK 的 TCP 互操作继续由 SDK 的虚拟 Reader/协议测试覆盖。
