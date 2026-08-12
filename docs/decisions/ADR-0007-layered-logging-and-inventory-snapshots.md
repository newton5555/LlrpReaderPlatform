# ADR-0007：分层日志与盘存最终快照

- 状态：已接受
- 日期：2026-08-12

## 背景

原有日志同时承载平台服务、WPF 操作、SDK/LLRP 协议和 EF Core 数据库诊断，
导致用户行为无法与 Reader/协议故障区分；盘存期间的高频标签报告也不适合写入普通应用日志。

## 决策

1. 应用日志分为 `ui-*.log`（WPF 操作和异常）、`platform-*.log`（Services/Infrastructure）
   和 `sdk-*.log`（LLRP/SDK/厂商协议）。
2. EF Core SQL、参数和查询诊断不进入文件日志；EF Core 只保留 Warning 及以上。
3. WPF 操作记录结构化 `Operation`、`OperationId`、`ReaderId`、结果和错误码，不记录每个标签报告。
4. 盘存数据策略为 `Off`、`FinalSnapshot`、`RawReports`：默认是 `FinalSnapshot`，停止、定时结束、
   GPI、故障或退出时由 Services 排空报告后写一次最终聚合 JSON；`RawReports` 额外写原始 JSONL。
5. 最终快照来自 Services 聚合状态；SQLite 只保存运行汇总及快照/原始日志路径，不保存高频标签行。
6. 旧版 `tag-logging-enabled=True/False` 兼容映射为 `RawReports`/`FinalSnapshot`，新设置优先使用
   `inventory-logging-mode`。

## 后果

- 用户操作、平台故障和协议故障可分别检索；同一 `OperationId` 可关联一次 UI 操作链路。
- 默认模式不会因标签速率导致普通日志快速增长；逐报告证据需要显式选择 `RawReports`。
- 快照与原始报告是文件数据，备份时应同时备份快照目录、原始日志目录和 SQLite 文件。
