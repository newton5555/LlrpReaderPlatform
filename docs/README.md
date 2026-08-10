# 文档导航

本目录按“目标—架构—兼容性—开发—决策”组织。规划文档定义要做什么，架构文档定义边界和约束，设备矩阵定义实际支持范围，ADR 记录不可随意反转的设计决定。

## 阅读顺序

### 1. 唯一主计划

- [LLRP Reader Platform 总体规划](llrp-framework-vision.md)

主计划统一维护目标、范围、项目结构、阶段计划、验收标准和风险。其他文档只补充细节，不复制第二份阶段计划。

### 2. 配套架构

- [架构总览、解决方案结构与 UI 边界](architecture/overview.md)
- [Reader 生命周期与连接所有权](architecture/reader-runtime.md)
- [厂商扩展与设置模型](architecture/extensions-and-settings.md)

### 3. 兼容性

- [设备支持等级与矩阵](compatibility/device-matrix.md)
- [Impinj R420 真机验收记录（2026-08-10）](compatibility/impinj-r420-2026-08-10.md)

### 4. 开发与验证

- [开发路线图](development/roadmap.md)
- [测试策略](development/testing-strategy.md)
- [交接文档（Handoff）](development/handoff.md)
- [旧 WPF 功能迁移矩阵](development/legacy-feature-matrix.md)
- [真机验收运行手册](development/hardware-validation-runbook.md)
- [WPF 用户操作与故障排查](development/wpf-user-and-troubleshooting.md)

### 5. 决策记录

- [ADR 索引](decisions/README.md)
- 当前 Inventory 生命周期事件的权威来源见 [ADR-0005](decisions/ADR-0005-inventory-lifecycle-events.md)。

### 6. 冻结项目参考

- [LlrpReaderStudio 旧仓库地址与当前架构](legacy/README.md)

## 文档规则

- 规划文档：描述阶段目标、范围、验收标准和工作量；
- 架构文档：描述项目边界、依赖方向、公开契约和运行时约束；
- 兼容性文档：只记录经过设备或协议测试的能力；
- ADR：记录已经作出的架构决定及其替代方案；
- 模板：新 ADR 或设备验收记录必须从 `templates/` 复制；
- `legacy/` 只记录冻结项目的地址和当前结构，不复制旧项目规划。

文档中出现“已支持”时，必须能在设备矩阵或测试记录中找到依据；仅有接口设计不等于厂商支持。
