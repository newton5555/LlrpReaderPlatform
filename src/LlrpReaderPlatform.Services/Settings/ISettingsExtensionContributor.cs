using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpSdk;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 厂商设置的 Services 扩展点。标准编译器只负责生命周期和标准字段，具体厂商模块在
/// 本接口中贡献平台语义布局并把值编译回 SDK 设置；厂商类型不会进入 Contracts 或 WPF。
/// </summary>
public interface ISettingsExtensionContributor
{
    string Id { get; }

    bool IsApplicable(ReaderRuntimeSnapshot reader);

    void ContributeLayout(
        IList<SettingsEntry> entries,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime);

    ReaderSettings Apply(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        ReaderRuntimeSnapshot reader,
        ReaderSettingsRuntimeSnapshot runtime,
        ReaderSettings settings);
}
