# 测试策略

## 测试层级

### Contracts 与架构测试

- Contracts 不引用 WPF、SDK 或厂商程序集；
- Services 不设置 `UseWPF`；
- 依赖方向不反转；
- 公开 API 不暴露 SDK 类型。

### Services.Tests

- Probe、Add 补偿、Enable、Activate、Deactivate、Remove；
- 单 Session 和 per-reader Gate；
- Inventory 与 Settings/Tag Access/GPO 的冲突；
- CapabilityRevision 和过期 Draft；
- 标准设备降级路径。

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
- 无能力或 ReaderBusy 时的 UI 状态。

### 实机验收

- 标准 LLRP 1.0.1 Reader；
- Impinj R420；
- 分别运行和同时运行；
- 断线、Reader 重启、设置拒绝、扩展拒绝和取消清理。

`FakeSession` 用于服务层确定性测试；不能替代 SDK 适配器和真实设备验收。
