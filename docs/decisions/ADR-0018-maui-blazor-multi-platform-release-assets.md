# ADR-0018：MAUI Blazor 多平台发布资产

- 状态：Accepted
- 日期：2026-08

## 决策

`LlrpReaderManager` 作为 MAUI Blazor Hybrid 应用，使用以下目标平台并由发布流水线分别生成资产：

- `net10.0-windows10.0.19041.0`：Windows x64 目录包；
- `net10.0-maccatalyst`：Mac Catalyst，分别生成 `maccatalyst-x64` 和 `maccatalyst-arm64` 未签名应用包；
- `net10.0-android`：APK。

正式发布同时保留两个 WPF 应用的 Windows x64 自包含单文件包。所有资产使用独立文件名和 SHA256
校验文件，统一挂载到同一个版本的 GitHub Release。发布仍由符合版本号的 `vMAJOR.MINOR.PATCH`
Tag 触发或手动触发，不由普通分支推送触发。

## 背景

仓库已经包含主客户端 WPF、独立虚拟设备管理 WPF 和 MAUI Blazor Hybrid 三个 UI 消费者。旧流程只
发布主客户端，无法让使用者取得其他已完成的 UI 消费者的可运行版本。MAUI 项目同时覆盖 Windows、
Android 和 Mac Catalyst，不能用一个 Windows WPF 发布命令代替所有平台。

## 候选方案

- 只继续发布主客户端：改动最小，但无法交付其他已完成的 UI 消费者。
- 将所有项目打入一个 Windows ZIP：平台边界错误，且不能交付 Android/Mac Catalyst。
- 按应用和平台拆分资产：资产清晰，能使用各平台原生 runner，代价是流水线步骤和验证矩阵增加。

## 原因

选择按应用和平台拆分。WPF 使用当前已经验证的 Windows x64 自包含单文件策略；MAUI Blazor 使用
各自的 TFM 和 runner。Mac Catalyst/Android 的签名、商店提交和真机验收属于后续平台发布工作，
不把未配置的证书或 provisioning profile 写入仓库。

## 影响

- `LlrpReaderManager.csproj` 必须声明三平台目标；Windows runner 构建时跳过只能在 Apple 工具链上
  完成的 Mac Catalyst target，Mac runner 单独发布该 target。
- `.github/workflows/release.yml` 需要 Windows、Android、Mac Catalyst 和统一 Release 汇总作业。
- 发布文档必须区分 Windows 目录包、Mac Catalyst 未签名包和 Android APK，不能把它们描述成同一种
  单文件交付。
- Mac Catalyst 签名、公证、Android 签名和商店发布仍需凭据与平台验收，不由本 ADR 自动解决。
