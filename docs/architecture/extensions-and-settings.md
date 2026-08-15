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

LLRP 能力响应中的表项是表驱动设置的唯一选项源：`TxPowers`、`RxSensitivities`、`RfModes`
以及频率表生成 `SettingsOption`。选项的 `Value` 始终是设备要接收的 index/id，`Display`
统一使用“索引（具体描述）”格式；例如 `7 (30.5 dBm)`、`2 (6 dB offset)`、`20 (FM0)`。
Draft、CompiledSettings 和 SDK `ReaderSettings` 都沿用该 index/id，不把显示用的物理描述反向换算成邻近表项。
Rx 的描述只用于说明 dB offset，不作为写入值。没有能力表时才退回范围文本编辑；有能力表时使用 Choice 下拉。

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

Reader 的当前配置与 managed ROSpec 是两个查询范围。设备没有初始 ROSpec 时，设置编译器
会以新的默认 `InventorySettings` 作为待部署 ROSpec；这不代表设备配置为空，`GET_READER_CONFIG`
Reader 的当前配置与 managed ROSpec 是两个查询范围。设备没有初始 ROSpec 时，设置编译器
会以新的默认 `InventorySettings` 作为待部署 ROSpec；这不代表设备目前已空，`GET_READER_CONFIG`
返回的 `ReaderConfiguration.Antennas`、事件、GPO 等仍作为 `SET_READER_CONFIG` 基线保留并回写。

## 新厂商模块接入清单（面向 1.1/2.0 与多厂商）

接入一个新厂商（或既有厂商在更高协议版本上的扩展）时，按以下清单逐项核对；参照现有
`Extensions.Impinj` / `Extensions.Zebra` 两个已接入模块的结构。

1. **新项目**：新建 `src/LlrpReaderPlatform.Extensions.<Vendor>/`，csproj 采用双模式引用
   （本地 `ProjectReference` 条件 `UseLocalLlrpSdk == 'true'`；否则 `PackageReference`）
   `LlrpSdk.Extensions.<Vendor>`），并登记进 `LlrpReaderPlatform.slnx` 与 `Directory.Packages.props`。
2. **模块适用性**（两轴门控）：厂商轴按 `ManufacturerId` + 能力画像判定；标准轴需要时按
   `info.ProtocolVersion` 判定（与 SDK matcher 一致）；两轴取交集，避免在错误协议版本上配扩展。
3. **语义键与毕业元数据**：每个 `Feature` 必须有稳定 `SemanticId`；语义会被标准吸收时立即标
   `StandardizedSince`（ADR-0012）。
4. **设置贡献者**：实现 `ISettingsExtensionContributor`，只在 `Supports(feature)` 时贡献行；
   UI 行走泛型 `SettingsEntryRowViewModel`，渲染层零感知。
5. **报告投影**：实现 `ProjectTagReport` 把扩展字段投影为稳定字符串，落 `ReaderTagReportProjection.Fields`；
   寻卡页如需专属列再补列头选择器开关。
6. **组合根**：宿主显式 `services.Add<Vendor>Extension()` 注册，不扫描 / 不硬编码。
7. **测试与验证门槛**：厂商模块测试（适用性、门控、layout/Apply/投影往返）+ 至少一台真机按设备矩阵 L4 验收。
   未真机验收前声明为实验性（如 Zebra），不提升支持等级。
8. **对版本组合的准备**：不假设厂商只匹配 1.0.1；通过 `ProtocolVersion` 显式声明，天然支持
   未来 1.1/2.0 + 厂商组合，不产生按版本拆 UI 的组合爆炸。
