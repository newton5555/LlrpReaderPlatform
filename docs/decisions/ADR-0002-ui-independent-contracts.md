# ADR-0002：UI 无关 Contracts

- 状态：Accepted
- 日期：2026-08

## 决策

Contracts、Services 和 Infrastructure 不引用 WPF 或其他具体 UI 框架。设置模型使用 `EditorKind` 和语义化值类型；UI 自己映射到控件和调度机制。

## 原因

第一个消费者是 WPF，但未来可能接入其他 UI 框架。共享层不应包含 TextBox、Dispatcher、ViewModel、DataTemplate 等具体 UI 概念。

## 影响

- WPF 是第一个适配层，不是共享服务层的设计前提；
- `CompiledSettings` 和 SDK 类型只存在于 Services/Extensions 内部；
- 需要额外的 DTO 和架构测试维护边界。
