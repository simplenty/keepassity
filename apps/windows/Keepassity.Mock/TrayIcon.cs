// SPDX-License-Identifier: GPL-3.0-or-later
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Keepassity.Mock;

/// <summary>WinUI notification-area icon with Show / Lock / Exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyout _menu;
    private bool _disposed;

    public TrayIcon(string toolTip, ElementTheme theme, Action show, Action lockAction,
        Action exit)
    {
        var showItem = new MenuFlyoutItem
        {
            Text = "Show keepassity",
            Icon = new FontIcon { Glyph = "\uE8A7", FontSize = 12 },
        };
        showItem.Click += (_, _) => Invoke(show);

        var lockItem = new MenuFlyoutItem
        {
            Text = "Lock",
            Icon = new FontIcon { Glyph = "\uE72E", FontSize = 12 },
        };
        lockItem.Click += (_, _) => Invoke(lockAction);

        var exitItem = new MenuFlyoutItem
        {
            Text = "Exit keepassity",
            Icon = new FontIcon { Glyph = "\uE7E8", FontSize = 12 },
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        exitItem.Click += (_, _) => Invoke(exit);

        _menu = new MenuFlyout();
        _menu.Items.Add(showItem);
        _menu.Items.Add(lockItem);
        _menu.Items.Add(new MenuFlyoutSeparator());
        _menu.Items.Add(exitItem);

        _icon = new TaskbarIcon
        {
            ToolTipText = toolTip,
            ContextFlyout = _menu,
            ContextMenuMode = ContextMenuMode.SecondWindow,
            RequestedTheme = theme,
            NoLeftClickDelay = true,
            IconSource = new GeneratedIconSource
            {
                Text = "K",
                FontSize = 34,
                FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                Background = new SolidColorBrush(Color.FromArgb(255, 0, 120, 212)),
            },
            LeftClickCommand = new TrayCommand(show),
            DoubleClickCommand = new TrayCommand(show),
        };
        _icon.ForceCreate();
    }

    public bool Available => !_disposed && _icon.IsCreated;

    public void ApplyTheme(ElementTheme theme)
    {
        _icon.RequestedTheme = theme;
    }

    public UIElement? Element => _icon;

    public string? LastError { get; }

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
            _icon.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
