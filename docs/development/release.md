# 发布规范

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

验证通过后执行 WPF 发布：

```powershell
dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:UseLocalLlrpSdk=false `
  -o artifacts/publish/win-x64 --no-restore
```

发布完成后创建版本 Tag；只有需要维护已发布版本时才从对应 Tag 创建短期维护分支。
