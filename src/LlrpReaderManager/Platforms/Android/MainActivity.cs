using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Core.View;

namespace LlrpReaderManager;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Icon = "@mipmap/appicon",
    RoundIcon = "@mipmap/appicon_round",
    ConfigurationChanges = ConfigChanges.ScreenSize
        | ConfigChanges.Orientation
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        if (Window is not null)
        {
            // Ensure content does not draw underneath the system status bar
            WindowCompat.SetDecorFitsSystemWindows(Window, true);

            var darkNavy = Android.Graphics.Color.ParseColor("#0F172A");

#pragma warning disable CA1422
            Window.SetStatusBarColor(darkNavy);
            Window.SetNavigationBarColor(darkNavy);
#pragma warning restore CA1422

            var controller = WindowCompat.GetInsetsController(Window, Window.DecorView);
            if (controller is not null)
            {
                // Dark background => Light status bar and navigation bar icons
                controller.AppearanceLightStatusBars = false;
                controller.AppearanceLightNavigationBars = false;
            }
        }
    }
}
