using Android.App;
using Android.Content.PM;
using Android.OS;
using AndroidX.Activity;
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
    private AlertDialog? exitDialog;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Handle app-level back after the WebView has returned to its root page.
        // This prevents subsequent back gestures from walking through browser
        // history and makes them an explicit app-exit action.
        OnBackPressedDispatcher.AddCallback(new ExitOnBackPressedCallback(this));

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

    private void ShowExitConfirmation()
    {
        if (IsFinishing || exitDialog?.IsShowing == true)
        {
            return;
        }

        var builder = new AlertDialog.Builder(this);
        builder.SetTitle("退出应用");
        builder.SetMessage("确定要退出 LLRP Reader Manager 吗？");
        builder.SetNegativeButton("取消", (_, _) => { });
        builder.SetPositiveButton("退出", (_, _) => FinishAndRemoveTask());

        AlertDialog dialog = builder.Create()!;
        exitDialog = dialog;
        dialog.Show();
    }

    private sealed class ExitOnBackPressedCallback : OnBackPressedCallback
    {
        private readonly MainActivity activity;

        public ExitOnBackPressedCallback(MainActivity activity)
            : base(true)
        {
            this.activity = activity;
        }

        public override void HandleOnBackPressed()
        {
            activity.ShowExitConfirmation();
        }
    }
}
