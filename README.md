# LlrpReaderPlatform

LLRP Reader Platform 是面向多种 LLRP Reader 的应用框架规划仓库。

当前仓库处于文档与架构准备阶段，暂不实现业务代码。第一个 UI 消费者计划为 WPF；共享 Contracts、Services 和 Infrastructure 必须保持 UI 框架无关，以便未来接入其他 UI 框架。

现有 `LlrpReaderStudio` 仓库已经冻结，作为标准 LLRP 1.0.1 和 Impinj R420 的行为、测试和迁移参考，不作为本仓库的项目依赖。

## 文档入口

从 [docs/README.md](docs/README.md) 开始阅读。

建议顺序：

1. [总体规划](docs/llrp-framework-vision.md)
2. [架构总览、解决方案结构与 UI 边界](docs/architecture/overview.md)
3. [设备生命周期与连接所有权](docs/architecture/reader-runtime.md)
4. [设备兼容性矩阵](docs/compatibility/device-matrix.md)
5. [开发路线图](docs/development/roadmap.md)

## 当前状态

- 仓库：文档与组织架构准备阶段；
- 首个验证基线：标准 LLRP 1.0.1、Impinj R420；
- 首个 UI：`LlrpReaderPlatform.App.Wpf`；
- 业务实现：尚未开始。
