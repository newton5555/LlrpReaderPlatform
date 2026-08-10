# ADR-0004：使用 EF Core SQLite 作为本地持久化实现

- 状态：已接受
- 日期：2026-08-09

## 背景

平台需要在 WPF 应用重启后恢复 Reader Profile、设置语义快照、Tag List、Inventory Run、应用设置和日志配置；同时需要有 EF schema 初始化能力和临时测试数据库。持久化不能让 WPF ViewModel 直接依赖数据库，也不能把旧项目 SDK 对象带入运行时。

## 决策

1. 使用 EF Core SQLite 作为首个 WPF 消费者的本地数据库实现，放在 `LlrpReaderPlatform.Infrastructure`；
2. `Contracts` 只定义 Store 接口和平台语义模型，`Services` 只通过这些接口工作，不引用 EF、SQLite Entity 或 DbContext；
3. 数据库 schema 通过 EF migrations 管理，默认数据库位于当前用户的本地应用数据目录；测试使用独立的内存 SQLite 或临时数据库；早期版本不承诺历史 schema 的数据兼容，开发和验收阶段允许清空数据库后重建；
4. Reader Settings 只保存版本化的语义 JSON，不把 SDK 或厂商对象写进 Contracts/数据库模型；
5. 旧仓库只作为行为和 UI 迁移参考，不作为新仓库的 ProjectReference、运行时程序集、数据导入源或持久化模型来源。

## 选择理由

- SQLite 适合单机 WPF 部署，无需额外数据库服务；
- EF migrations 提供可审计的 schema 演进和启动恢复基础；
- Store 契约让未来的服务端数据库、测试替身或其他 UI 消费者不需要修改 Services；
- 版本化语义 JSON 能保留未知厂商字段，同时避免把 SDK 类型泄漏到共享模型。

## 影响

- 新增持久化表必须同时更新 DbContext、migration、Store 测试和主计划；
- 应用启动需要执行数据库迁移，数据库损坏或迁移失败必须在组合根处显示可诊断错误；
- 清空数据库会丢失本地 Reader Profile、设置快照、Tag List、运行记录和应用设置，生产环境应先备份文件；
- 如果未来需要多进程或集中式管理，应新增 ADR，不把 SQLite 直接扩展成跨网络数据库。
