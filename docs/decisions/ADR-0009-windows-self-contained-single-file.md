# ADR-0009：Windows x64 自包含单文件交付

- 状态：Accepted
- 日期：2026-08-13

## 决策

首个 WPF 消费者的 Windows x64 正式交付统一使用 .NET 自包含、单文件发布：

- 输出程序集名为 `LlrpReaderPlatform`，主文件为 `LlrpReaderPlatform.exe`；
- 发布目标为 `win-x64`；
- 发布使用 `--self-contained true`、`PublishSingleFile=true` 和
  `IncludeNativeLibrariesForSelfExtract=true`；
- 正式 GitHub Release ZIP 附带 README 和发布说明；本地现场便携包可以只包含 EXE；
- 发布仍必须使用 NuGet SDK 模式，不能从本地 `LLRPCSharp` 项目引用模式产出。

## 背景

现场验收需要把 WPF 应用复制到 Windows x64 机器后直接运行，减少目标机运行时安装和版本差异。
之前的 `v1.0.0` Tag 使用框架依赖部署，实际 EXE 名称也仍为 `App.Wpf.exe`；当前源码已完成
程序集改名和单文件交付收口。

## 候选方案

- 框架依赖、多文件发布：包较小，但要求目标机安装匹配的 .NET Desktop Runtime；
- 自包含、多文件发布：不要求运行时安装，但现场文件较多；
- 自包含、单文件发布：现场交付最简单，代价是 EXE 体积较大，native 组件可能在启动时临时解压。

## 原因

选择自包含单文件是为了让现场验收入口稳定、可复制、与目标机的 .NET 安装状态解耦。发布包体积和
启动时临时解压属于可接受的 WPF 单文件运行代价。

## 影响

- CI 发布必须校验 `LlrpReaderPlatform.exe` 存在，并以它为应用主文件；
- 用户文档不再要求预装 .NET Desktop Runtime；
- 版本发布仍需在 `release/*` 分支完成，并显式使用 `UseLocalLlrpSdk=false`；
- 变更发布模型时必须新增 ADR，不直接修改历史 `v1.0.0` 说明。
