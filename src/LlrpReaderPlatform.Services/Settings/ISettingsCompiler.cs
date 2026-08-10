using LlrpReaderPlatform.Contracts.Readers;
using LlrpReaderPlatform.Contracts.Settings;

namespace LlrpReaderPlatform.Services.Settings;

/// <summary>
/// 设置编译器（Services 内部）：能力驱动的布局/快照生成 + Draft 编译。
/// 标准设置的编译器据此实现；扩展模块可通过扩展贡献项（F5）。
/// </summary>
public interface ISettingsCompiler
{
    EffectiveSettingsLayout BuildLayout(ReaderRuntimeSnapshot snapshot);

    SettingsSnapshot BuildSnapshot(ReaderRuntimeSnapshot snapshot);

    CompiledSettings Compile(SettingsDraft draft, EffectiveSettingsLayout layout);
}
