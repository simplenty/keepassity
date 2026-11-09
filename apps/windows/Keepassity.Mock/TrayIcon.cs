// SPDX-License-Identifier: GPL-3.0-or-later
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;
using WinRT.Interop;

namespace Keepassity.Mock;

/// <summary>
/// WinUI notification-area icon with a native WinUI menu surface.
///
/// There is no first-party tray-icon API in WinUI 3; the supported approach is
/// Shell_NotifyIcon (provides the icon and the WM_CONTEXTMENU callback) plus a
/// WinUI <see cref="MenuFlyout"/> for the menu. A flyout needs a XamlRoot, so a
/// small invisible host window is moved to the notification icon before showing
/// the menu. This is the same pattern the WinUIEx TrayIcon uses: the host is
/// positioned with Shell_NotifyIconGetRect, the flyout is shown with
/// Position=(0,0) and an ExclusionRect covering the icon, and the host takes the
/// foreground so the menu is above the taskbar and the overflow flyout.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int GwlExStyle = -20;
    private const nint WsExTransparent = 0x00000020;
    private const nint WsExToolWindow = 0x00000080;
    private const nint WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    private readonly TaskbarIcon _icon;
    private readonly MenuFlyout _menu;
    private readonly MenuFlyoutItem _showItem;
    private readonly MenuFlyoutItem _lockItem;
    private readonly MenuFlyoutItem _exitItem;
    private System.Drawing.Icon? _ownedIcon;
    private Window? _anchorWindow;
    private Border? _anchor;
    private ElementTheme _theme;
    private IntPtr _previousForeground;
    private bool _actionInvoked;
    private bool _anchorLoaded;
    private bool _disposed;
    private bool _unlocked;
    private string? _databaseName;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey,
        byte alpha, uint flags);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier,
        out NativeRect iconLocation);

    public TrayIcon(string toolTip, ElementTheme theme, string? iconPath, Action show,
        Action lockAction, Action exit)
    {
        _theme = theme;

        var showItem = new MenuFlyoutItem
        {
            Text = Loc.T("Show keepassity"),
            Icon = CreateIcon("\uE8A7"),
        };
        _showItem = showItem;
        showItem.Click += (_, _) =>
        {
            _actionInvoked = true;
            Invoke(show);
        };

        _lockItem = new MenuFlyoutItem
        {
            Text = Loc.T("Lock"),
            Icon = CreateIcon("\uE72E"),
            IsEnabled = false,
        };
        _lockItem.Click += (_, _) =>
        {
            _actionInvoked = true;
            Invoke(lockAction);
        };

        var exitItem = new MenuFlyoutItem
        {
            Text = Loc.T("Exit keepassity"),
            Icon = CreateIcon("\uE7E8"),
        };
        _exitItem = exitItem;
        exitItem.Click += (_, _) =>
        {
            _actionInvoked = true;
            Invoke(exit);
        };

        _menu = new MenuFlyout
        {
            Items =
            {
                showItem,
                _lockItem,
                new MenuFlyoutSeparator(),
                exitItem,
            },
        };
        _menu.Opened += OnMenuOpened;
        _menu.Closed += (_, _) => OnMenuClosed();
        _menu.ShouldConstrainToRootBounds = false;

        _icon = new TaskbarIcon
        {
            ToolTipText = toolTip,
            MenuActivation = PopupActivationMode.None,
            RequestedTheme = theme,
            NoLeftClickDelay = true,
            LeftClickCommand = new TrayCommand(show),
            DoubleClickCommand = new TrayCommand(show),
            RightClickCommand = new TrayCommand(OpenMenuAtIcon),
        };
        // Prefer the real application artwork. H.NotifyIcon accepts a
        // System.Drawing.Icon directly for the notification-area icon; the
        // framework keeps using the icon for the lifetime of the tray icon, so
        // the instance is owned and disposed here.
        if (!TrySetApplicationIcon(iconPath))
        {
            _icon.IconSource = new GeneratedIconSource
            {
                Text = "K",
                FontSize = 34,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            };
        }
        _icon.ForceCreate();
    }

    private bool TrySetApplicationIcon(string? iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath) || !File.Exists(iconPath))
        {
            AppLog.Warn("tray icon: app.ico asset is missing; using the generated fallback");
            return false;
        }
        try
        {
            _ownedIcon = new System.Drawing.Icon(iconPath);
            _icon.Icon = _ownedIcon;
            AppLog.Info("tray icon: loaded Assets/app.ico");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("tray icon: app.ico could not be loaded", ex);
            return false;
        }
    }

    public bool Available => !_disposed && _icon.IsCreated;

    public UIElement? Element => _icon;

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        _icon.RequestedTheme = theme;
        if (_anchorWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    /// <summary>
    /// Re-renders the menu and tooltip after the shell language changed. The flyout
    /// items are reused (not recreated) so an open menu and the click handlers stay
    /// valid; only their text is replaced.
    /// </summary>
    public void ApplyLanguage()
    {
        if (_disposed)
        {
            return;
        }
        _showItem.Text = Loc.T("Show keepassity");
        _lockItem.Text = Loc.T("Lock");
        _exitItem.Text = Loc.T("Exit keepassity");
        UpdateToolTip();
    }

    /// <summary>
    /// Reflects the vault state in the menu and the tray tooltip. The Lock command
    /// is only meaningful while a database is open.
    /// </summary>
    public void SetVaultState(bool unlocked, string? databaseName)
    {
        if (_disposed)
        {
            return;
        }
        _lockItem.IsEnabled = unlocked;
        _unlocked = unlocked;
        _databaseName = databaseName;
        UpdateToolTip();
    }

    private void UpdateToolTip()
    {
        if (_disposed)
        {
            return;
        }
        string state = Loc.T(_unlocked ? "unlocked" : "locked");
        _icon.ToolTipText = string.IsNullOrWhiteSpace(_databaseName)
            ? Loc.T("keepassity - {0}", state)
            : Loc.T("keepassity - {0} ({1})", _databaseName, state);
    }

    /// <summary>
    /// Test hook: opens the context menu at a physical screen position so an
    /// automated capture can inspect the rendered menu without pointer input.
    /// </summary>
    public void ShowMenuAt(int x, int y)
    {
        if (_disposed)
        {
            return;
        }
        ShowMenu(x, y, 0, 0);
    }

    /// <summary>
    /// Test hook: opens the context menu anchored to the notification icon (the
    /// same path a real right-click uses) instead of at explicit coordinates.
    /// </summary>
    public void ShowMenuAtIconForTest()
    {
        if (_disposed)
        {
            return;
        }
        OpenMenuAtIcon();
    }

    public void ShowInfo(string title, string message)
    {
        if (_disposed)
        {
            return;
        }
        _icon.ShowNotification(title, message);
    }

    private void OpenMenuAtIcon()
    {
        if (_disposed)
        {
            return;
        }
        if (TryGetIconRect(out int x, out int y, out int width, out int height))
        {
            ShowMenu(x, y, width, height);
            return;
        }
        if (GetCursorPos(out NativePoint point))
        {
            ShowMenu(point.X, point.Y, 0, 0);
        }
    }

    private bool TryGetIconRect(out int x, out int y, out int width, out int height)
    {
        x = y = width = height = 0;
        try
        {
            H.NotifyIcon.Core.TrayIcon tray = _icon.TrayIcon;
            if (tray is null)
            {
                return false;
            }
            NotifyIconIdentifier identifier = new()
            {
                Size = (uint)Marshal.SizeOf<NotifyIconIdentifier>(),
                Window = (IntPtr)tray.WindowHandle,
                Id = 0,
                Guid = tray.Id,
            };
            int hresult = Shell_NotifyIconGetRect(ref identifier, out NativeRect rect);
            if (hresult < 0 || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            {
                return false;
            }
            x = rect.Left;
            y = rect.Top;
            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ShowMenu(int x, int y, int iconWidth, int iconHeight)
    {
        if (_menu.IsOpen)
        {
            CloseMenu();
            return;
        }
        EnsureAnchorWindow();
        if (_anchorWindow is null || _anchor is null)
        {
            return;
        }
        try
        {
            _previousForeground = GetForegroundWindow();
            _actionInvoked = false;
            _iconWidth = iconWidth;
            _iconHeight = iconHeight;
            _anchorX = x;
            _anchorY = y;
            _anchorWindow.AppWindow.MoveAndResize(
                new Windows.Graphics.RectInt32(x, y, 1, 1));
            if (!_anchorLoaded || _anchor.XamlRoot is null)
            {
                _anchor.Loaded -= OnAnchorLoaded;
                _anchor.Loaded += OnAnchorLoaded;
                _anchorWindow.AppWindow.Show();
                return;
            }
            PresentMenu();
        }
        catch (Exception ex)
        {
            AppLog.Error("tray menu failed to open", ex);
        }
    }

    private int _iconWidth;
    private int _iconHeight;
    private int _anchorX;
    private int _anchorY;

    private void OnAnchorLoaded(object sender, RoutedEventArgs e)
    {
        _anchor!.Loaded -= OnAnchorLoaded;
        _anchorLoaded = true;
        PresentMenu();
    }

    private void PresentMenu()
    {
        if (_disposed || _anchorWindow is null || _anchor is null || _menu.IsOpen)
        {
            return;
        }
        nint handle = WindowNative.GetWindowHandle(_anchorWindow);
        double scale = _anchor.XamlRoot?.RasterizationScale ?? 1.0;
        _anchorWindow.Activate();
        _anchorWindow.AppWindow.Show();
        _ = SetForegroundWindow(handle);
        try
        {
            _menu.ShowAt(_anchor, new FlyoutShowOptions
            {
                Position = new Windows.Foundation.Point(0, 0),
                Placement = ChoosePlacement(),
                ExclusionRect = new Windows.Foundation.Rect(
                    0, 0, _iconWidth / scale, _iconHeight / scale),
            });
            AppLog.Info("tray menu shown");
        }
        catch (Exception ex)
        {
            AppLog.Error("tray menu failed to open", ex);
        }
    }

    /// <summary>
    /// Picks the side of the anchor with more room. Menus anchored near the top
    /// edge (top taskbar) open down and to the right; menus near the bottom edge
    /// open up. Near the right edge the menu is right-aligned to stay on screen.
    /// </summary>
    private FlyoutPlacementMode ChoosePlacement()
    {
        DisplayArea area = DisplayArea.GetFromPoint(
            new Windows.Graphics.PointInt32(_anchorX, _anchorY),
            DisplayAreaFallback.Nearest);
        Windows.Graphics.RectInt32 work = area.WorkArea;
        TrayMenuPlacement.Result placement = TrayMenuPlacement.Choose(
            _anchorX, _anchorY, _iconWidth, _iconHeight,
            work.Y, work.X + work.Width, work.Y + work.Height);
        bool below = placement.Side == TrayMenuPlacement.VerticalSide.Below;
        return below
            ? (placement.RightAligned
                ? FlyoutPlacementMode.BottomEdgeAlignedRight
                : FlyoutPlacementMode.BottomEdgeAlignedLeft)
            : (placement.RightAligned
                ? FlyoutPlacementMode.TopEdgeAlignedRight
                : FlyoutPlacementMode.TopEdgeAlignedLeft);
    }

    private void OnMenuOpened(object? sender, object e)
    {
        // The flyout presenter is a separate visual root and does not inherit the
        // app theme from the anchor element, so pin the theme explicitly.
        if (sender is FrameworkElement presenter)
        {
            presenter.RequestedTheme = _theme;
        }
        AppLog.Info("tray menu opened");
    }

    private void CloseMenu()
    {
        try
        {
            _menu.Hide();
        }
        catch (Exception ex)
        {
            AppLog.Error("tray menu failed to close", ex);
        }
    }

    private void OnMenuClosed()
    {
        AppLog.Info("tray menu closed");
        try
        {
            _anchorWindow?.AppWindow.Hide();
        }
        catch
        {
            // Best-effort cleanup.
        }
        RestorePreviousForeground();
    }

    private void RestorePreviousForeground()
    {
        IntPtr previous = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (previous == IntPtr.Zero || _actionInvoked || _disposed)
        {
            return;
        }
        IntPtr foreground = GetForegroundWindow();
        _ = GetWindowThreadProcessId(foreground, out uint foregroundProcess);
        if (foregroundProcess != (uint)Environment.ProcessId)
        {
            // The user dismissed the menu by activating another application;
            // leave that application in front.
            return;
        }
        _ = SetForegroundWindow(previous);
    }

    private void EnsureAnchorWindow()
    {
        if (_anchorWindow is not null)
        {
            return;
        }

        _anchor = new Border
        {
            Width = 1,
            Height = 1,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var root = new Grid
        {
            RequestedTheme = _theme,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        root.Children.Add(_anchor);

        var window = new Window { Content = root };
        nint handle = WindowNative.GetWindowHandle(window);
        Microsoft.UI.WindowId windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(handle);
        AppWindow appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.IsShownInSwitchers = false;
        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }
        MakeTransparent(handle);
        window.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated && _menu.IsOpen)
            {
                CloseMenu();
            }
        };
        appWindow.Hide();
        _anchorWindow = window;
        AppLog.Info("tray menu anchor window ready");
    }

    private static void MakeTransparent(nint handle)
    {
        nint style = GetWindowLongPtr(handle, GwlExStyle);
        style |= WsExToolWindow | WsExTransparent | WsExLayered;
        _ = SetWindowLongPtr(handle, GwlExStyle, style);
        _ = SetLayeredWindowAttributes(handle, 0, 0, LwaAlpha);
    }

    private static FontIcon CreateIcon(string glyph) => new() { Glyph = glyph, FontSize = 14 };

    private static void Invoke(Action action)
    {
        try
        {
            action();
        }
        catch
        {
            // A tray callback must not take down the application.
        }
    }

    private sealed class TrayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _action;

        public TrayCommand(Action action) => _action = action;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Invoke(_action);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (_menu.IsOpen)
        {
            try
            {
                _menu.Hide();
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
        _disposed = true;
        try
        {
            _anchorWindow?.Close();
        }
        catch
        {
            // Best-effort cleanup.
        }
        try
        {
            _icon.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
        try
        {
            _ownedIcon?.Dispose();
            _ownedIcon = null;
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
