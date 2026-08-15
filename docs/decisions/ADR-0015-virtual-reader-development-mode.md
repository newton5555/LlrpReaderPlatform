# ADR-0015：Virtual Reader 作为平台级开发替身

- 状态：Accepted
- 日期：2026-08-16

## 背景

WPF 页面和服务层需要在没有真机、没有天线或需要重复故障条件时验证完整的 Probe、设置、盘存、Tag Access、GPI/GPO 和停止生命周期。仅靠 `FakeSession` 不能提供跨连接的设备状态，也不能直接作为 WPF 的运行时设备。

## 决策

新增独立的 `LlrpReaderPlatform.VirtualReader` 项目，提供：

- 版本化 JSON 场景；
- 从平台 `tag-logs` JSONL 优先、`inventory-snapshots` 回退的标签回放；
- RealTime、Accelerated、Step、Loop 回放模式；
- 标准 Reader 身份、能力、设置和显式天线校验；
- 跨 Session 保留的 Reader 配置、GPI/GPO 和标签内存；
- Tag Access、GPI/GPO、设备异常、连接故障和启动失败注入；
- 与真实实现相同的 `IReaderSessionFactory` 边界，以及 WPF 的显式开发开关 `LLRP_VIRTUAL_SCENARIO`。

Virtual Reader 只替换 Session 实现，不在 WPF 或 ViewModel 增加业务分支。未设置开发环境变量时，WPF 继续使用真实 LLRP SDK TCP Session。协议编解码互操作仍由相邻 SDK 仓库的 TCP 虚拟 Reader 和协议测试负责，平台 Virtual Reader 不复制 LLRP 编解码器。

## 结果

同一套 `ReaderManager`、Settings、Inventory、Tag Memory、GPI/GPO 和 WPF 页面可以执行真机等价的上位机生命周期；回放数据使用新平台已有的标签数据格式，不引入旧库导入或第二套数据库。
