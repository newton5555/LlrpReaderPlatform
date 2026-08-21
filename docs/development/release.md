# 发布规范与应用流水线

## 交付边界

`LlrpReaderPlatform` 不发布平台 NuGet 包。正式发布按应用和平台拆分为独立资产：

- `LlrpReaderPlatform-v<version>-win-x64.zip`：包含自包含单文件
  `LlrpReaderPlatform.exe`、README 和发布说明；
- `LlrpVirtualDeviceManager-v<version>-win-x64.zip`：虚拟设备管理 WPF 应用；
- `LlrpReaderManager-v<version>-win-x64.zip`：MAUI Blazor Windows 桌面应用；
- `LlrpReaderManager-v<version>-macos-x64.zip` 和
  `LlrpReaderManager-v<version>-macos-arm64.zip`：Mac Catalyst 应用包；
- `LlrpReaderManager-v<version>-android.apk`：Android 安装包；
- `LlrpReaderManager-v<version>-linux-x64.deb`：Linux GTK4 x64 Debian 安装包；
- 每个应用资产对应的 `.sha256` 校验文件；
- GitHub Release 页面和版本说明。

平台使用的 `LlrpSdk` NuGet 包属于输入依赖，不是本仓库的发布产物。

两个 WPF 应用使用 Windows x64 自包含单文件交付；`LlrpReaderManager` 是 MAUI Blazor
Hybrid 应用，Windows 使用目录包，Mac Catalyst 使用未签名 `.app` 压缩包，Android 使用 APK；Linux
GTK4 使用独立 Head 生成 framework-dependent `.deb`。
Mac Catalyst 的签名、公证和 Android 的商店签名不由当前流水线提供，正式分发前需要补充对应平台凭据。
虚拟设备管理 UI 已切换为直接引用 `LlrpDevice.Virtual.Hosting` 顶层 NuGet 包；运行和构建方式见
[Virtual Reader 开发模式](virtual-reader.md)。

## 下载与运行

