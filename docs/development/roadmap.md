# 开发路线图

完整阶段计划见 [LLRP Reader Platform 总体规划](../llrp-framework-vision.md)。本文件只保留执行顺序和阶段出口，不复制主计划内容。

## 阶段顺序

1. F1：仓库、文档、Contracts、依赖和架构测试；
2. F2：标准 Reader 生命周期、单 Session、Gate 和 Capabilities；
3. F3：UI 无关 Settings Layout/Snapshot/Draft 和 Settings Service；
4. F4：标准 Inventory、TagReport、Tag Access、GPI/GPO；
5. F5：Impinj R420 扩展和两阶段连接；
6. F6～F8：第一个 WPF 消费者；
7. F9：测试、设备矩阵和实机验收。

## 阶段出口

每阶段必须具备：

- 可独立构建的提交；
- 对应的自动化测试；
- 明确的设备/模拟器验收结果；
- 文档中的状态回填；
- 未完成项和已知限制记录。

旧 `LlrpReaderStudio` 不作为新仓库的项目引用。旧数据库导入若有需要，单独建立迁移任务，不阻塞框架骨架阶段。
