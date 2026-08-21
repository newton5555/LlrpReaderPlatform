using Microsoft.Maui.Platforms.Linux.Gtk4.Platform;
using System.Runtime.Versioning;

namespace LlrpReaderManager;

[SupportedOSPlatform("linux")]
public sealed class Program : GtkMauiApplication
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();

    public static void Main(string[] args)
    {
        Program application = new();
        application.Run(args);
    }
}
