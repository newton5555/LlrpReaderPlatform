# ADR-0006：开发期直接引用本地 LLRPCSharp SDK 项目

- 状态：Accepted
- 日期：2026-08-12

## 决策

在当前 SDK 报告出口重构和平台联调阶段，`LlrpReaderPlatform.Services` 与
`LlrpReaderPlatform.Extensions.Impinj` 直接引用相邻 `LLRPCSharp` 仓库中的 SDK
项目，而不是引用同版本 NuGet 缓存包。

平台根部的 `LlrpSdkSourceRoot` 默认指向 `F:\Projects\LLRP\LLRPCSharp` 的同级目录，
也可以在构建时覆盖。由于 SDK 包内的 `LlrpNet` 依赖被标记为私有，平台项目同时显式
引用所需的本地 `LlrpNet` 项目。

## 原因

SDK 与平台正在同步调整 Inventory 报告出口所有权。直接项目引用可以让平台立即编译和
验证当前 SDK 源码，避免同为 `1.2.1` 的旧 NuGet 包被全局缓存误用。

## 影响

- 平台构建环境必须同时具备 `LlrpReaderPlatform` 和 `LLRPCSharp` 两个仓库；
- SDK 修改后平台无需重新打包即可编译验证；
- SDK 仍是独立仓库，平台不复制其源码，也不让 UI 直接引用 SDK；
- 发布包模式暂不作为当前开发验收入口，待 SDK API 稳定后另行恢复并记录切换决策。
