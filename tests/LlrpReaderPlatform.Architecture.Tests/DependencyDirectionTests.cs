using System.Reflection;
using Xunit;

namespace LlrpReaderPlatform.Architecture.Tests;

/// <summary>
/// 强化主计划中的依赖方向铁律：Contracts/Services/Infrastructure/Extensions 不得引用 WPF/UI 程序集；
/// Contracts 不得引用 SDK 或厂商扩展。UI（App.Wpf）是末端消费者。
/// </summary>
public sealed class DependencyDirectionTests
{
    private static readonly Assembly ContractsAssembly = typeof(LlrpReaderPlatform.Contracts.ContractsMarker).Assembly;
    private static readonly Assembly ServicesAssembly = typeof(LlrpReaderPlatform.Services.ServicesMarker).Assembly;
    private static readonly Assembly InfrastructureAssembly = typeof(LlrpReaderPlatform.Infrastructure.InfrastructureMarker).Assembly;
    private static readonly Assembly ExtensionsAssembly = typeof(LlrpReaderPlatform.Extensions.Impinj.ExtensionsMarker).Assembly;
    private static readonly Assembly WpfAssembly = typeof(LlrpReaderPlatform.App.Wpf.AppMarker).Assembly;

    private static readonly string[] WpfUiAssemblies =
    [
        "PresentationFramework",
        "PresentationCore",
        "WindowsBase",
        "WindowsFormsIntegration",
    ];

    private static readonly string[] SdkAssemblies =
    [
        "LlrpSdk",
        "LlrpNet.Core",
        "LlrpNet.Protocol",
        "LlrpSdk.Extensions.Impinj",
    ];

    public static TheoryData<Assembly> SharedLayers =>
        new()
        {
            ContractsAssembly,
            ServicesAssembly,
            InfrastructureAssembly,
            ExtensionsAssembly,
        };

    [Theory]
    [MemberData(nameof(SharedLayers))]
    public void Shared_layers_must_not_reference_WPF(Assembly assembly)
    {
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), r => WpfUiAssemblies.Contains(r.Name));
    }

    [Fact]
    public void Contracts_must_not_reference_Sdk_or_vendor_types() =>
        Assert.DoesNotContain(ContractsAssembly.GetReferencedAssemblies(), r => SdkAssemblies.Contains(r.Name));

    [Fact]
    public void Services_must_not_reference_Impinj_vendor_extension() =>
        Assert.DoesNotContain(ServicesAssembly.GetReferencedAssemblies(), r => r.Name is "LlrpSdk.Extensions.Impinj");

    [Fact]
    public void Wpf_is_the_only_layer_that_references_ui_frameworks() =>
        Assert.Contains(WpfAssembly.GetReferencedAssemblies(), r => r.Name is "PresentationFramework");
}
