using LlrpReaderManager.State;
using LlrpReaderManager.VirtualDevices;
using LlrpReaderPlatform.Extensions.Impinj;
using LlrpReaderPlatform.Extensions.Zebra;
using LlrpReaderPlatform.Infrastructure;
using LlrpReaderPlatform.Services;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Microsoft.Extensions.Logging;
using Microsoft.Maui.Platforms.Linux.Gtk4.Hosting;
using Microsoft.Maui.Platforms.Linux.Gtk4.BlazorWebView;
using Microsoft.Maui.Platforms.Linux.Gtk4.Essentials.Hosting;
using System.Runtime.Versioning;
using LinuxBlazorWebViewHandler = Microsoft.Maui.Platforms.Linux.Gtk4.BlazorWebView.BlazorWebViewHandler;

namespace LlrpReaderManager;

[SupportedOSPlatform("linux")]
public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        MauiAppBuilder builder = MauiApp
            .CreateBuilder()
            .UseMauiAppLinuxGtk4<App>();

        // The standard MAUI registration only maps the built-in mobile/desktop
        // handlers. GTK4 provides its own BlazorWebView handler and registration.
        builder.Services.AddBlazorWebView();
        builder.Services.AddLinuxGtk4BlazorWebView();
        builder.ConfigureMauiHandlers(handlers =>
            handlers.AddHandler<BlazorWebView, LinuxBlazorWebViewHandler>());
        builder.AddLinuxGtk4Essentials();
        builder.Services.AddLlrpReaderPlatform();
        builder.Services.AddLlrpInfrastructure();
        builder.Services.AddImpinjExtension();
        builder.Services.AddZebraExtension();
        builder.Services.AddSingleton<ReaderManagerState>();
        builder.Services.AddSingleton<VirtualReaderWidgetService>();
        builder.Services.AddSingleton<LlrpReaderManager.Services.IAppWindowService, LlrpReaderManager.Services.AppWindowService>();
        builder.Logging.AddConsole();

        return builder.Build();
    }
}
