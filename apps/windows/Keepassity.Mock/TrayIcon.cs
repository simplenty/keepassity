// SPDX-License-Identifier: GPL-3.0-or-later
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.UI;

namespace Keepassity.Mock;

/// <summary>WinUI notification-area icon with a WinUI tray menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly Action _show;
    private readonly Action _lock;
    private readonly Action _exit;
    private Window? _menuWindow;
    private ElementTheme _theme;
    private bool _disposed;

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
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private enum TaskbarEdge
    {
        Top,
        Bottom,
        Left,
        Right,
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType,
        out uint dpiX, out uint dpiY);

    public TrayIcon(string toolTip, ElementTheme theme, Action show, Action lockAction,
        Action exit)
    {
        _theme = theme;
        _show = show;
        _lock = lockAction;
        _exit = exit;
        _icon = new TaskbarIcon
        {
            ToolTipText = toolTip,
            MenuActivation = PopupActivationMode.None,
            RequestedTheme = theme,
            NoLeftClickDelay = true,
            LeftClickCommand = new TrayCommand(show),
            DoubleClickCommand = new TrayCommand(show),
            RightClickCommand = new TrayCommand(ShowMenuAtCursor),
            IconSource = new GeneratedIconSource
            {
                Text = "K",
                FontSize = 34,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            },
        };
        _icon.ForceCreate();
    }

    public bool Available => !_disposed && _icon.IsCreated;

    public UIElement? Element => _icon;

    public string? LastError { get; }

    public void ApplyTheme(ElementTheme theme)
    {
        _theme = theme;
        _icon.RequestedTheme = theme;
        if (_menuWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme;
        }
    }

    private void ShowMenuAtCursor()
    {
        if (_disposed || _icon.DispatcherQueue is null)
        {
            return;
        }
        _icon.DispatcherQueue.TryEnqueue(ShowMenuOnUiThread);
    }

    private void ShowMenuOnUiThread()
    {
        if (_disposed || !GetCursorPos(out NativePoint point))
        {
            return;
        }
        EnsureMenuWindow();
        if (_menuWindow is null)
        {
            return;
        }

        IntPtr monitor = MonitorFromPoint(point, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        bool hasWorkArea = monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info);
        double scale = GetScale(monitor);
        int width = (int)Math.Round(240 * scale);
        int height = (int)Math.Round(156 * scale);
        TaskbarEdge edge = hasWorkArea ? GetTaskbarEdge(info) : TaskbarEdge.Bottom;

        int x;
        int y;
        switch (edge)
        {
            case TaskbarEdge.Top:
                x = point.X + 8;
                y = point.Y + 8;
                break;
            case TaskbarEdge.Left:
                x = point.X + 8;
                y = point.Y;
                break;
            case TaskbarEdge.Right:
                x = point.X - width - 8;
                y = point.Y;
                break;
            default:
                x = point.X + 8;
                y = point.Y - height - 8;
                break;
        }
        if (hasWorkArea)
        {
            x = Math.Clamp(x, info.Work.Left, Math.Max(info.Work.Left, info.Work.Right - width));
            y = Math.Clamp(y, info.Work.Top, Math.Max(info.Work.Top, info.Work.Bottom - height));
        }

        _menuWindow.AppWindow.Move(new PointInt32(x, y));
        _menuWindow.AppWindow.Resize(new SizeInt32(width, height));
        _menuWindow.Activate();
    }

    private void EnsureMenuWindow()
    {
        if (_menuWindow is not null)
        {
            return;
        }

        var items = new StackPanel { Spacing = 2 };
        AddMenuItem(items, "Show keepassity", "\uE8A7", _show);
        AddMenuItem(items, "Lock", "\uE72E", _lock);
        items.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(8, 4, 8, 4),
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });
        AddMenuItem(items, "Exit keepassity", "\uE7E8", _exit, danger: true);

        var root = new Border
        {
            RequestedTheme = _theme,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6),
            Child = items,
        };
        _menuWindow = new Window
        {
            Content = root,
            ExtendsContentIntoTitleBar = true,
        };
        OverlappedPresenter presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsResizable = false;
        _menuWindow.AppWindow.SetPresenter(presenter);
        _menuWindow.AppWindow.IsShownInSwitchers = false;
        _menuWindow.AppWindow.Resize(new SizeInt32(240, 156));
        _menuWindow.Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
            {
                _menuWindow?.AppWindow.Hide();
            }
        };
    }

    private void AddMenuItem(StackPanel panel, string text, string glyph, Action action,
        bool danger = false)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        content.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
        };
        if (danger)
        {
            button.Foreground =
                (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
        }
        button.Click += (_, _) =>
        {
            _menuWindow?.AppWindow.Hide();
            Invoke(action);
        };
        panel.Children.Add(button);
    }

    private static double GetScale(IntPtr monitor)
    {
        if (monitor != IntPtr.Zero &&
            GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 &&
            dpiX > 0)
        {
            return dpiX / 96.0;
        }
        return 1.0;
    }

    private static TaskbarEdge GetTaskbarEdge(MonitorInfo info)
    {
        if (info.Work.Top > info.Monitor.Top)
        {
            return TaskbarEdge.Top;
        }
        if (info.Work.Bottom < info.Monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }
        if (info.Work.Left > info.Monitor.Left)
        {
            return TaskbarEdge.Left;
        }
        if (info.Work.Right < info.Monitor.Right)
        {
            return TaskbarEdge.Right;
        }
        // Auto-hidden taskbar: default to the usual bottom edge. The menu is
        // clamped to the work area afterwards.
        return TaskbarEdge.Bottom;
    }

    public void ShowInfo(string title, string message)
    {
        if (_disposed)
        {
            return;
        }
        _icon.ShowNotification(title, message);
    }

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
        _disposed = true;
        try
        {
            _menuWindow?.Close();
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
    }
}