主客户端从 GitHub Release 下载对应版本的 `LlrpReaderPlatform-v<version>-win-x64.zip`，解压后直接运行
`LlrpReaderPlatform.exe`。虚拟设备管理器使用对应的 `LlrpVirtualDeviceManager` 包；Windows 两个包都是
自包含单文件，目标机无需另装 .NET Desktop Runtime。Reader 需要通过网络可达，默认 LLRP 端口为 `5084`。首次运行会在
`%LocalAppData%\LlrpReaderPlatform\` 创建 SQLite 数据库、日志和盘存快照目录。

### Mac Catalyst 应用

GitHub Release 中的 `LlrpReaderManager-v<version>-macos-arm64.zip` 用于 Apple Silicon Mac，
`LlrpReaderManager-v<version>-macos-x64.zip` 用于 Intel Mac。按芯片选择对应资产，解压后运行其中的
`LlrpReaderManager.app`，也可以将它复制到 `/Applications`。该应用是自包含的，不需要另外安装 .NET Runtime；
Reader 仍需通过网络可达，默认 LLRP 端口为 `5084`。

macOS 支持在 App Store 之外直接分发 `.app`，因此可以通过 ZIP 解压后运行；但这不等于没有安全限制。
当前流水线生成的是未签名、未公证的内部/测试包，不等同于 App Store 或 Developer ID 正式分发包。首次打开时如果
macOS 提示无法验证开发者，请在 Finder 中右键应用选择“打开”；如果仍被拦截，在“系统设置 → 隐私与安全性”中选择
“仍要打开”。仅对确认来自可信 Release 的包，必要时可清除下载隔离标记：

```bash
xattr -dr com.apple.quarantine /Applications/LlrpReaderManager.app
open /Applications/LlrpReaderManager.app
```

正式对外分发时，还需要补充 Developer ID 签名和公证，以及相应的安装/升级验收。

## GitHub Actions

仓库包含两条自动流程：

- `.github/workflows/ci.yml`：`master`、`release/*` 的 push/PR，以及手动触发时分别在 Windows、macOS 和 Ubuntu 执行对应的构建检查；通用服务和测试在 Windows，Mac Catalyst 在 macOS，Linux GTK4 在 Ubuntu。
- `.github/workflows/release.yml`：推送 `vMAJOR.MINOR.PATCH` Tag 或手动触发时，重复执行构建和测试，然后并行发布 WPF、MAUI Blazor 的 Windows/Mac Catalyst/Android 资产，以及 Linux GTK4 `.deb`、SHA256 和 GitHub Release。

发布流程以 Tag 为触发依据，不自动限制 Tag 来源分支；团队仍应在正式 `release/*` 分支准备版本。
流程会校验 Tag、`Directory.Build.props` 中的版本号和 `docs/releases/v<version>.md` 是否一致；任一不一致都会停止发布。

## SDK 引用模式

主分支、CI 和发布默认使用 NuGet：

- `LlrpSdk`：`2.0.1`
- `LlrpSdk.Extensions.Impinj`：`2.0.1`
- `LlrpSdk.Extensions.Zebra`：`2.0.1`
- `LlrpDevice.Virtual.Hosting`：`2.0.1`（`LlrpVirtualDevice.App.Wpf` 和 Blazor 虚拟设备挂件的直接依赖）

版本统一维护在仓库根部的 `Directory.Packages.props`。`LlrpSdk` 包含平台客户端所需的
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

- Release 资产清单中的 `LlrpSdk/2.0.1` 和 `LlrpDevice.Virtual.Hosting/2.0.1` 类型为 `package`，不是 `project`；
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

MAUI Blazor 的平台发布必须针对项目本身执行，不能对整个解决方案执行 `dotnet publish`：

```powershell
dotnet publish src/LlrpReaderManager/LlrpReaderManager.csproj `
  -f net10.0-windows10.0.19041.0 -c Release -r win-x64 `
  --self-contained true -p:WindowsPackageType=None -p:PublishReadyToRun=false `
  -p:UseLocalLlrpSdk=false

dotnet publish src/LlrpReaderManager/LlrpReaderManager.csproj `
  -f net10.0-android -c Release --self-contained true `
  -p:AndroidPackageFormat=apk -p:UseLocalLlrpSdk=false
```

Mac Catalyst 发布在 macOS runner 上分别使用 `maccatalyst-x64` 和 `maccatalyst-arm64`，
生成未签名 `.app` 后压缩为对应资产；签名发布需要另行提供 Apple 证书和 provisioning profile。

Linux GTK4 发布在 Ubuntu runner 上执行，安装 GTK4/WebKitGTK 运行库后生成 framework-dependent
`.deb`，原始产物位于 `src/LlrpReaderManager.Linux/bin/Deb/`。还原、构建和发布必须带
`--runtime linux-x64`，否则可能因缺少 Runtime target 导致 `project.assets.json` 错误。目标机需要预先安装匹配的 .NET Runtime；安装包由 `dpkg`/`apt` 管理，卸载使用
`sudo apt remove readermanager` 或 `sudo dpkg -r readermanager`。

## 正式发布步骤

以 `1.0.0` 为例：

1. 从目标基线创建 `release/1.0.0` 分支，确认 `Directory.Build.props` 的版本为 `1.0.0`；
2. 补齐 `docs/releases/v1.0.0.md`，并确认本地构建、测试和自包含单文件发布成功；
3. 在 `release/1.0.0` 分支推送匹配的 `v1.0.0` Tag：

   ```powershell
   git tag v1.0.0
   git push origin v1.0.0
   ```

4. GitHub Actions 自动运行发布流程；成功后在 GitHub Release 下载对应平台资产和 SHA256 文件；
5. 按目标平台安装或解压资产，在现场设备上启动应用并按[真机验收运行手册](hardware-validation-runbook.md)完成最后验收。

也可以在 GitHub Actions 页面手动运行 `Application Release`，输入与项目版本一致的 Tag。手动运行同样不会跳过构建、测试和版本说明校验。
