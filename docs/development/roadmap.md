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
10. **M1：多版本与多厂商推进**（dev 分支）——阶段 D（2.0 策略与版本模型）→ 阶段 A（双轴门控与语义键，ADR-0011/0012）→ 阶段 C0（Rx 灵敏度实际值显示）→ 阶段 B（1.0.1+Zebra 实验模块）→ 阶段 C（块级 Tag Access）→ 阶段 E（厂商轴扩展性）→ 阶段 F（验证收口）；范围、SDK 能力边界与缺口清单见[主计划 9.1](../llrp-framework-vision.md)。
11. **M2：寻卡联动上报设置**（ADR-0013）——阶段 R1～R5 已完成（build 0 错误，测试全绿），R6 真机验证待设备现场；当前状态以[主计划 9.2](../llrp-framework-vision.md)、[ADR-0013](../decisions/ADR-0013-report-capability-ownership.md)和[设备矩阵](../compatibility/device-matrix.md)为准。
## 长期规划（未排期）

- **平台虚拟设备 Data Source 化**（ADR-0016）——长期保留主计划 7.1 的 VP1～VP6：有类型来源契约 → SQLite 与路由 SessionFactory → 预设 Catalog/Contributor → Data Sources 添加与启动恢复 → 多 Reader WPF 验收。它不是当前执行顺序中的下一项；报文级虚拟设备由 SDK 仓库的独立 Virtual Reader Manager 长期规划负责。

## 当前进度

当前进度：P0～P7 首版代码已落地；P3 已完成 EF Core SQLite、Profile/Settings/TagList/InventoryRun/AppSettings、JSONL Logging 和启动恢复；P4/P5 已接入标准深度设置、Impinj 设置贡献、TagReport 扩展字段投影和完整 Inventory 生命周期；P6/P7 已完成 TagList/Run 管理 UI、设备设置 Tab1/Tab2（GPO/GPI 状态）、Tag Memory、App Settings、原生 ProgressBar、EPC/TID Tag Access 目标、能力驱动频率集合编辑、旧 WPF 风格的寻卡页布局与 DataGrid 列头右键选择、按时长自动停止、稳定错误码到 WPF 状态投影和旧 WPF 多 Reader 全局寻卡编排；能力目录已接入运行时快照、设置布局和 Impinj 扩展贡献，并按型号、固件和 SDK 能力画像逐项限定 L4 字段，R420 已确认不开放 Doppler；WPF 消费者组合根已集中注册并注入全部页面 ViewModel，`MainViewModel` 不再直接创建页面对象；P8 已完成真机标准 Probe/Settings Query、WPF Settings Apply，且已修复设备列表刷新期间取消设置查询的竞态，R420 设置页可显示 Loaded from Reader、Save 和 62 个回读值、GPO/GPI 状态查询、GPI 状态事件的平台链路、Impinj debounce/FastID/Phase/Search/Low Duty/固定频率回写、有界 Inventory Start/Stop/Disconnect、真实 TagReport 聚合、EPC/TID/User/Reserved 四个 Memory Bank 读取、User Bank 写入恢复和 FastID/Phase 扩展 TagReport，代码级一般 Connection Faulted/ReaderException、故障/取消 Session 回收与干净 Session 重建、取消后重新 Probe 恢复能力、匹配 GPI Stop 触发器收敛与重新 Start 已有自动化验证；无 GPI/GPO 能力的状态查询已统一返回 `Unsupported` 且不把 Reader 置为 Faulted；GPI 物理触发、其它 Memory Bank 写入、多 Reader、断网/重启恢复现场验收待执行。标准 Tag Access 已按 ReaderCapabilities 明确能力做服务/UI 降级，标准 GPIO 端口数量优先按 General Device Capabilities 驱动 Tab1/Tab2；能力响应未声明数量时，成功 GPIO 状态查询会按实际返回端口补充当前运行时快照，但不替代物理触发验收。自动化测试基线为 368 项全绿；Tag List 保存/删除现在会通过 WPF 变更事件即时刷新已经显示的 Inventory 行，不触碰 Reader 生命周期；平台 `LifecycleChanged` 事件统一驱动 WPF 的手动 Stop、GPI Stop、定时结束和连接故障收尾，设备列表提供 Faulted Reader 重新连接/刷新能力；SQLite 只维护新平台数据，早期 schema 变化允许清空数据库重建；Settings Preset 的版本化语义 JSON 同时承载 Inventory 字段，不引入旧库导入；短连接断开时设置布局转为只读并要求重新激活，能力快照过期时设置页同步禁用编辑和保存；能力解析循环使用整数索引，避免 `ushort` 最大值回绕；Inventory 服务入口拒绝无效时长、重复天线和混合全部天线/指定天线参数；TagReport 和 TagLog 队列均有界，WPF 事件队列和展示集合也已设置硬上限，生命周期、设置取消、消费者异步操作忙碌状态、设备主动断连运行记录落库、一般 Connection Faulted/ReaderException 收敛、单 Reader 断连不影响其它 Reader、四个标准 Memory Bank 读写映射、匹配 GPI Stop 触发、多 Reader GPI 隔离、幂等异步释放和 WPF 异常状态回退已有回归覆盖；全局寻卡的部分启动/停止失败会按 Reader 名称和错误摘要显示，便于多 Reader 验收定位。

