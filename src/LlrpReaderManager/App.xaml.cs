namespace LlrpReaderManager;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage())
        {
            Title = "LLRP Reader Manager"
        };

#if WINDOWS
        window.Created += (s, e) =>
        {
            var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
            if (nativeWindow is not null)
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "AppIcon", "appicon.ico");
                if (System.IO.File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }
            }
        };
#endif

        return window;
    }
}
