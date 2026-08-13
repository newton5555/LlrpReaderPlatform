# 发布规范与应用流水线

## 交付边界

`LlrpReaderPlatform` 当前是 WPF 应用，不发布平台 NuGet 包。正式发布产物为：

- `LlrpReaderPlatform-v<version>-win-x64.zip`：包含自包含单文件
  `LlrpReaderPlatform.exe`、README 和发布说明；
- 对应的 `.sha256` 校验文件；
- GitHub Release 页面和版本说明。

平台使用的 `LlrpSdk` NuGet 包属于输入依赖，不是本仓库的发布产物。

## GitHub Actions

仓库包含两条自动流程：

- `.github/workflows/ci.yml`：`master`、`release/*` 的 push/PR，以及手动触发时执行 NuGet 模式还原、Release 构建和自动化测试；
- `.github/workflows/release.yml`：推送 `vMAJOR.MINOR.PATCH` Tag 或手动触发时，重复执行构建和测试，然后发布 `win-x64` ZIP、SHA256 和 GitHub Release。

发布流程会校验 Tag、`Directory.Build.props` 中的版本号和 `docs/releases/v<version>.md` 是否一致；任一不一致都会停止发布。

## SDK 引用模式

主分支、CI 和发布默认使用 NuGet：

- `LlrpSdk`：`1.3.0`
- `LlrpSdk.Extensions.Impinj`：`1.3.0`

版本统一维护在仓库根部的 `Directory.Packages.props`。`LlrpSdk` 包含平台所需的
LlrpNet 和 SDK 扩展抽象依赖，`LlrpSdk.Extensions.Impinj` 传递依赖同版本的
`LlrpSdk`；平台在 NuGet 模式下不单独引用底层 LlrpNet 包。

不再维护“本地项目引用分支”和“NuGet 发布分支”，也不在发布前手工改写 csproj。

## 本地 SDK 联调

命令行一次性启用本地项目引用：

```powershell
dotnet build LlrpReaderPlatform.slnx `
  -p:UseLocalLlrpSdk=true `
  -p:LlrpSdkSourceRoot=F:\Projects\LLRP\LLRPCSharp
```

Visual Studio 长期启用本机设置时，将根目录的
`Directory.Build.local.props.example` 复制为 `Directory.Build.local.props`，确认其中
`UseLocalLlrpSdk` 为 `true`，然后重新加载解决方案并还原依赖。复制后的文件已被 Git
忽略，不会影响其他开发者、CI 或发布。

切回 NuGet 时，删除 `Directory.Build.local.props`，或把其中属性改为 `false`，再重新
加载解决方案并还原。若 Visual Studio 仍显示旧依赖，关闭 Visual Studio，清理各项目
`obj/` 后重新打开；不要提交这些生成物。

## 发布前检查

发布版本在 `release/*` 分支完成。创建或切换到发布分支后，必须显式指定 NuGet 模式，
不能只依赖默认值，因为开发机可能存在 Git 忽略的 `Directory.Build.local.props`：

```powershell
git switch release/<version>
dotnet restore LlrpReaderPlatform.slnx -p:UseLocalLlrpSdk=false
dotnet build LlrpReaderPlatform.slnx -c Release -p:UseLocalLlrpSdk=false --no-restore
dotnet test LlrpReaderPlatform.slnx -c Release -p:UseLocalLlrpSdk=false --no-build --no-restore
```

命令行属性优先于本地 props，因此即使开发机保留本地联调配置，上述命令仍强制使用
NuGet。发布前还应确认：

- Release 资产清单中的 `LlrpSdk/1.3.0` 类型为 `package`，不是 `project`；
- 发布版本号与平台版本声明一致；
- `bin/`、`obj/` 和 `artifacts/` 等本地生成物不提交。

## 发布命令

验证通过后执行 WPF 自包含单文件发布：

```powershell
dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:DebugSymbols=false `
  -p:UseLocalLlrpSdk=false `
  -o artifacts/publish/win-x64 --no-restore
```

发布目录的主文件名由 `App.Wpf.csproj` 的 `AssemblyName` 控制，当前为
`LlrpReaderPlatform.exe`。`ApplicationIcon` 只控制 EXE 图标，不控制文件名。
本地只含 EXE 的便携 ZIP 不属于 GitHub Release 的额外资产，可按需从发布目录另行压缩。

## 正式发布步骤

以 `1.0.0` 为例：

1. 从目标基线创建 `release/1.0.0` 分支，确认 `Directory.Build.props` 的版本为 `1.0.0`；
2. 补齐 `docs/releases/v1.0.0.md`，并确认本地构建、测试和自包含单文件发布成功；
3. 在 `release/1.0.0` 分支推送匹配的 `v1.0.0` Tag：

   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```

4. GitHub Actions 自动运行发布流程；成功后在 GitHub Release 下载 ZIP 和 SHA256 文件；
5. 解压 ZIP，在现场 Windows 机器上启动应用并按[真机验收运行手册](hardware-validation-runbook.md)完成最后验收。

也可以在 GitHub Actions 页面手动运行 `WPF Release`，输入与项目版本一致的 Tag。手动运行同样不会跳过构建、测试和版本说明校验。
