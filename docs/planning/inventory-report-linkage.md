# 寻卡联动上报设置实现计划（Planning）

- 状态：**已完成（留存记录，不删除）**；**R1–R5 已完成**（build 0 错误，全量测试 349 项全绿：Contracts 5、Services 183、Impinj 17、Architecture 7、Infrastructure 10、App.Wpf 127）。**R6 真机部分完成**：R420（identity Mfr 25882/Model 2001002/FW 6.4.1.240，IP 已由 .134 变更为 .87）真实盘存开启相位→`impinj.rfPhaseAngle` 实测命中（30/30 报告），对照组不请求相位时不误发（0/25 报告含相位）以验证能力门控；Zebra FX9600（Mfr 161/Model 96008/FW 3.32.37.0，IP .88）完成 L1 身份连接；R6 剩余为 GPI 物理触发/多 Reader 并存/断网重启等现场项，见[设备矩阵](../compatibility/device-matrix.md)。
- 创建：2026-08
- 决策依据：[ADR-0013](../decisions/ADR-0013-report-capability-ownership.md)（只有"需要上报控制"的参数做联动；实现真实联动后设置页对应项改为只读）
- 生命周期：本文件是临时工作文档，不是正式计划。全部阶段完成后，把结果归档回
  [主计划 9.2](../llrp-framework-vision.md)、ADR-0013、设备矩阵与 roadmap，然后**删除本文件**。

## 目标

只有"需要上报控制"的参数（Phase/GPS/XPC 等"报告里多不多一个字段"的开关）做联动——由寻卡页列开关自动下发；**一项参数实现真实联动后，设置页对应项即改为只读 + "由寻卡页联动控制"提示**；未联动的项保持现状可写。数据类能力（FastID 等"影响读标签行为"的开关）不联动，仍由设置页独占，寻卡页只展示。

## 现状基线（已核实代码）

- 标准字段已经联动：`InventoryViewModel.TryBuildStartSpec` 把 `ShowXxxColumn` 映射进 `InventoryReportSpec`，`ReaderManager` / `LlrpReaderSession.ApplyInventorySpec` 按 `spec.Report` 覆盖 SDK 的 `InventorySettings.Report` 后下发（如 `ShowRssiColumn → IncludePeakRssi`）。
- 扩展字段尚未联动：Impinj（`ImpinjInventoryReportOptions.IncludeRfPhaseAngle`）与 Zebra（`ZebraInventoryReportOptions.IncludePhase/IncludeGps`）只在设置页 `ISettingsExtensionContributor.Apply` 写扩展字典；寻卡页 `ShowPhaseColumn` 等只控制列显示，不写设备。
- 语义键已就绪但未在寻卡侧消费：`ZebraReportPhase` 语义键 `phase-report`，`ZebraReportGps` = `gps-report`；`ImpinjRfPhase` 目前**无 semanticId**（需补）。
- WPF 只显示 Zebra 字段：`TagRowViewModel.Phase` 只读 `zebra.phase`；Impinj 相位列不显示。
- 标准 LLRP 1.0.1 没有 RF phase 报告字段；相位只能是厂商扩展或未来标准版本吸收后的语义路径。

## 阶段计划

### R1：Contracts 语义上报字段 `已完成`

- `InventoryReportSpec` 增 `IReadOnlySet<string> ExtensionReportFields`（语义键集合，如 `phase-report` / `gps-report` / `xpc-report`）。
- 定义稳定常量 `ReportFieldSemantics.Phase = "phase-report"` 等（Contracts，UI 无关）。
- `ReaderFeatures.ImpinjRfPhase` 补 semanticId `phase-report`（与 Zebra 对齐，供仲裁）。

### R2：服务层扩展模块上报编译钩子 `已完成`

- `IReaderExtensionModule` 增可选方法 `ApplyInventoryReportSpec(InventorySettings settings, IReadOnlySet<string> semanticFields)`，默认 no-op（旧模块零成本升级）。
- Impinj 实现：semantic 含 `phase-report` 且 `Supports(ImpinjRfPhase)` 时写 `ImpinjInventoryReportOptions.IncludeRfPhaseAngle`。
- Zebra 实现：按 `phase-report` / `gps-report` / `xpc-report` 写 `ZebraInventoryReportOptions` 对应开关。
- `ReaderManager.StartInventoryAsync` 在 `ApplyInventorySpec` 之后、`Session.StartInventoryAsync` 之前，按当前激活扩展调用；不支持的语义键静默忽略并记日志（不报错、不阻断寻卡）。

### R3：寻卡页联动 `已完成`

- `InventoryViewModel` 列开关 `ShowPhaseColumn` / `ShowGpsColumn` / `ShowXpcColumn` 写入 `TryBuildStartSpec` 的 `ExtensionReportFields`。
- 列开关仅当当前 Reader 的 FeatureCatalog 支持对应语义时可用；不可用时禁用/隐藏开关。
- `TagRowViewModel` 改从语义字段读取显示（`phase` / `gps` / `xpc` 语义键），不区分 zebra/impinj 厂商键；厂商投影保持现有字符串字段，语义键由扩展模块按同一命名投影。

### R4：设置页只读 + 联动提示（逐项生效） `已完成`

- `SettingsEntry` 增加"由寻卡页联动控制"标记或直接 `ReadOnlyReason`。
- 只有 R2/R3 已实现真实联动、且本次联动生效的项（如 Impinj RF phase、Zebra report-phase）才在设置页置只读并显示提示。
- 尚未联动的项保持可写，**不做一次性批量只读**。
- 数据类能力（FastID 等）始终可编辑。

### R5：自动化测试 `已完成`

- Contracts：语义字段集合序列化/回读。
- Services：`ApplyInventoryReportSpec` 按模块 × 能力矩阵编译（FakeSession 捕获下发的 `InventorySettings`）；不支持语义键被忽略。
- WPF：`TryBuildStartSpec` 列 → 语义映射；列开关可用性门控；设置页只读投影。
- Architecture：确认无厂商类型泄漏进 Contracts。

### R6：真机验证与归档 `待设备现场（进行中）`

- R420：寻卡开相位列后 TagReport 出现 `impinj.rfPhaseAngle`；设置页相位项只读并提示。
- Zebra FX9600（如有设备）：开相位/GPS 列后报告含对应字段。
- 归档：结果回填主计划 9.2、设备矩阵、ADR-0013 状态；完成后删除本 planning 文档。

## 明确不在本计划

- FastID / Search Mode / Low Duty / Fixed Frequency 等数据能力保持设置页独占；寻卡页对其只展示不控制（已有行为，不回归）。
- 标准报告字段（天线/RSSI/信道/PC/时间/SeenCount）已联动，不在本计划范围内（只作回归基线）。

## 验收标准

- 寻卡页开 Phase 列 → 启动寻卡 → 设备收到相位报告开关（R420 实测字段出现）；设置页相位项为只读 + 提示。
- 寻卡页未开 Phase 列 → 不额外下发相位开关，沿用设备/设置现状。
- 不支持相位的 Reader：寻卡页相位列开关不可用；服务层不发送对应参数。
- 每阶段结束保持构建 0 警告 0 错误与全量测试全绿。
