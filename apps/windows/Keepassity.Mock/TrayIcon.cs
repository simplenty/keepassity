// SPDX-License-Identifier: GPL-3.0-or-later
using System.Drawing;
using System.Windows.Forms;

namespace Keepassity.Mock;

/// <summary>Notification-area icon with Show / Lock / Exit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly ContextMenuStrip _menu;
    private readonly NotifyIcon _icon;
    private readonly Icon? _ownedIcon;
    private bool _disposed;

    public TrayIcon(string toolTip, Action show, Action lockAction, Action exit)
    {
        _ownedIcon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Show keepassity", null, (_, _) => Invoke(show));
        _menu.Items.Add("Lock", null, (_, _) => Invoke(lockAction));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit keepassity", null, (_, _) => Invoke(exit));
        _icon = new NotifyIcon
        {
            Icon = _ownedIcon ?? SystemIcons.Application,
            Text = toolTip,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Invoke(show);
    }

    public bool Available => !_disposed && _icon.Visible;

    public Microsoft.UI.Xaml.UIElement? Element => null;

    public string? LastError { get; }

    public void ShowInfo(string title, string message)
    {
        if (_disposed)
        {
            return;
        }
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = message;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch
        {
            // Best-effort cleanup.
        }
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
