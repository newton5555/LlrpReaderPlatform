# 测试策略

## 测试层级

### Contracts 与架构测试

- Contracts 不引用 WPF、SDK 或厂商程序集；
- Services 不设置 `UseWPF`；
- 依赖方向不反转；
- 公开 API 不暴露 SDK 类型；
- Inventory 长连接租约、Settings/TagAccess/GPO 短连接租约不被 UI 层绕过；
- 不允许引入第二套 Fleet/Session 管理器。

### Services.Tests

- Probe、Add 补偿、Enable、Activate、Deactivate、Remove；
- 单 Session 和 per-reader Gate；
- Inventory Start→Connect→Run→Stop→Disconnect 的完整生命周期；
- Stop/Start 重入、ROSpec 清理、设备主动断连和 DisposeAsync；
- Inventory 与 Settings/Tag Access/GPO 的冲突；
- CapabilityRevision 和过期 Draft；
- 标准设备降级路径；
- SDK Settings Query/Apply、Inventory Settings、TagReport 映射；
- 高并发 TagReport 的有界队列和丢弃统计。

### Infrastructure.Tests

- EF Core SQLite 建库和迁移；
- Reader Profile、Settings/Inventory Preset、Tag List、Inventory Run、App Settings CRUD；
- 启动恢复、数据库初始化和基础 schema 行为；
- 数据库路径、并发 DbContext 和失败回滚。

### Extensions.Impinj.Tests

- 模块 Match；
- Builder 配置；
- 扩展能力和设置解析/编译；
- TagReport 投影；
- Preset 版本兼容性。

### App.Wpf.Tests

- DI 容器能创建 MainWindow；
- ViewModel 只调用 Contracts/Services；
- EditorKind 到 DataTemplate 的映射；
- 状态事件切换到 Dispatcher；
- 无能力或 ReaderBusy 时的 UI 状态；
- Settings、Tag Access 和 Inventory 结果的 `PlatformErrorCode` 与用户可读错误文本同时正确投影；
- Settings Query/Apply 状态和离线缓存只读状态；
- Inventory TagObserved 批量消费、Start/Stop 状态和统计；
- Inventory `LifecycleChanged` 对手动 Stop、GPI Stop、设备断连和多 Reader 隔离的统一状态投影；
- 设备设置 Tab2 GPO、Tag Memory、Tag List、App Settings 页面服务接入。

### 实机验收

- 标准 LLRP 1.0.1 Reader；
- Impinj R420；
- 分别运行和同时运行；
- 断线、Reader 重启、设置拒绝、扩展拒绝和取消清理；
- Query/Apply 真实设备配置并重新 Query 验证；
- Inventory 长连接从 Start 持续到 Stop，不按报告重新连接；
- 高频 TagReport 下 SDK 消息泵、Services 和 WPF 均不阻塞。

`FakeSession` 用于服务层确定性测试；不能替代 SDK 适配器和真实设备验收。
