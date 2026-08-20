namespace LlrpReaderManager.Services;

public sealed class AppWindowService : IAppWindowService
{
#if WINDOWS
    private int savedX = -1;
    private int savedY = -1;
    private int savedWidth = 1280;
    private int savedHeight = 860;
    private bool savedWasMaximized;
    private bool hasSavedDesktopBounds;
#endif

    public void EnterHandheldSimulator(int targetClientWidth, int targetClientHeight, int controlBarHeight = 36)
    {
#if WINDOWS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Application.Current?.Windows.FirstOrDefault() is { } window)
                {
                    var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                    if (nativeWindow is not null)
                    {
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

                        if (appWindow is not null)
                        {
                            // 1. Save previous desktop state before switching to simulator mode
                            if (!hasSavedDesktopBounds)
                            {
                                var currentPos = appWindow.Position;
                                var currentSize = appWindow.Size;
                                savedX = currentPos.X;
                                savedY = currentPos.Y;
                                savedWidth = currentSize.Width;
                                savedHeight = currentSize.Height;
                                savedWasMaximized = (appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
                                hasSavedDesktopBounds = true;
                            }

                            // If maximized, restore to non-maximized first
                            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                            {
                                if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                                {
                                    presenter.Restore();
                                }
                            }

                            // 2. Query work area of current display
                            var displayArea = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
                            var workArea = displayArea.WorkArea;

                            int totalTargetHeight = targetClientHeight + controlBarHeight;
                            int totalTargetWidth = targetClientWidth;

                            // If display work area height is smaller than desired window height, fit within work area
                            int finalHeight = Math.Min(totalTargetHeight, workArea.Height - 40);
                            int finalWidth = Math.Min(totalTargetWidth, workArea.Width - 40);

                            // Center window horizontally and position near top with margin
                            int posX = Math.Max(workArea.X, workArea.X + (workArea.Width - finalWidth) / 2);
                            int posY = Math.Max(workArea.Y, workArea.Y + (workArea.Height - finalHeight) / 2);

                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(posX, posY, finalWidth, finalHeight));
                        }
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

    public void RestoreDesktop()
    {
#if WINDOWS
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Application.Current?.Windows.FirstOrDefault() is { } window)
                {
                    var nativeWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                    if (nativeWindow is not null)
                    {
                        var handle = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                        var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
                        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);

                        if (appWindow is not null)
                        {
                            int targetX = savedX > 0 ? savedX : 100;
                            int targetY = savedY > 0 ? savedY : 100;
                            int targetWidth = savedWidth >= 800 ? savedWidth : 1280;
                            int targetHeight = savedHeight >= 600 ? savedHeight : 860;

                            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(targetX, targetY, targetWidth, targetHeight));

                            if (savedWasMaximized && appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                            {
                                presenter.Maximize();
                            }

                            hasSavedDesktopBounds = false;
                        }
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

    public void ExitApplication()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
#if WINDOWS
                Application.Current?.Quit();
                Environment.Exit(0);
#elif ANDROID
                Android.OS.Process.KillProcess(Android.OS.Process.MyPid());
#else
                Environment.Exit(0);
#endif
            }
            catch
            {
                Environment.Exit(0);
            }
        });
    }
}
