# 发布规范

## 分支策略

日常开发分支使用本地 `LLRPCSharp` SDK 项目引用，便于断点调试和跨仓库联调。

发布分支当前命名为 `relse`。进入发布流程后，在该分支固定切换为 NuGet SDK 引用，发布产物不得依赖本机 `F:\Projects\LLRP\LLRPCSharp` 源码目录。

发布分支需要将平台项目中的本地 `ProjectReference` 替换为 NuGet `PackageReference`：

- `LlrpSdk`：`1.3.0`
- `LlrpSdk.Extensions.Impinj`：`1.3.0`

其中 `LlrpSdk 1.3.0` 已包含 `LlrpNet.Core`、`LlrpNet.Protocol` 和 SDK 扩展抽象等底层 DLL；`LlrpSdk.Extensions.Impinj 1.3.0` 传递依赖同版本的 `LlrpSdk`。平台不需要再单独引用底层 `LlrpNet` NuGet 包。

## 发布前检查

切换到 `relse` 分支并完成 NuGet 引用调整后，必须重新还原依赖。不要复用开发分支生成的 `obj/project.assets.json`，否则可能把本地项目引用误带入发布构建。

```powershell
git switch relse
dotnet restore LlrpReaderPlatform.slnx
dotnet build LlrpReaderPlatform.slnx -c Release --no-restore
dotnet test LlrpReaderPlatform.slnx -c Release --no-build --no-restore
```

发布前还应确认：

- Release 资产清单中的 `LlrpSdk/1.3.0` 类型为 `package`，不是 `project`；
- 发布输出目录中没有来自 `LLRPCSharp` 源码构建的 SDK 项目 DLL；
- 发布版本号与平台版本声明一致；
- `bin/`、`obj/` 和 `artifacts/` 等本地生成物不提交。

## 发布命令

验证通过后执行 WPF 发布：

```powershell
dotnet publish src/LlrpReaderPlatform.App.Wpf/App.Wpf.csproj `
  -c Release -r win-x64 --self-contained false `
  -o artifacts/publish/win-x64 --no-restore
```

发布分支的 NuGet 切换只用于发布基线；日常开发完成后，功能代码仍应同步回开发分支，并继续使用本地 SDK 项目引用进行调试。
