using LlrpReaderManager.State;
using LlrpReaderManager.VirtualDevices;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Extensions.Zebra;
using LlrpReaderPlatform.Infrastructure;
using LlrpReaderPlatform.Services;
using Microsoft.Extensions.Logging;

namespace LlrpReaderManager;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddLlrpReaderPlatform();
        builder.Services.AddLlrpInfrastructure();
        builder.Services.AddImpinjExtension();
        builder.Services.AddZebraExtension();
        builder.Services.AddSingleton<ReaderManagerState>();
        builder.Services.AddSingleton<VirtualReaderWidgetService>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
