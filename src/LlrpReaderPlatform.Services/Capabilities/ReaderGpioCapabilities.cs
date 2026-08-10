using LlrpSdk;
using P101 = LlrpNet.Protocol.Parameters.V1_0_1;
using P11 = LlrpNet.Protocol.Parameters.V1_1;

namespace LlrpReaderPlatform.Services.Capabilities;

/// <summary>
/// 从标准 LLRP General Device Capabilities 提取 GPIO 数量。
/// null 表示设备没有返回可识别的 GPIO 能力参数，不能据此判定为不支持。
/// </summary>
internal sealed record ReaderGpioCapabilities(ushort GpiCount, ushort GpoCount)
{
    public static ReaderGpioCapabilities? From(ReaderCapabilities? capabilities)
    {
        if (capabilities is null)
        {
            return null;
        }

        foreach (LlrpNet.Protocol.Parameters.ILlrpParameter parameter in capabilities.GeneralDeviceParameters)
        {
            ReaderGpioCapabilities? result = parameter switch
            {
                P101.GeneralDeviceCapabilities value => From(value.GPIOCapabilities),
                P11.GeneralDeviceCapabilities value => From(value.GPIOCapabilities),
                _ => null,
            };
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    private static ReaderGpioCapabilities From(P101.GPIOCapabilities capabilities) =>
        new(capabilities.NumGPIs, capabilities.NumGPOs);

    private static ReaderGpioCapabilities From(P11.GPIOCapabilities capabilities) =>
        new(capabilities.NumGPIs, capabilities.NumGPOs);
}
