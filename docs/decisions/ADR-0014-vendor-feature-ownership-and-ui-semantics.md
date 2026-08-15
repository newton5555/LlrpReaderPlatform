# ADR-0014：厂商能力归属与 UI 语义投影

- 状态：Accepted
- 日期：2026-08
- 关联：ADR-0012、ADR-0013

## 决策

厂商能力标识由对应的 `Extensions.<Vendor>` 项目拥有，通用 Contracts 只拥有以下无厂商
实现的载体和标准能力：

- `Feature`：能力 ID、Vendor、SemanticId 和 `StandardizedSince`；
- `ReaderFeatureCatalog`：能力集合、未知状态和标准优先仲裁；
- `ReportFieldSemantics`：跨厂商报告字段的稳定语义键；
- `SettingsEntry`：设置 Key、语义键、分组键和重复项实例键等 UI 无关元数据。

例如 `ImpinjFeatures` 与 `ZebraFeatures` 分别留在各自扩展项目中。新增厂商不需要修改
Contracts 的厂商常量，也不需要让 Services 引用该厂商程序集。

WPF 和未来其他 UI 消费者只使用 `SettingsEntry.SemanticId`、`GroupKey`、`InstanceKey` 与
`ReportFieldSemantics`。设置的实际 Key、SDK 扩展对象和厂商报告原始字段由扩展项目负责。
旧缓存中没有元数据时，WPF 允许保留一次按历史 Key 尾部的兼容读取；新布局和新持久化数据
必须携带语义元数据。

## 原因

将厂商 Feature 常量放在 Contracts 会使通用契约随每个厂商增长；在 WPF ViewModel 中按
`impinj.*`/`zebra.*` 查找行则会使未来厂商接入变成 UI 修改。两种做法都没有形成编译器、
扩展模块和消费者之间的稳定边界。

设置行的 Key 仍然可以是厂商私有 Key，因为 Draft/Apply 必须精确回到拥有该参数的扩展；
但展示、分组、列绑定和能力仲裁不得依赖这个私有 Key。报告投影同理：原始厂商字段可用于
诊断或未来高级消费者，平台通用 UI 只读稳定语义字段。

## 影响与约束

- 扩展测试必须覆盖 Feature 画像、设置元数据、Apply 编译和报告语义投影；
- 架构测试必须把每个已登记扩展及其 SDK Adapter 纳入依赖方向守护；
- App.Wpf 仍是应用组合根，可以直接引用已启用的扩展项目并注册 DI，这是组合根例外，不是
  Services 反向依赖；
- `dev` 可以通过被 Git 忽略的 `Directory.Build.local.props` 使用本地 SDK 项目；发布和 CI
  仍必须使用 NuGet 模式；
- 本 ADR 不要求 SQLite 历史数据迁移。若未来要保留旧报告，只能在 Infrastructure/Services
  边界增加集中兼容转换，不能把旧厂商 Key 重新扩散到 ViewModel。
