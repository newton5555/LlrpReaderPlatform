using LlrpReaderPlatform.Contracts.Readers;

namespace LlrpReaderPlatform.App.Wpf.ViewModels;

internal static class ReaderEndpointFormatter
{
    public static string NormalizeHost(string host) => ReaderEndpoint.NormalizeHost(host);

    public static string Format(string host, int port) => ReaderEndpoint.Format(host, port);
}
