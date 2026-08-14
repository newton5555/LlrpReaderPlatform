# ADR-0011：设置页双轴门控——标准轴按协商协议版本、厂商轴按能力画像

- 状态：Accepted
- 日期：2026-08-14

## 决策

- Reader Settings 保持**单一页面**，不按协议版本或厂商拆分成多套 UI。
- **标准轴**：标准参数行由设备实际协商的协议版本（`ReaderRuntimeSnapshot.NegotiatedProtocolVersion`）门控显隐。
- **厂商轴**：厂商扩展行由 `ManufacturerId` + 能力画像门控（现状不变）。
- 两轴**正交取交集**：同一台设备同一时刻只有一个协商版本，因此版本门控在效果上等价于整页切换，但布局、渲染、测试只维护一份实现。

## 背景

LLRP 1.1 相对 1.0.1 新增 10 个标准参数（块级 Tag Access、LoopSpec/SpecLoopEvent、射频能力细节），2.0 再新增 19 个（安全 Tag Access、XPC、Keepalive 等），且三个版本约九成参数行完全相同。标准参数与厂商参数是两个正交维度；若按版本拆页，再叠加厂商轴会出现组合爆炸（1.0.1+Impinj、1.0.1+Zebra、1.1+厂商、2.0+厂商……），同一批行为不同的版本组合各自维护一套 UI。

## 候选方案

1. 按协议版本整页切换（三套 XAML/ViewModel/测试）。
2. 一页 + 版本门控 + 厂商门控，两轴正交（**选此**）。
3. 不接入任何版本专属参数，平台只保留策略开关。

## 原因

- 版本门控是 `NegotiatedProtocolVersion` 单一字段上的行级过滤，与现有 `ReaderFeatureCatalog` 能力门控机制同构，不需要新的 UI 概念。
- 标准/厂商正交，天然为「未来 1.1+其他厂商、2.0+Zebra」等组合做准备，不产生组合爆炸。
- WPF 渲染层只认语义行与 `EditorKind`，保持 UI 零版本感知；新版本参数接入的代价收敛在标准编译器与能力目录。

## 影响

- `SettingsEntry` 不引入独立版本字段；版本门控通过「版本作用域的语义 Feature」（如 `standard.c1g2-block-permalock`）表达，由 `ReaderFeatureCatalog` 按协商版本聚合，与厂商 Feature 走同一门控管线。
- `StandardSettingsCompiler` 布局生成按协商版本聚合标准行；WPF 不改渲染逻辑。
- 1.1/2.0 专属标准参数的接入各自独立成工作项，按 SDK 托管能力与真机可用性分批，不在无设备时一次性铺开。
- 设备矩阵仍按 L1–L4 分层验收；无对应版本真机时不声明支持。
