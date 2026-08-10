# 开发路线图

完整阶段计划见 [LLRP Reader Platform 总体规划](../llrp-framework-vision.md)。本文件只保留执行顺序和阶段出口，不复制主计划内容。

## 执行顺序

主计划使用 F 阶段记录产品路线，实际开发按依赖层级 P0～P8 执行：

1. **P0：交付基线和旧项目功能矩阵**——锁定旧 WPF 行为、架构 ADR 和最终验收清单；
2. **P1：SDK Adapter**——补齐 Query/Apply Settings、Inventory、Tag Access、GPI/GPO 和异常投影；
3. **P2：Services Runtime**——完成 ReaderManager 单 Session/Gate、短连接和 Inventory 长连接租约；
4. **P3：Infrastructure**——完成 EF Core SQLite、Profile/Preset/TagList/Run/AppSettings 和启动恢复；
5. **P4：Extensions**——完成 Impinj 能力、设置、TagReport 和 Preset 贡献接口；
6. **P5：Settings/Inventory Services**——完成真实 Query/Apply 和 Start→Inventory→Stop→Disconnect 闭环；
7. **P6：应用服务**——完成 Tag List、Inventory Run、日志、Diagnostics/GPO、Tag Memory 协调；
8. **P7：WPF Consumer**——接入完整页面和 ViewModel，ViewModel 不直接碰 SDK、数据库或连接；
9. **P8：真机与兼容性**——完成 R420、标准 Reader、未知设备、多 Reader 和故障恢复验收。

当前进度：P0～P7 首版代码已落地；P3 已完成 EF Core SQLite、Profile/Settings/TagList/InventoryRun/AppSettings、JSONL Logging 和启动恢复；P4/P5 已接入标准深度设置、Impinj 设置贡献、TagReport 扩展字段投影和完整 Inventory 生命周期；P6/P7 已完成 TagList/Run 管理 UI、设备设置 Tab1/Tab2（GPO/GPI 状态）、Tag Memory、App Settings、原生 ProgressBar、EPC/TID Tag Access 目标、能力驱动频率集合编辑和旧 WPF 多 Reader 全局寻卡编排；能力目录已接入运行时快照、设置布局和 Impinj 扩展贡献，并按型号、固件和 SDK 能力画像逐项限定 L4 字段，R420 已确认不开放 Doppler；P8 已完成真机标准 Probe/Settings Query、WPF Settings Apply、GPO/GPI 状态查询、GPI 状态事件的平台链路、Impinj debounce/FastID/Phase/Search/Low Duty/固定频率回写、有界 Inventory Start/Stop/Disconnect、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复和 FastID/Phase 扩展 TagReport，代码级一般 Connection Faulted/ReaderException、匹配 GPI Stop 触发器收敛与重新 Start 已有自动化验证；GPI 物理触发、其它 Memory Bank 写入、多 Reader、断网/重启恢复现场验收待执行。标准 Tag Access 已按 ReaderCapabilities 明确能力做服务/UI 降级，标准 GPIO 端口数量已按 General Device Capabilities 驱动 Tab1/Tab2 降级，部分 GPO 设备按实际端口启用控件。自动化测试基线为 201 项全绿；平台 `LifecycleChanged` 事件统一驱动 WPF 的手动 Stop、GPI Stop、定时结束和连接故障收尾，设备列表提供 Faulted Reader 重新连接/刷新能力；SQLite 只维护新平台数据，早期 schema 变化允许清空数据库重建；TagReport 和 TagLog 队列均有界，WPF 事件队列和展示集合也已设置硬上限，生命周期、设置取消、消费者异步操作忙碌状态、设备主动断连运行记录落库、一般 Connection Faulted/ReaderException 收敛、应用关闭时排空 TagLog、单 Reader 断连不影响其它 Reader、四个标准 Memory Bank 读写映射、匹配 GPI Stop 触发、GPI 启停配置透传、多 Reader GPI 隔离、幂等异步释放和 WPF 异常状态回退已有回归覆盖。

## 阶段出口

每阶段必须具备：

- 可独立构建的提交；
- 对应的自动化测试；
- 明确的设备/模拟器验收结果；
- 文档中的状态回填；
- 未完成项和已知限制记录。

旧 `LlrpReaderStudio` 不作为新仓库的项目引用或数据导入源。SQLite 只负责新平台自身数据；早期 schema 变化以清空数据库重建为可接受方案，SQLite、设置下发和完整 Inventory 都是最终交付范围，不是可选的后续事项。
