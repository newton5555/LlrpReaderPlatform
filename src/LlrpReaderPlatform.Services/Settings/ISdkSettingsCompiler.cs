using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;
using LlrpSdk;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 标准设置编译器的 SDK 适配面。它仍属于 Services，故 SDK 类型不会进入 Contracts
/// 或 WPF；厂商扩展以后可提供自己的同类适配器。
/// </summary>
public interface ISdkSettingsCompiler
{
    EffectiveSettingsLayout BuildLayout(
        ReaderRuntimeSnapshot snapshot,
        ReaderSettingsRuntimeSnapshot runtime);

    SettingsSnapshot BuildSnapshot(
        ReaderRuntimeSnapshot snapshot,
        ReaderSettingsRuntimeSnapshot runtime);

    ReaderSettings CompileSdk(
        SettingsDraft draft,
        EffectiveSettingsLayout layout,
        ReaderSettingsRuntimeSnapshot runtime,
        ReaderRuntimeSnapshot reader);
}
