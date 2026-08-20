namespace LlrpReaderManager.Services;

public sealed class AppWindowService : IAppWindowService
{
    public void ResizeToMobile(int contentWidth, int contentHeight)
    {
#if WINDOWS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Application.Current?.Windows.FirstOrDefault() is { } window)
                {
                    int targetWidth = contentWidth + 48;
                    int targetHeight = Math.Min(980, contentHeight + 110);
                    window.Width = targetWidth;
                    window.Height = targetHeight;

                    var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                    if (nativeWindow is not null)
                    {
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                        appWindow?.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                    }
                }
            }
            catch
            {
                // Graceful fallback if window management is constrained
            }
        });
#endif
    }

    public void RestoreToDesktop()
    {
#if WINDOWS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Application.Current?.Windows.FirstOrDefault() is { } window)
                {
                    int targetWidth = 1280;
                    int targetHeight = 860;
                    window.Width = targetWidth;
                    window.Height = targetHeight;

                    var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                    if (nativeWindow is not null)
                    {
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
                        appWindow?.Resize(new Windows.Graphics.SizeInt32(targetWidth, targetHeight));
                    }
                }
            }
            catch
            {
                // Graceful fallback
            }
        });
#endif
    }
}
