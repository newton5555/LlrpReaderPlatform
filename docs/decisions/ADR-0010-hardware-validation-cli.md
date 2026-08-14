# ADR-0010：增加硬件验证命令行入口

- 状态：已接受
- 日期：2026-08-13

## 背景

真实 Reader 的网络发现和 mDNS 响应需要在现场网络中验证。WPF 应用适合完整验收，但只为确认服务发现、地址、端口和 TXT 属性而启动 UI 成本较高，也不利于快速诊断网络问题。

## 决策

在 `tests/LlrpReaderPlatform.Hardware.Tests` 增加一个可运行的 .NET 命令行项目，作为硬件测试辅助入口：

- 默认复用 `LlrpReaderPlatform.Infrastructure.Discovery.ZeroconfReaderDiscoveryService`，验证 `_llrp._tcp.local.` Reader 发现及平台契约归一化结果；
- 通过 `--all-services` 提供 `_services._dns-sd._udp.local.` 的 mDNS 服务枚举，便于现场确认服务类型；
- 通过 `--scan-seconds` 控制扫描时间，并支持 Ctrl+C 取消；
- 只执行发现和诊断，不连接 Reader、不修改设备设置、不启动 Inventory，也不替代 WPF 的完整硬件验收流程。

## 结果

硬件验证运行手册提供该 CLI 的命令。真实设备的能力、Inventory、Tag Access、GPI/GPO 和厂商扩展结论仍必须按设备矩阵和 WPF 验收流程记录。