当前交付配置：`App.Wpf.csproj` 的程序集名为 `LlrpReaderPlatform`；Windows x64 发布使用 NuGet SDK、`--self-contained true` 和 `PublishSingleFile=true`，输出为 `LlrpReaderPlatform.exe`。本地便携目录和官方 Release ZIP 的具体内容以[发布规范](release.md)为准。

2026-08-11 的 P8 补充证据：使用 `win-x64` 发布包直接运行 WPF 并连接 R420，寻卡页收到真实 EPC，5～6 个唯一标签以约 269～300 tags/s 更新；Stop 最终回到 `Start`/已同步能力，正常关闭窗口验证应用退出释放。第二台 `192.168.41.148` 已用强制 LLRP 1.0.1 完成 Probe→Add→Activate、Settings Query 和 `Report Every N Tags` 的 `1→2→1` 真实 Apply/回读闭环，设备无天线，尚未执行 Inventory。当前 P8 剩余仍是 GPI 物理事件/触发、其它 Memory Bank 写入、第二台 Reader 的带天线 Inventory、多 Reader 并行 Inventory 以及断网/重启恢复。

本轮 WPF 可用性补充：设备列表和设置页同时显示 Reader 持久化的 LLRP 版本策略与最近一次实际协商版本；因此 `192.168.41.148` 即使尚未重新连接或应用刚重启，也能明确看到 `Force LLRP 1.0.1`，不改变现有连接编排。

设备列表和设置页同时解释连接生命周期：短操作完成后的 `Disconnected` 会显示为能力已同步、短连接已释放；只有 Inventory 运行期间才保持 LLRP 长连接。

本轮又完成双 Reader 的短连接隔离：R420 与 148 并行完成激活、设置查询和 GPIO 查询，分别保持各自的扩展/标准能力上下文，均无 Faulted，操作结束后两台活动 TCP 均为 0；这证明多 Reader 的短操作隔离，不把它计作多 Reader 同时寻卡通过。

第二台标准 Reader 的能力快照没有声明 GPI/GPO 数量，但真实 `GetGpiStatusAsync`/`GetGpoStatusAsync` 各返回 4 个端口；平台继续把未知能力作为兼容回退，不把它误判为明确不支持，物理事件/触发仍须接线验收。

本轮补充：寻卡页继续以平台 `LifecycleChanged` 作为唯一收尾来源，并将手动停止、GPI 触发、定时结束、设备断开和 Reader 异常的 `Inventory.Status` 投影到主窗口底部状态栏，便于 WPF 真机验收直接确认停止原因；同时防止 Start 返回与早到终止生命周期事件之间的状态覆盖竞态；添加数据源页和主设备页对 Probe、添加、激活的未结构化异常统一投影为稳定设备错误并保留详细信息；Settings Query 在 ReaderBusy 时通过 Contracts 平台异常保留稳定错误码；无 GPI/GPO 能力的状态查询通过同一平台异常返回 Unsupported；构建与 307 项全量测试保持通过。

本轮已补齐 ReaderManager 退出期间的 Session 注册保护、已知能力下的 Inventory 天线边界、Tag Access 选择长度边界、Tab2 GPO 端口输入边界和按 GPI 能力数量生成/回写 Impinj debounce；设置 Tab1 分组和 Tab2 GPO/GPI 区域也按实际语义行、端口能力隐藏空控件；这些变化不改变现有 UI → Services → Contracts 依赖方向。

本轮还把 IPv6 端点归一化提升到 Contracts：程序化添加、服务端去重、SQLite Profile 保存、SDK 会话构造和 WPF 展示共用 Host 规则；带方括号的 IPv6 不会再因消费者不同而产生重复端点或传输构造差异。

发现服务、WPF 主设备页和添加数据源页现在共用 Contracts 的发现结果归一化器：重复端点、非法端口、空 Host 和 IPv6 展示在两个入口保持一致。

本轮再补两项收尾保证：ReaderManager 直接启动 Inventory 时的扩展探测取消会统一清理 Session 并回到 `Disconnected`；WPF 在收到平台停止事件后会继续排空已入队的最后一批 TagObserved，避免服务端先完成 Drain 而 UI 丢失尾部显示。两项均已加入自动化回归，当前基线为 307 项全绿。

2026-08-16 架构债务收口：厂商 Feature 常量已移出 Contracts，分别归属 Impinj/Zebra 扩展；
设置行增加 `SemanticId`/`GroupKey`/`InstanceKey`，WPF 不再按厂商 Key 定位扩展设置；Impinj/Zebra
报告投影统一输出 `ReportFieldSemantics`，WPF 只消费稳定语义字段；新增 Zebra 扩展测试并把
Zebra 扩展/SDK 纳入架构依赖测试。当前自动化基线为 368 项全绿（Architecture 9、Impinj 17、
Zebra 6），`dev` 继续允许本地 SDK 项目引用，发布/CI 仍固定 NuGet。

## 阶段出口

每阶段必须具备：

- 可独立构建的提交；
- 对应的自动化测试；
- 明确的设备/模拟器验收结果；
- 文档中的状态回填；
- 未完成项和已知限制记录。

旧 `LlrpReaderStudio` 不作为新仓库的项目引用或数据导入源。SQLite 只负责新平台自身数据；早期 schema 变化以清空数据库重建为可接受方案，SQLite、设置下发和完整 Inventory 都是最终交付范围，不是可选的后续事项。
