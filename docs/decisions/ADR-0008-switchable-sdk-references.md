# ADR-0008：NuGet 默认与本地 SDK 项目可选切换

- 状态：Accepted
- 日期：2026-08-13
- 取代：[ADR-0006](ADR-0006-local-sdk-project-reference.md)

## 决策

平台所有分支默认引用中央版本管理声明的 `LlrpSdk` 和
`LlrpSdk.Extensions.Impinj` NuGet 包。CI、发布和未配置本地覆盖的 Visual Studio
构建都使用 NuGet，不再通过专用长期分支维护另一套项目文件。

需要跨仓库联调或进入 SDK 源码调试时，通过 MSBuild 属性
`UseLocalLlrpSdk=true` 将 SDK 包引用替换为相邻 `LLRPCSharp` 仓库中的
`ProjectReference`。`LlrpSdkSourceRoot` 指定源码根目录，默认值为平台仓库的相邻
`../LLRPCSharp`。

命令行可以直接传入这两个属性。Visual Studio 使用者可以复制仓库中的
`Directory.Build.local.props.example` 为 `Directory.Build.local.props`；复制后的文件
由 Git 忽略，只保存本机偏好。修改该文件后需要重新加载解决方案并还原依赖。

默认解决方案只登记平台项目，不登记相邻仓库项目。启用本地模式后，MSBuild 会通过条件
`ProjectReference` 构建所需 SDK 项目。

## 原因

- 默认 NuGet 构建可复现，不要求每台开发机同时检出 SDK 仓库；
- 发布与日常开发使用同一种默认依赖，避免合并前后手工改写项目文件；
- 本地项目模式仍保留 SDK 源码断点调试和跨仓库即时验证能力；
- 引用模式属于构建参数，不应由长期分支承载。

## 影响

- NuGet 包版本只在 `Directory.Packages.props` 中维护；
- 本地模式必须具有有效的 `LlrpSdkSourceRoot`，并且 SDK 源码应与平台预期 API 兼容；
- 从一种模式切换到另一种模式后，应重新加载 Visual Studio 解决方案并执行 restore；若
  设计时缓存未刷新，可关闭 Visual Studio 后清理各项目 `obj/` 再重新打开；
- CI 使用默认值或显式设置 `UseLocalLlrpSdk=false`，从而验证 NuGet 模式；
- 发布版本在 `release/*` 分支完成，发布流程必须显式设置
  `UseLocalLlrpSdk=false`，避免本机 `Directory.Build.local.props` 意外覆盖发布依赖。
