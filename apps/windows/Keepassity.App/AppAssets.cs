// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.App;

/// <summary>
/// Absolute paths to the runtime artwork copied next to the executable by the
/// build (see Keepassity.App.csproj). The Windows App SDK requires a real file
/// path for AppWindow.SetIcon(string); the tray icon and the About page load the
/// same assets from disk.
/// </summary>
internal static class AppAssets
{
    /// <summary>Multi-size .ico used for the window and notification-area icon.</summary>
    public static string AppIconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");

    /// <summary>256px PNG preview shown in the About section.</summary>
    public static string AppIconPreviewPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "app-icon-256.png");
}
