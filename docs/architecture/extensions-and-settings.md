# 厂商扩展与设置模型

## 厂商扩展目标

标准 Services 不直接引用 Impinj、Seuic 或其他厂商包。厂商包通过独立 `Extensions.*` 项目接入，并由应用组合根显式注册。

一个扩展模块应能够：

- 根据标准 Probe 的身份、能力和探测数据判断是否匹配；
- 在创建 SDK Reader 前配置协议扩展；
- 解析扩展能力和扩展设置；
- 贡献 UI 无关的设置条目和校验规则；
- 编译扩展设置；
- 将扩展 TagReport 字段投影为 Contracts DTO；
- 参与 Preset 的版本化序列化和兼容性诊断。

## Auto 两阶段流程

```text
标准 Probe
  -> Identity / Standard Capabilities
  -> ExtensionResolver.Match
  -> 无匹配：标准 Reader
  -> 有匹配：断开标准 Session
       -> ConfigureReader
       -> 扩展连接
       -> 读取扩展能力/设置
       -> 更新 ActiveExtensions
```

显式 Standard 模式跳过扩展匹配。显式强制扩展但模块不存在或 Reader 拒绝扩展时，返回明确错误，不静默降级。

模块通过宿主应用组合根注册，例如：

```text
AddLlrpReaderPlatform()
AddImpinjExtension()
```

Services 不扫描程序集，也不硬编码具体厂商模块。

设置贡献由扩展模块通过 Services 的 `ISettingsExtensionContributor` 暴露；宿主只注册模块，
标准编译器先生成标准布局，再按运行时 `ReaderFeatureCatalog` 判断该模块在当前型号上
是否真正贡献布局；Impinj 贡献者按 vendor 能力命名空间判断适用，不把某一个可选能力
（例如 FastID）当作厂商识别条件。Apply 时仍在同一 Reader Gate 和同一短连接租约内，把 Draft 编译回
SDK 扩展字典。当前 `Extensions.Impinj` 已提供
FastID、Phase/Doppler、Search Mode、Low Duty Cycle 和 Fixed Frequency 的这一条路径。
同时贡献 GPI debounce；其端口行数由运行时快照的 GPI 数量驱动，明确为 0 的设备不生成
厂商 debounce 参数，未知数量保留兼容回退；标准设置层负责 GPI Start/Stop、报告字段、天线/RF、Gen2 Filter
等通用语义。Inventory 启动时可由 UI 选择报告字段，Services 在同一长连接租约内编译并覆盖
报告位，避免为每次报告刷新重新连接 Reader。

Impinj 模块对协议 Builder 的匹配按厂商身份执行；R420 的 L4 能力则额外要求已实测的
ModelId `2001002`。因此未知 Impinj 型号仍可走标准/协议扩展连接路径，但不会自动声明
R420 专属设置能力。

## 设置模型

```text
SettingsLayout   设备能力决定的字段结构、选项、范围和校验规则
SettingsSnapshot Reader 当前设置的 UI 无关快照
SettingsDraft    用户正在编辑的值，带 ReaderId 和 CapabilityRevision
CompiledSettings Services 内部的标准设置和扩展设置结果
```

Layout 不保存用户输入，Draft 不保存 UI 控件对象。

设置项使用语义化 `EditorKind`：

```text
Boolean / Choice / Integer / Decimal / Text / Collection
```

天线、Filter、频率列表等复杂值可以使用专用语义模型。Contracts 不出现 TextBox、ComboBox、CheckBox、Visibility、Dispatcher 等 WPF 概念。

## 设置服务边界

UI 只依赖：

```text
IReaderSettingsService.QueryAsync
IReaderSettingsService.Validate
IReaderSettingsService.ApplyAsync
```

`SettingsCompiler`、`CompiledSettings`、SDK `ReaderSettings` 和厂商设置对象留在 Services/Extensions 内部。

Apply 前必须重新验证：

- Reader 是否仍然具有当前能力；
- Draft 的 CapabilityRevision 是否过期；
- 当前操作是否被 Inventory 占用；
- 所有标准和扩展参数是否都能编译并通过校验。
