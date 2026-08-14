# ADR-0012：语义能力键与厂商参数毕业机制

- 状态：Accepted
- 日期：2026-08-14

## 决策

1. 引入**稳定语义能力键**（跨厂商、跨协议版本稳定，例如 `xpc-report`）；UI 与设置行只认语义键。
2. 厂商 `Feature` 声明 `StandardizedSince`（吸收该语义的标准协议版本；尚无标准化的填 `null`）。当设备协商版本 ≥ `StandardizedSince` 时，厂商轴停止贡献该语义，标准轴接管。
3. 同语义在双轴同时出现时**标准优先**，去重仲裁唯一收口在 `ReaderFeatureCatalog` 聚合点。
4. 毕业时设置键采用**别名兼容**（沿用 `SettingsKeys.TxPowerDbm = TxPowerIndex` 的既有先例），存量 Preset 经 `SchemaVersion` 演进，不做破坏性迁移。

## 背景

同一语义可能先由厂商实现、后被标准吸收。真实案例：Zebra LLRP 1.0.1 的 `MotoC1G2ExtendedPC` 对应 LLRP 2.0 标准 `C1G2XPCW1/C1G2XPCW2`；Impinj/Zebra 的相位报告也存在同类风险。若无毕业机制，该语义会在 UI 出现两行、两条编译路径，且标准全面接管时变成 Contracts 层破坏性迁移。

## 候选方案

1. 不处理，依赖「厂商只匹配 1.0.1、标准新参数只在 2.0 出现」的天然互斥。
2. 语义键 + `StandardizedSince` + 标准优先仲裁（**选此**）。
3. 厂商参数永远走厂商路径，即使标准已吸收。

## 原因

- 候选 1 依赖现状假设；「1.1+其他厂商」被支持后该假设即被打破。
- 候选 3 让标准能力永远绑定厂商命名空间，违背「厂商扩展不污染通用契约」的长期方向。
- 语义键 + 元数据把「厂商先有 → 标准后有」变成数据变化（一行元数据）而非迁移事件。

## 影响

- `Feature` 结构扩展：语义键 + `StandardizedSince` 元数据；既有 `Feature` 构造点逐一补齐语义键。
- `ReaderFeatureCatalog` 聚合增加标准优先去重仲裁与日志记录。
- 厂商模块 `GetFeatures` 按协商版本停止贡献已标准化的语义（Impinj/Zebra 同步改造）。
- 新语义能力必须先分配稳定语义键；本次接入的 XPC 报告即为首个实例（`zebra.xpc` 与 `standard.c1g2-xpc` 共享同一语义键）。
- 报告投影继续使用泛型 `Fields` 字典，UI 列只认语义名，不感知来源轴。
