// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.System;
using Windows.UI;
using Windows.UI.ViewManagement;
using Keepassity.ViewModels;

namespace Keepassity.App;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<GroupNavItem> Groups { get; } = new();
    public ObservableCollection<EntryRowPresenter> Entries { get; } = new();

    /// <summary>State and display strings for the unlock surface (MVVM boundary).</summary>
    public UnlockViewModel Unlock { get; }

    /// <summary>State and display strings for the detail panel (MVVM boundary).</summary>
    public EntryDetailViewModel Detail { get; }

    /// <summary>TOTP display state machine (MVVM boundary).</summary>
    public TotpDisplayViewModel Totp { get; }

    /// <summary>Localized sync status shown in the navigation pane.</summary>
    public SyncStatusViewModel SyncStatus { get; }

    private readonly CoreClient _core = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly UISettings _uiSettings = new();
    private readonly SyncStateStore _syncStates = new();
    private readonly CredentialStore _credentials = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    // The sync protocol (decision, back-off, verification, conflict rules and
    // baseline ordering) lives in Core; the window supplies the open vault, the
    // WebDAV client and the conflict dialog.
    private readonly SyncCoordinator _sync;
    // A WebDAV download is held here until the master password opens its bytes;
    // only then is the working copy replaced (see RemoteVaultOpener).
    private readonly RemoteVaultOpener _remoteOpener;
    private PendingRemoteDatabase? _pendingRemoteDownload;
    private readonly DispatcherTimer _totpTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _autoLockTimer = new();
    private readonly DispatcherTimer _clipboardTimer = new();
    private readonly List<GroupDto> _groupDtos = new();
    private readonly List<GroupDto> _selectableGroups = new();
    private readonly Dictionary<string, string> _groupNames = new();

    private string? _dbPath;
    private bool _webDavSource;
    private string? _sourceWebDavUrl;
    private string? _selectedGroupUuid;
    private string? _recycleBinGroupUuid;
    private string? _rootGroupUuid;
    private EntryFullDto? _currentFull;
    private EntryRowPresenter? _currentSummary;
    private string? _lastClipboardText;
    private string? _masterPassword;
    private readonly VaultSession _vault = new();
    private TrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _searchCts;
    private bool _passwordVisible;
    private bool _busy;
    private bool _suppressNav;
    private int _showGeneration;
    private EntrySort _entrySort = EntrySort.Title;
    private bool _onlyTotp;
    private string _lastEntryFingerprint = string.Empty;
    private Grid? _rightPanel;
    private TextBlock? _rightPanelTitle;
    private ContentControl? _rightPanelContent;
    private StackPanel? _rightPanelActions;
    private TaskCompletionSource<RightPanelResult>? _rightPanelCompletion;
    private bool _rightPanelOpen;
    private Grid? _detailPanel;
    private TextBlock? _detailPanelTitle;
    private ContentControl? _detailPanelContent;
    private StackPanel? _detailPanelActions;
    private TaskCompletionSource<RightPanelResult>? _detailPanelCompletion;
    private bool _detailPanelOpen;
    private Action? _generatorReturn;
    private readonly RecentDatabaseStore _recentDatabases = new();
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _trayMenuDemoTimers =
        new();
    private SessionLockMonitor? _sessionLockMonitor;
    private SingleInstanceRelay? _singleInstanceRelay;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _networkReplayTimer;
    private bool _sessionLockPending;
    private bool _announcePeerWarningLogged;
    // Drag-move state: which row is being dragged and which group it already
    // lives in, so a drop onto that same group is refused with a hint instead of
    // ejecting the row from the list and putting it straight back.
    private string? _draggedEntryGroupUuid;
    private string? _draggedEntryTitle;
    private bool _dragSameGroupHintShown;
    // Navigation pane width animation: the pane is resized in layout so the
    // workspace columns reflow on every frame instead of jumping.
    private double _paneOpenLength;
    private bool _paneOpen = true;
    private bool _paneAnimating;
    private bool _paneAnimationFrameHooked;
    private double _paneAnimationFrom;
    private double _paneAnimationTo;
    private int _paneAnimationMilliseconds;
    private TimeSpan? _paneAnimationStartedAt;

    private enum RightPanelResult
    {
        Primary,
        Secondary,
        Close,
    }

    private AppSettings Settings => _settingsStore.Current;

    public MainWindow()
    {
        // Resolve the saved UI language before InitializeComponent so the XAML
        // attached localization properties apply the right strings on first load.
        Loc.Initialize(Settings.Language);
        _remoteOpener = new RemoteVaultOpener(_syncStates);
        Unlock = new UnlockViewModel(_core, LocText.Instance, _remoteOpener);
        SyncStatus = new SyncStatusViewModel(LocText.Instance);
        Detail = new EntryDetailViewModel();
        Detail.Clear(Loc.T("Select an entry"));
        Totp = new TotpDisplayViewModel(new CoreTotpSource(_core),
            () => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _sync = new SyncCoordinator(_syncStates)
        {
            ConflictChoice = ShowConflictDialogAsync,
        };
        InitializeComponent();
        ApplyThemePreference(Settings.Theme);
        Title = "Keepassity";
        // The shell/taskbar icon is not inherited from the executable by WinUI 3;
        // the window icon must be set explicitly (AppWindow.SetIcon needs a real
        // .ico path for unpackaged apps).
        ApplyWindowIcon();
        RestoreWindowBounds();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) =>
        {
            ApplyTitleBarTheme();
            _trayIcon?.ApplyTheme(Root.ActualTheme);
        };
        SearchBox.Visibility = Visibility.Collapsed;
        EntryList.ItemsSource = Entries;
        ParseArguments();
        if (_dbPath is null &&
            !string.IsNullOrWhiteSpace(Settings.LastDatabasePath) &&
            File.Exists(Settings.LastDatabasePath))
        {
            _dbPath = Settings.LastDatabasePath;
            if (string.Equals(Settings.LastDatabaseKind, "webdav",
                    StringComparison.OrdinalIgnoreCase))
            {
                _webDavSource = true;
                _sourceWebDavUrl = Settings.LastWebDavUrl;
            }
        }
        UpdateDatabasePath();
        Root.Language = Loc.IsChinese ? "zh-CN" : "en-US";
        InitializeLanguageUi();

        // Security: lock the vault on Windows session lock, logoff, session end
        // and system suspend (see SessionLockMonitor). The network monitor
        // replays pending WebDAV edits when connectivity comes back.
        InitializeSessionLockMonitor();
        InitializeNetworkReplay();
        InitializeSingleInstanceRelay();

        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += async (_, _) => await TickTotpAsync();
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.IsOpen = false;
        };
        _autoLockTimer.Interval = TimeSpan.FromSeconds(10);
        _autoLockTimer.Tick += async (_, _) => await AutoLockTimer_Tick();
        _clipboardTimer.Interval = TimeSpan.FromSeconds(30);
        _clipboardTimer.Tick += async (_, _) => await ClearClipboardTimer_Tick();
        Root.AddHandler(UIElement.PointerMovedEvent,
            new PointerEventHandler((_, _) => TouchActivity()), true);
        Root.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, _) => TouchActivity()), true);
        Root.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler((_, _) => TouchActivity()), true);
        Root.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(RightPanel_KeyDown), true);
        RegisterKeyboardAccelerators();
        Root.Loaded += Root_Loaded;
        Closed += (_, _) =>
        {
            SaveWindowBounds();
            _totpTimer.Stop();
            _sessionLockMonitor?.Dispose();
            _sessionLockMonitor = null;
            _singleInstanceRelay?.Dispose();
            _singleInstanceRelay = null;
            _vault.Dispose();
            _networkReplayTimer?.Stop();
            NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
            _trayIcon?.Dispose();
            _trayIcon = null;
            _core.Dispose();
        };
    }

    private void InitializeTray()
    {
        if (TestHooks.HasSwitch("--no-tray"))
        {
            return;
        }
        try
        {
            _trayIcon = new TrayIcon("Keepassity", Root.ActualTheme, AppAssets.AppIconPath,
                ShowFromTray, LockFromTray, ExitFromTray);
            _trayIcon.SetVaultState(_core.IsOpen, DisplayVaultName());
            if (_trayIcon.Element is UIElement trayElement &&
                !Root.Children.Contains(trayElement))
            {
                Root.Children.Add(trayElement);
            }
            AppLog.Info($"tray icon available: {_trayIcon.Available}");
            AppWindow.Closing += AppWindow_Closing;
        }
        catch (Exception ex)
        {
            AppLog.Error("tray icon initialization failed", ex);
            _trayIcon?.Dispose();
            _trayIcon = null;
        }
    }

    private void ShowFromTray()
    {
        AppWindow.Show();
        Activate();
    }

    private void LockFromTray()
    {
        if (!_core.IsOpen)
        {
            return;
        }
        if (_busy)
        {
            _trayIcon?.ShowInfo("Keepassity",
                Loc.T("Busy saving or syncing; the vault was not locked. Try again in a moment."));
            return;
        }
        // Lock in place: a tray lock must not raise the window (and with it the
        // decrypted workspace) just to lock. If the window is visible it stays
        // visible and switches to the unlock surface.
        _ = LockVaultAsync();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exitRequested || _trayIcon is null || !_trayIcon.Available)
        {
            return;
        }
        args.Cancel = true;
        AppWindow.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowInfo("Keepassity",
                Loc.T("Still running in the notification area. Use its menu to exit."));
        }
    }

    private void RestoreWindowBounds()
    {
        var fallback = new RectInt32(28, 40, 1650, 970);
        if (Settings.WindowWidth is not int width || Settings.WindowHeight is not int height ||
            width < 800 || height < 600)
        {
            AppWindow.MoveAndResize(fallback);
            return;
        }
        try
        {
            DisplayArea area = DisplayArea.GetFromWindowId(AppWindow.Id,
                DisplayAreaFallback.Primary);
            RectInt32 work = area.WorkArea;
            width = Math.Min(width, work.Width);
            height = Math.Min(height, work.Height);
            int x = Settings.WindowX ?? work.X;
            int y = Settings.WindowY ?? work.Y;
            x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - 200));
            y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - 100));
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        catch
        {
            AppWindow.MoveAndResize(fallback);
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            Settings.WindowX = AppWindow.Position.X;
            Settings.WindowY = AppWindow.Position.Y;
            Settings.WindowWidth = AppWindow.Size.Width;
            Settings.WindowHeight = AppWindow.Size.Height;
            _settingsStore.Save();
        }
        catch
        {
            // Window bounds are a convenience; never block closing.
        }
    }

    private void ApplyThemePreference(string theme)
    {
        Root.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyWindowBackdrop();
    }

    private void TouchActivity()
    {
        _lastActivity = DateTimeOffset.UtcNow;
    }

    private async Task AutoLockTimer_Tick()
    {
        if (!_core.IsOpen || _busy || !Settings.AutoLockEnabled)
        {
            return;
        }
        int minutes = Math.Clamp(Settings.AutoLockMinutes, 1, 240);
        if (DateTimeOffset.UtcNow - _lastActivity >= TimeSpan.FromMinutes(minutes))
        {
            await LockVaultAsync();
        }
    }

    // ------------------------------------------------------- session lock

    private void InitializeSessionLockMonitor()
    {
        if (_sessionLockMonitor is not null)
        {
            return;
        }
        try
        {
            IntPtr window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _sessionLockMonitor = SessionLockMonitor.TryInstall(window, OnSessionLockEvent);
            if (_sessionLockMonitor is not null)
            {
                AppLog.Info("session lock monitor active");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("session lock monitor initialization failed", ex);
        }
    }

    /// <summary>Called on the UI thread by the window procedure subclass.</summary>
    private void OnSessionLockEvent(string reason)
    {
        // Dispatch explicitly: LockVaultAsync awaits core work, and a lock must
        // never run re-entrantly inside a window message handler.
        DispatcherQueue.TryEnqueue(() => _ = LockForSessionEventAsync(reason));
    }

    private async Task LockForSessionEventAsync(string reason)
    {
        if (_sessionLockPending)
        {
            // Windows emits several messages for one lock/suspend transition;
            // one in-flight lock request is enough.
            return;
        }
        if (!_core.IsOpen)
        {
            AppLog.Info($"session event ignored ({reason}): vault already locked");
            return;
        }
        _sessionLockPending = true;
        try
        {
            // A save/open may be in flight for a moment. Give it a short window to
            // finish instead of racing it; never lock while a database write is
            // still running. Edits are saved immediately in this app, so no save
            // is needed here before locking.
            for (int attempt = 0; attempt < 10 && _busy; attempt++)
            {
                await Task.Delay(200);
            }
            if (!_core.IsOpen)
            {
                AppLog.Info($"session event ({reason}): vault already locked");
                return;
            }
            if (_busy)
            {
                AppLog.Warn($"session event ({reason}): application busy; vault left " +
                            "unlocked (manual lock still available)");
                return;
            }
            AppLog.Info($"session event ({reason}): locking vault");
            await LockVaultAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error($"session event ({reason}): automatic lock failed", ex);
        }
        finally
        {
            _sessionLockPending = false;
        }
    }

    // --------------------------------------------------- network replay

    private void InitializeNetworkReplay()
    {
        try
        {
            _networkReplayTimer = DispatcherQueue.CreateTimer();
            _networkReplayTimer.Interval = TimeSpan.FromSeconds(2);
            _networkReplayTimer.IsRepeating = false;
            _networkReplayTimer.Tick += (_, _) =>
            {
                _networkReplayTimer?.Stop();
                _ = ReplaySyncAfterNetworkReturnAsync();
            };
            NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            AppLog.Info("network change monitor active");
        }
        catch (Exception ex)
        {
            AppLog.Error("network change monitor initialization failed", ex);
        }
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (!e.IsAvailable || _exitRequested)
        {
            return;
        }
        // This event runs on a worker thread; only touch UI state through the
        // dispatcher, and debounce the burst of events Windows raises while a
        // connection settles.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_exitRequested || _networkReplayTimer is null)
            {
                return;
            }
            _networkReplayTimer.Stop();
            _networkReplayTimer.Start();
        });
    }

    private async Task ReplaySyncAfterNetworkReturnAsync()
    {
        if (!IsSyncConfigured)
        {
            return;
        }
        if (!_core.IsOpen || _dbPath is null)
        {
            AppLog.Info("network replay skipped: vault is locked");
            return;
        }
        if (_busy)
        {
            AppLog.Info("network replay skipped: application busy");
            return;
        }
        AppLog.Info("network replay: connectivity restored, syncing pending changes");
        await SyncAsync();
    }

    private async Task ClearClipboardTimer_Tick()
    {
        string? expected = _lastClipboardText;
        _lastClipboardText = null;
        _clipboardTimer.Stop();
        if (expected is null)
        {
            return;
        }
        try
        {
            DataPackageView content = Clipboard.GetContent();
            if (content.Contains(StandardDataFormats.Text) &&
                await content.GetTextAsync() == expected)
            {
                Clipboard.Clear();
                ShowToast(Loc.T("Clipboard cleared"), InfoBarSeverity.Informational);
            }
        }
        catch
        {
            // Clipboard can be locked by another process; leave it alone.
        }
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTray();
        ScheduleTrayMenuDemo();
        ScheduleSettingsDemo();
        // The HWND may not have existed during construction on some WinAppSDK
        // versions; retry once the window is loaded.
        if (_sessionLockMonitor is null)
        {
            InitializeSessionLockMonitor();
        }
        if (Settings.StartMinimized && _trayIcon is { Available: true })
        {
            AppWindow.Hide();
        }
        string? devPassword = TestHooks.EnvironmentValue("KEEPASSITY_MOCK_PASSWORD");
        if (_dbPath is not null && !string.IsNullOrEmpty(devPassword))
        {
            MasterPassword.Password = devPassword;
            await UnlockAsync();
        }
    }

    /// <summary>
    /// Test hook for automated visual checks. KEEPASSITY_TRAY_MENU_AT="x,y" opens
    /// the tray menu at that physical screen position shortly after startup.
    /// KEEPASSITY_TRAY_MENU_SCHEDULE="1500,x,y;6000,x,y;7000,x,y" provides an
    /// explicit list of delay/position entries (each call toggles the menu), which
    /// lets a harness compare a first open with a repeated open in one process.
    /// </summary>
    private void ScheduleTrayMenuDemo()
    {
        if (_trayIcon is null)
        {
            return;
        }
        bool anchorToIcon = string.Equals(
            TestHooks.EnvironmentValue("KEEPASSITY_TRAY_MENU_ANCHOR"),
            "icon", StringComparison.OrdinalIgnoreCase);
        string? schedule = TestHooks.EnvironmentValue("KEEPASSITY_TRAY_MENU_SCHEDULE");
        string? point = TestHooks.EnvironmentValue("KEEPASSITY_TRAY_MENU_AT");
        int pointX = 0;
        int pointY = 0;
        bool hasPoint = point is not null && TryParsePoint(point, out pointX, out pointY);
        if (string.IsNullOrWhiteSpace(schedule))
        {
            if (!hasPoint)
            {
                return;
            }
            schedule = "1500," + point;
        }
        foreach (string entry in schedule.Split(';',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = entry.Split(',');
            if (parts.Length != 3 ||
                !int.TryParse(parts[0].Trim(), out int delay) ||
                !int.TryParse(parts[1].Trim(), out int x) ||
                !int.TryParse(parts[2].Trim(), out int y))
            {
                AppLog.Warn("tray menu test schedule entry must be delay,x,y");
                continue;
            }
            AddTrayMenuDemoTimer(TimeSpan.FromMilliseconds(Math.Max(100, delay)),
                repeating: false, anchorToIcon, x, y);
        }
        // Repeating open/close cycle for interactive verification: keeps the menu
        // appearing regularly so an external capture tool can catch it even when
        // the reviewer misses the first window.
        if (int.TryParse(TestHooks.EnvironmentValue("KEEPASSITY_TRAY_MENU_CYCLE_MS"),
                out int cycle) && cycle >= 500 && hasPoint)
        {
            AddTrayMenuDemoTimer(TimeSpan.FromMilliseconds(cycle),
                repeating: true, anchorToIcon, pointX, pointY);
        }
    }

    private static bool TryParsePoint(string specification, out int x, out int y)
    {
        x = 0;
        y = 0;
        string[] parts = specification.Split(',');
        return parts.Length == 2 &&
            int.TryParse(parts[0].Trim(), out x) &&
            int.TryParse(parts[1].Trim(), out y);
    }

    /// <summary>
    /// Test hook for automated visual checks: KEEPASSITY_SETTINGS_DEMO_MS opens
    /// the Settings panel once after that many milliseconds, which exercises the
    /// About artwork loading path for an external capture tool.
    /// </summary>
    private void ScheduleSettingsDemo()
    {
        if (!int.TryParse(TestHooks.EnvironmentValue("KEEPASSITY_SETTINGS_DEMO_MS"),
                out int delay) || delay < 500)
        {
            return;
        }
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(delay);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (MainRoot.Visibility == Visibility.Visible && !_rightPanelOpen)
            {
                SettingsButton_Click(this, new RoutedEventArgs());
            }
        };
        timer.Start();
    }

    private void AddTrayMenuDemoTimer(TimeSpan interval, bool repeating, bool anchorToIcon,
        int x, int y)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = interval;
        timer.IsRepeating = repeating;
        timer.Tick += (_, _) =>
        {
            if (anchorToIcon)
            {
                _trayIcon?.ShowMenuAtIconForTest();
            }
            else
            {
                _trayIcon?.ShowMenuAt(x, y);
            }
        };
        _trayMenuDemoTimers.Add(timer);
        timer.Start();
    }

    private void ParseArguments()
    {
        string[] args = Environment.GetCommandLineArgs();
        string? candidate = LaunchArguments.FindDatabasePath(args, File.Exists);
        if (candidate is null)
        {
            return;
        }
        _dbPath = Path.GetFullPath(candidate);
        // An explicit local file wins over a previously opened WebDAV
        // source; drop that remote target so saves cannot overwrite it.
        if (string.Equals(Settings.LastDatabaseKind, "webdav",
                StringComparison.OrdinalIgnoreCase))
        {
            Settings.WebDavUrl = null;
            Settings.WebDavUsername = null;
        }
        Settings.LastDatabaseKind = "local";
        Settings.LastWebDavUrl = null;
        Settings.LastWebDavUsername = null;
        AppLog.Info("launch argument selected a local database");
    }

    private void UpdateDatabasePath()
    {
        if (_pendingRemoteDownload is not null && !string.Equals(_dbPath,
                _pendingRemoteDownload.WorkingCopyPath, StringComparison.OrdinalIgnoreCase))
        {
            // The pending download belongs to the previous database. A later
            // unlock of a different file must never promote those bytes.
            ClearPendingRemoteDownload();
        }
        Unlock.SetDatabasePath(_dbPath);
        RefreshRecentDatabaseList();
        bool hasDatabase = _dbPath is not null;
        UnlockTitle.Text = hasDatabase ? Loc.T("Unlock Keepassity") : "Keepassity";
        UnlockStartHint.Visibility = hasDatabase ? Visibility.Collapsed : Visibility.Visible;
        DatabasePathText.Visibility = hasDatabase ? Visibility.Visible : Visibility.Collapsed;
        UnlockFormSection.Visibility = hasDatabase ? Visibility.Visible : Visibility.Collapsed;
        UnlockDivider.Visibility = hasDatabase ? Visibility.Visible : Visibility.Collapsed;
        DatabaseActionsLabel.Visibility = hasDatabase ? Visibility.Visible : Visibility.Collapsed;
        if (!hasDatabase)
        {
            Unlock.ClearStatus();
            DatabasePathText.Text = Loc.T("No database selected");
            ToolTipService.SetToolTip(DatabasePathText,
                Loc.T("Open a local .kdbx file or open from WebDAV"));
            return;
        }
        if (_webDavSource && !string.IsNullOrWhiteSpace(_sourceWebDavUrl))
        {
            DatabasePathText.Text = Loc.T("WebDAV: {0}", _sourceWebDavUrl);
            ToolTipService.SetToolTip(DatabasePathText,
                _sourceWebDavUrl + Environment.NewLine +
                Loc.T("Local working copy: {0}", _dbPath));
            return;
        }
        DatabasePathText.Text = Path.GetFileName(_dbPath);
        ToolTipService.SetToolTip(DatabasePathText, _dbPath);
    }

    private string DisplayVaultName()
    {
        if (_webDavSource && !string.IsNullOrWhiteSpace(_sourceWebDavUrl))
        {
            try
            {
                string name = Path.GetFileName(new Uri(_sourceWebDavUrl).AbsolutePath);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return Path.GetFileNameWithoutExtension(Uri.UnescapeDataString(name));
                }
            }
            catch
            {
                // Fall back to the working-copy file name.
            }
        }
        return _dbPath is null ? Loc.T("Vault") : Path.GetFileNameWithoutExtension(_dbPath);
    }

    // ------------------------------------------------------ window assets

    /// <summary>
    /// Applies the branded application icon to the window (and thus the taskbar
    /// and Alt+Tab switcher). WinUI 3 does not inherit the executable icon for a
    /// window; the supported approach for unpackaged apps is
    /// AppWindow.SetIcon with a fully qualified .ico path.
    /// </summary>
    private void ApplyWindowIcon()
    {
        string iconPath = AppAssets.AppIconPath;
        if (File.Exists(iconPath))
        {
            try
            {
                AppWindow.SetIcon(iconPath);
                AppLog.Info("window icon applied from Assets/app.ico");
                return;
            }
            catch (Exception ex)
            {
                AppLog.Error("window icon could not be applied from Assets/app.ico", ex);
            }
        }
        else
        {
            AppLog.Warn("window icon: Assets/app.ico is missing next to the executable");
        }
        // Fallback: reuse the icon embedded in the executable by the
        // ApplicationIcon project property so a missing Assets folder still
        // produces a branded window icon.
        try
        {
            string executable = Environment.ProcessPath ?? string.Empty;
            if (string.IsNullOrEmpty(executable) ||
                ExtractIconEx(executable, 0, out IntPtr large, out IntPtr small, 1) == 0)
            {
                AppLog.Warn("window icon: no executable icon resource to fall back to");
                return;
            }
            IntPtr handle = large != IntPtr.Zero ? large : small;
            if (handle != IntPtr.Zero)
            {
                AppWindow.SetIcon(Microsoft.UI.Win32Interop.GetIconIdFromIcon(handle));
                AppLog.Info("window icon applied from the embedded executable resource");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("window icon: embedded icon fallback failed", ex);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large,
        out IntPtr small, uint count);

    // ------------------------------------------------------ keyboard input

    /// <summary>
    /// Conventional desktop shortcuts. Accelerators live on the window content
    /// root so they work regardless of which control has focus; the chosen keys
    /// (Ctrl+F, Ctrl+L, Ctrl+N, Ctrl+S, F5) are not text-editing commands.
    /// Escape is intentionally not an accelerator: Root_KeyDown already closes
    /// the open right/detail panel, and flyouts close themselves natively.
    /// </summary>
    private void RegisterKeyboardAccelerators()
    {
        AddKeyboardAccelerator(VirtualKey.F, VirtualKeyModifiers.Control,
            FocusSearchShortcut);
        AddKeyboardAccelerator(VirtualKey.L, VirtualKeyModifiers.Control,
            LockShortcut);
        AddKeyboardAccelerator(VirtualKey.N, VirtualKeyModifiers.Control,
            NewEntryShortcut);
        AddKeyboardAccelerator(VirtualKey.S, VirtualKeyModifiers.Control,
            SaveShortcut);
        AddKeyboardAccelerator(VirtualKey.F5, VirtualKeyModifiers.None,
            SyncShortcut);
    }

    private void AddKeyboardAccelerator(VirtualKey key, VirtualKeyModifiers modifiers,
        Action action)
    {
        var accelerator = new KeyboardAccelerator
        {
            Key = key,
            Modifiers = modifiers,
        };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                AppLog.Error($"keyboard shortcut {key} failed", ex);
            }
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    private void FocusSearchShortcut()
    {
        if (MainRoot.Visibility != Visibility.Visible || _busy)
        {
            return;
        }
        ClosePanels();
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void LockShortcut()
    {
        if (_core.IsOpen && !_busy)
        {
            _ = LockVaultAsync();
        }
    }

    private void NewEntryShortcut()
    {
        // Never stack a second editor on top of an open panel or an in-flight
        // save: the shortcut is only live when the workspace is idle.
        if (!_core.IsOpen || _busy || _rightPanelOpen || _detailPanelOpen)
        {
            return;
        }
        NewEntryButton_Click(this, new RoutedEventArgs());
    }

    private void SyncShortcut()
    {
        if (!_core.IsOpen || _busy || !IsSyncConfigured)
        {
            return;
        }
        _ = SyncAsync();
    }

    private void SaveShortcut()
    {
        if (!_core.IsOpen)
        {
            return;
        }
        // Every edit path saves the database immediately, so Ctrl+S never needs
        // to write the vault. Mapping it to "sync now" is the honest meaning of
        // save for a synced database; without WebDAV it just reports the truth.
        if (IsSyncConfigured)
        {
            ShowToast(Loc.T("Changes are saved automatically; syncing now…"),
                InfoBarSeverity.Informational);
            _ = SyncAsync();
        }
        else
        {
            ShowToast(Loc.T("Every change is saved automatically — nothing to save."),
                InfoBarSeverity.Informational);
        }
    }

    // --------------------------------------------- single-instance relay

    private void InitializeSingleInstanceRelay()
    {
        _singleInstanceRelay = SingleInstanceRelay.TryStart(OnForwardedDatabaseRequest);
        if (_singleInstanceRelay is null)
        {
            AppLog.Warn("open requests from a second launch will not be forwarded");
        }
    }

    /// <summary>
    /// Called on the UI thread from the relay window procedure when a second
    /// launch hands over a .kdbx path. The actual lock/prefill work is deferred
    /// so it never runs re-entrantly inside a window message (the sending
    /// process is waiting for that message to be handled).
    /// </summary>
    private void OnForwardedDatabaseRequest(string databasePath)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AppWindow.Show();
                Activate();
            }
            catch (Exception ex)
            {
                AppLog.Error("could not raise the window for a forwarded open request", ex);
            }
            _ = OpenExternalDatabaseAsync(databasePath, "another instance");
        });
    }

    // ------------------------------------------------------------ drag/drop

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        // Only external file drops are handled at the window level. Drags that
        // start inside the window (entry -> group) carry text, not StorageItems,
        // and are left to the group drop targets.
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.T("Open database");
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }
        e.Handled = true;
        try
        {
            IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
            StorageFile? database = items.OfType<StorageFile>()
                .FirstOrDefault(file => file.Name.EndsWith(".kdbx",
                    StringComparison.OrdinalIgnoreCase));
            if (database is null)
            {
                ShowToast(Loc.T("Only .kdbx database files can be opened this way."),
                    InfoBarSeverity.Warning);
                return;
            }
            await OpenExternalDatabaseAsync(database.Path, "dropped file");
        }
        catch (Exception ex)
        {
            AppLog.Error("dropped-file handling failed", ex);
            ShowToast(Loc.T("The dropped file could not be read."), InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Opens a database selected outside the normal open dialog (dropped file or
    /// a path forwarded by a second launch). Locks the current vault first, then
    /// pre-fills the unlock page; same database and busy states are reported
    /// instead of silently doing nothing.
    /// </summary>
    private async Task OpenExternalDatabaseAsync(string droppedPath, string origin)
    {
        if (_busy)
        {
            AppLog.Info($"open request ({origin}) ignored: application busy");
            ShowToast(Loc.T("Busy saving or syncing; try again in a moment."),
                InfoBarSeverity.Informational);
            return;
        }
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(droppedPath);
        }
        catch
        {
            AppLog.Warn($"open request ({origin}) ignored: invalid path");
            ShowToast(Loc.T("The database path is not valid."), InfoBarSeverity.Error);
            return;
        }
        if (!File.Exists(fullPath))
        {
            AppLog.Warn($"open request ({origin}) ignored: file missing");
            ShowToast(Loc.T("The database file no longer exists."), InfoBarSeverity.Error);
            return;
        }
        // MainRoot visibility is the reliable "vault is unlocked" signal here:
        // CoreClient keeps its native handle after kp_lock, so _core.IsOpen stays
        // true for a locked vault.
        bool unlocked = MainRoot.Visibility == Visibility.Visible;
        if (unlocked && string.Equals(_dbPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            AppLog.Info($"open request ({origin}) ignored: database already open");
            ShowToast(Loc.T("That database is already open."), InfoBarSeverity.Informational);
            return;
        }
        if (unlocked)
        {
            // Opening another file means "open this instead": end the current
            // session cleanly first (all edits are already saved).
            await LockVaultAsync();
            if (MainRoot.Visibility == Visibility.Visible)
            {
                AppLog.Warn($"open request ({origin}) abandoned: the current vault " +
                            "could not be locked");
                return;
            }
        }
        if (!string.Equals(_dbPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            _dbPath = fullPath;
            _webDavSource = false;
            _sourceWebDavUrl = null;
            Settings.LastDatabaseKind = "local";
            Settings.LastWebDavUrl = null;
            Settings.LastWebDavUsername = null;
            // A different local file must not inherit another database's sync
            // target, or a save could overwrite that remote database.
            Settings.WebDavUrl = null;
            Settings.WebDavUsername = null;
            _settingsStore.Save();
            AppLog.Info($"open request ({origin}): unlock page prefilled");
        }
        else
        {
            // Same database as the (locked) selection: just make sure the unlock
            // surface is showing it.
            AppLog.Info($"open request ({origin}): unlock page shown for the selected database");
        }
        UpdateDatabasePath();
        Unlock.ShowStatus(Loc.T("Database ready to unlock"),
            Loc.T("Enter the master password to unlock this database."),
            UnlockStatusSeverity.Informational);
        MasterPassword.Focus(FocusState.Programmatic);
    }

    // -------------------------------------------------------------- about

    private async Task ApplyAboutIconAsync(Image image)
    {
        if (await SetImageFromFileAsync(image, AppAssets.AppIconPreviewPath))
        {
            image.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Loads a PNG next to the executable into an Image element. Unpackaged
    /// WinUI 3 apps cannot rely on ms-appx URIs for files copied next to the
    /// executable, so the bytes are streamed into a BitmapImage instead.
    /// </summary>
    private static async Task<bool> SetImageFromFileAsync(Image image, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                AppLog.Warn("about icon asset is missing next to the executable");
                return false;
            }
            byte[] bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            try
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
            }
            finally
            {
                writer.DetachStream();
                writer.Dispose();
            }
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            image.Source = bitmap;
            AppLog.Info("about icon loaded from Assets/app-icon-256.png");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("about icon could not be loaded", ex);
            return false;
        }
    }

    private void ApplyWindowBackdrop()
    {
        Color pageColor = Root.RequestedTheme switch
        {
            ElementTheme.Light => Color.FromArgb(255, 243, 243, 243),
            ElementTheme.Dark => Color.FromArgb(255, 32, 32, 32),
            _ => Root.ActualTheme == ElementTheme.Light
                ? Color.FromArgb(255, 243, 243, 243)
                : Color.FromArgb(255, 32, 32, 32),
        };
        try
        {
            if (Root.RequestedTheme == ElementTheme.Default)
            {
                // Mica follows the system theme; only use it when the app does too.
                SystemBackdrop = new MicaBackdrop();
                Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            }
            else
            {
                SystemBackdrop = null;
                Root.Background = new SolidColorBrush(pageColor);
            }
        }
        catch
        {
            // Mica is unavailable on some Windows builds; keep the solid fallback.
            SystemBackdrop = null;
            Root.Background = new SolidColorBrush(pageColor);
        }
    }

    private void ApplyTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        bool light = Root.ActualTheme == ElementTheme.Light;
        var transparent = Color.FromArgb(0, 0, 0, 0);
        var foreground = light
            ? Color.FromArgb(255, 28, 30, 36)
            : Color.FromArgb(255, 245, 247, 250);
        var inactive = light
            ? Color.FromArgb(255, 110, 116, 128)
            : Color.FromArgb(255, 170, 176, 188);
        titleBar.ButtonForegroundColor = foreground;
        titleBar.ButtonInactiveForegroundColor = inactive;
        titleBar.ButtonBackgroundColor = transparent;
        titleBar.ButtonInactiveBackgroundColor = transparent;
        titleBar.ButtonHoverBackgroundColor = light
            ? Color.FromArgb(255, 228, 231, 236)
            : Color.FromArgb(255, 48, 52, 62);
        titleBar.ButtonHoverForegroundColor = foreground;
        titleBar.ButtonPressedBackgroundColor = light
            ? Color.FromArgb(255, 212, 217, 224)
            : Color.FromArgb(255, 64, 70, 84);
        titleBar.ButtonPressedForegroundColor = foreground;
    }

    /// <summary>The two workspace columns slide in independently.</summary>
    private void AnimateWorkspace()
    {
        // Clear any previous whole-workspace animation state and animate the
        // middle list and the detail column as separate surfaces instead.
        WorkspacePanel.Opacity = 1;
        WorkspacePanel.RenderTransform = null;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(280));
        var middleTransform = new TranslateTransform { X = 18 };
        var detailTransform = new TranslateTransform { X = 18 };
        EntryColumn.RenderTransform = middleTransform;
        EntryColumn.Opacity = 0;
        DetailPane.RenderTransform = detailTransform;
        DetailPane.Opacity = 0;

        var storyboard = new Storyboard();
        void AddSlide(TranslateTransform transform)
        {
            var slide = new DoubleAnimation
            {
                To = 0,
                Duration = duration,
                EasingFunction = ease,
            };
            Storyboard.SetTarget(slide, transform);
            Storyboard.SetTargetProperty(slide, "X");
            storyboard.Children.Add(slide);
        }
        AddSlide(middleTransform);
        var middleFade = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(middleFade, EntryColumn);
        Storyboard.SetTargetProperty(middleFade, "Opacity");
        storyboard.Children.Add(middleFade);

        AddSlide(detailTransform);
        var detailFade = new DoubleAnimation
        {
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(detailFade, DetailPane);
        Storyboard.SetTargetProperty(detailFade, "Opacity");
        storyboard.Children.Add(detailFade);

        storyboard.Begin();
    }

    /// <summary>Slide and fade a full-width or detail-column page into view.</summary>
    private static void AnimatePanelIn(Grid panel, double offset = 28, int milliseconds = 220)
    {
        var transform = new TranslateTransform { X = offset };
        panel.RenderTransform = transform;
        panel.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    /// <summary>Fade a page out before the caller restores the content below it.</summary>
    private static Task AnimatePanelOutAsync(Grid panel, double offset = 18,
        int milliseconds = 140)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var transform = panel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        panel.RenderTransform = transform;
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation { To = offset, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        var fade = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, panel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            panel.Opacity = 1;
            transform.X = 0;
            completion.TrySetResult();
        };
        storyboard.Begin();
        return completion.Task;
    }

    /// <summary>Small cross-fade when the selected entry details change.</summary>
    private void AnimateDetailContent()
    {
        if (_detailPanelOpen)
        {
            return;
        }
        var transform = new TranslateTransform { X = 7 };
        DetailPane.RenderTransform = transform;
        DetailPane.Opacity = 0.72;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(170));
        var storyboard = new Storyboard();
        var slide = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slide, transform);
        Storyboard.SetTargetProperty(slide, "X");
        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, DetailPane);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slide);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    // ---------------------------------------------------------------- sync

    private bool IsSyncConfigured =>
        !string.IsNullOrWhiteSpace(Settings.WebDavUrl) &&
        !string.IsNullOrWhiteSpace(Settings.WebDavUsername);

    /// <summary>
    /// Remembers the sync state as a catalog key (+ arguments) and renders it, so
    /// a language switch can re-render the same state instead of leaving a stale
    /// label. <paramref name="source"/> is an English source string from the
    /// catalog; use <c>{0}</c> placeholders for <paramref name="arguments"/>.
    /// </summary>
    private void SetSyncStatus(string source, params object?[] arguments)
        => SyncStatus.Set(source, arguments);

    private void RenderSyncStatus()
    {
        SyncStatus.Refresh();
        Announce(Loc.T("Sync status: {0}", SyncStatus.Text));
    }

    // ------------------------------------------------------ accessibility

    /// <summary>
    /// Writes a short status message to the hidden live region and raises both a
    /// live-region change and a UIA notification so screen readers announce
    /// unlock, save, sync and lock results instead of the user having to find
    /// the toast by hand.
    /// </summary>
    private void Announce(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        try
        {
            StatusAnnouncement.Text = message;
            AutomationPeer? peer = GetOrCreateAutomationPeer(StatusAnnouncement);
            if (peer is null)
            {
                if (!_announcePeerWarningLogged)
                {
                    _announcePeerWarningLogged = true;
                    AppLog.Warn("screen-reader announcement: no automation peer available");
                }
            }
            else
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
                peer.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted,
                    AutomationNotificationProcessing.ImportantMostRecent, message,
                    "keepassity.status");
            }
            // Raising the same event on the now-visible toast gives screen readers
            // a second, on-screen live-region source for the announcement.
            if (Toast.IsOpen)
            {
                GetOrCreateAutomationPeer(Toast)
                    ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"screen-reader announcement failed: {ex.GetType().Name}");
        }
    }

    private static AutomationPeer? GetOrCreateAutomationPeer(FrameworkElement element)
        => FrameworkElementAutomationPeer.FromElement(element) ??
           FrameworkElementAutomationPeer.CreatePeerForElement(element);

    private static void SetButtonAutomation(Button button, string name, string? helpText = null,
        string? automationId = null)
    {
        AutomationProperties.SetName(button, name);
        if (!string.IsNullOrWhiteSpace(helpText))
        {
            AutomationProperties.SetHelpText(button, helpText);
        }
        if (!string.IsNullOrWhiteSpace(automationId))
        {
            AutomationProperties.SetAutomationId(button, automationId);
        }
    }

    private static string CertificateTrustKey(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            ? $"{uri.Host}:{uri.Port}"
            : url;
    }

    /// <summary>
    /// Creates the WebDAV client, asking the user to confirm a self-signed server
    /// certificate first (SY-04: explicit trust with fingerprint, never a global
    /// validation bypass). Publicly trusted certificates need no prompt.
    /// </summary>
    private async Task<WebDavClient> CreateWebDavClientAsync(string url, string username,
        string password)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return new WebDavClient(url, username, password);
        }
        TlsCertificateInfo info;
        try
        {
            info = await WebDavTls.ProbeAsync(uri.Host, uri.Port);
        }
        catch (WebDavException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WebDavException(
                Loc.T("Could not check the TLS certificate of {0}:{1}: {2}",
                    uri.Host, uri.Port, ex.Message));
        }
        if (info.IsPubliclyTrusted)
        {
            return new WebDavClient(url, username, password);
        }
        string key = CertificateTrustKey(url);
        if (Settings.TrustedCertificates.TryGetValue(key, out string? trusted) &&
            string.Equals(trusted, info.Sha256FingerprintHex, StringComparison.Ordinal))
        {
            return new WebDavClient(url, username, password, trusted);
        }
        bool changed = Settings.TrustedCertificates.ContainsKey(key);
        if (!await ConfirmWebDavCertificateAsync(info, changed))
        {
            throw new WebDavException(Loc.T(
                "The certificate presented by {0} was not trusted, so the connection was " +
                "cancelled.", info.Endpoint));
        }
        Settings.TrustedCertificates[key] = info.Sha256FingerprintHex;
        _settingsStore.Save();
        AppLog.Info("trusted WebDAV certificate for " + key +
                    " (sha256 " + info.Sha256FingerprintHex + ")");
        return new WebDavClient(url, username, password, info.Sha256FingerprintHex);
    }

    private async Task<bool> ConfirmWebDavCertificateAsync(TlsCertificateInfo info, bool changed)
    {
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = changed
                ? Loc.TLong("Dialog.CertificateChangedBody",
                    "The server's certificate changed since it was trusted. Only continue " +
                    "if you changed it yourself or an administrator did.")
                : Loc.TLong("Dialog.CertificateUntrustedBody",
                    "Windows does not trust this server's certificate. That is normal for a " +
                    "self-signed certificate; only trust it if you installed the server or " +
                    "an administrator gave you this fingerprint."),
            TextWrapping = TextWrapping.Wrap,
        });
        void AddRow(string label, string value)
        {
            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
            row.Children.Add(new TextBlock
            {
                Text = value,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
            content.Children.Add(row);
        }
        AddRow(Loc.T("Server"), info.Endpoint);
        AddRow(Loc.T("Subject"), info.Subject);
        AddRow(Loc.T("Issuer"), info.Issuer);
        AddRow(Loc.T("Expires"), info.NotAfter.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
        AddRow(Loc.T("SHA-256 fingerprint"), info.Sha256FingerprintHex);
        var dialog = new ContentDialog
        {
            Title = changed
                ? Loc.T("Server certificate changed")
                : Loc.T("Trust this server certificate?"),
            Content = content,
            PrimaryButtonText = Loc.T("Trust"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
    private async Task SyncAsync(bool startup = false)
    {
        if (!await _syncGate.WaitAsync(0))
        {
            return;
        }
        try
        {
            if (!_core.IsOpen || _dbPath is null || string.IsNullOrEmpty(_masterPassword))
            {
                // A locked vault keeps the core handle, but saving or verifying
                // remote bytes needs the master password; nothing to sync yet.
                return;
            }
            if (!IsSyncConfigured)
            {
                SetSyncStatus("Local only");
                if (!startup)
                {
                    ShowToast(Loc.T("Configure WebDAV in Settings first."), InfoBarSeverity.Informational);
                }
                return;
            }
            string url = Settings.WebDavUrl!.Trim();
            // A settings file written by an older build could still hold a plain
            // http URL; never send Basic credentials over it (SY-03/SY-04 policy).
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? syncUri) ||
                syncUri.Scheme != Uri.UriSchemeHttps)
            {
                SetSyncStatus("WebDAV needs https");
                if (!startup)
                {
                    ShowToast(Loc.T("WebDAV sync requires an https:// URL: Basic authentication must use TLS."), InfoBarSeverity.Warning);
                }
                return;
            }
            string username = Settings.WebDavUsername!.Trim();
            string? password = _credentials.LoadPassword(url, username);
            if (password is null)
            {
                SetSyncStatus("No credentials");
                if (!startup)
                {
                    ShowToast(Loc.T("Set the WebDAV password in Settings."), InfoBarSeverity.Warning);
                }
                return;
            }

            SetSyncStatus("Syncing…");
            using var dav = await CreateWebDavClientAsync(url, username, password);
            var options = new SyncOptions
            {
                RemoteUrl = url,
                Username = username,
                MasterPassword = _masterPassword,
            };
            SyncReport report = await _sync.RunAsync(
                dav,
                new LiveVaultDocument(this, _dbPath),
                _core,
                options,
                update => SetSyncStatus(update.Source, update.Arguments));
            ShowSyncReport(report, startup);
        }
        catch (WebDavException ex)
        {
            AppLog.Warn($"sync: {ex.Message}");
            SetSyncStatus(ex.Conflict ? "Conflict" : "Sync error");
            if (!startup)
            {
                ShowToast(ex.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            AppLog.Warn("webdav unreachable; working offline");
            SetSyncStatus("Offline");
            if (!startup)
            {
                ShowToast(Loc.T("WebDAV is unreachable; working offline."), InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("sync failed", ex);
            SetSyncStatus("Sync error");
            if (!startup)
            {
                ShowToast(Loc.T("Sync failed: {0}", ex.Message), InfoBarSeverity.Error);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task<SyncConflictChoice> ShowConflictDialogAsync(SyncConflictContext context)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.TLong("Dialog.ConflictBody",
                "Local and remote both changed. Conflict copies have been saved before " +
                "asking what to do."),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("Local copy: {0}", Path.GetFileName(context.LocalCopy)) + "\n" +
                   Loc.T("Remote copy: {0}", Path.GetFileName(context.RemoteCopy)),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        if (context.Conflicts.Count > 0)
        {
            var lines = context.Conflicts.Take(6).Select(conflict =>
            {
                string fields = conflict.Fields.Count == 0
                    ? Loc.T("entry changed on both sides")
                    : string.Join(", ", conflict.Fields);
                return $"• {conflict.Kind}: {fields}";
            });
            panel.Children.Add(new TextBlock
            {
                Text = string.Join("\n", lines),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        SyncConflictChoice choice = SyncConflictChoice.Cancel;
        var keepBoth = new Button
        {
            Content = Loc.T("Keep both (recommended)"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var keepLocal = new Button
        {
            Content = Loc.T("Keep local"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var keepRemote = new Button
        {
            Content = Loc.T("Keep remote"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancel = new Button
        {
            Content = Loc.T("Cancel and keep both files untouched"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(keepBoth);
        panel.Children.Add(keepLocal);
        panel.Children.Add(keepRemote);
        panel.Children.Add(cancel);
        var dialog = new ContentDialog
        {
            Title = Loc.T("Sync conflict"),
            Content = panel,
            XamlRoot = Root.XamlRoot,
        };
        keepBoth.Click += (_, _) =>
        {
            choice = SyncConflictChoice.KeepBoth;
            dialog.Hide();
        };
        keepLocal.Click += (_, _) =>
        {
            choice = SyncConflictChoice.KeepLocal;
            dialog.Hide();
        };
        keepRemote.Click += (_, _) =>
        {
            choice = SyncConflictChoice.KeepRemote;
            dialog.Hide();
        };
        cancel.Click += (_, _) =>
        {
            choice = SyncConflictChoice.Cancel;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => keepBoth.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
        return choice;
    }

    /// <summary>
    /// Turn the coordinator's report into the localized follow-up the user sees.
    /// The status line was already reported through the progress callback; this is
    /// only the toast, including the conflict copy names.
    /// </summary>
    private void ShowSyncReport(SyncReport report, bool startup)
    {
        if (report.Notice == SyncNotice.None)
        {
            if (report.Outcome == SyncOutcome.Offline && !startup)
            {
                ShowToast(Loc.T("Sync skipped: {0}", report.Reason),
                    InfoBarSeverity.Warning);
            }
            return;
        }
        string copies = Loc.T("local: {0}; remote: {1}",
            Path.GetFileName(report.LocalCopy ?? string.Empty),
            Path.GetFileName(report.RemoteCopy ?? string.Empty));
        switch (report.Notice)
        {
            case SyncNotice.Downloaded:
                ShowToast(Loc.T("Downloaded remote changes"), InfoBarSeverity.Success);
                break;
            case SyncNotice.MergedAutomatically:
                ShowToast(Loc.T("Merged remote changes automatically ({0}).", copies),
                    InfoBarSeverity.Success);
                break;
            case SyncNotice.MergedKeptBoth:
                ShowToast(Loc.T("Kept both versions; merged and uploaded ({0}).", copies),
                    InfoBarSeverity.Success);
                break;
            case SyncNotice.KeptLocal:
                ShowToast(Loc.T("Kept local version; remote copy was saved."),
                    InfoBarSeverity.Success);
                break;
            case SyncNotice.KeptRemote:
                ShowToast(Loc.T("Kept remote version; local copy was saved."),
                    InfoBarSeverity.Success);
                break;
            case SyncNotice.ConflictPaused:
                ShowToast(Loc.T("Sync paused. Local and remote are unchanged; conflict copies were saved."),
                    InfoBarSeverity.Warning);
                break;
            case SyncNotice.MergedRemoteChanged:
                ShowToast(Loc.T("Remote changed again; merged locally, will retry next sync."),
                    InfoBarSeverity.Warning);
                break;
            case SyncNotice.KeptLocalRemoteChanged:
                ShowToast(Loc.T("Remote changed again; local version kept, will retry next sync."),
                    InfoBarSeverity.Warning);
                break;
        }
    }

    /// <summary>
    /// Three-way merge through a second core handle, so the open window's model
    /// is untouched until the merged bytes replace the file. The sync
    /// coordinator reaches this through <see cref="IVaultDocument.MergeRemoteAsync"/>.
    /// </summary>
    internal async Task<SyncMergeResult> ProbeMergeAsync(byte[] remote)
    {
        using var probe = new CoreClient();
        await probe.OpenAsync(_dbPath!, _masterPassword!);
        try
        {
            return await probe.SyncMergeAsync(remote);
        }
        finally
        {
            await probe.LockAsync();
        }
    }

    /// <summary>
    /// Reopen the core and refresh the UI from the file just written. The sync
    /// coordinator's <see cref="IVaultDocument.ReloadAsync"/> adapter calls this.
    /// </summary>
    internal async Task ReopenFromLocalAsync()
    {
        if (_dbPath is null || string.IsNullOrEmpty(_masterPassword))
        {
            return;
        }
        await _core.OpenAsync(_dbPath, _masterPassword);
        // Stay in the group the user is looking at; ReloadVaultAsync keeps the
        // selected row and restores the wheel position.
        await ReloadVaultAsync(_currentFull?.Uuid);
    }

    /// <summary>Lowercase hex SHA-256, the same shape the core's sync baseline uses.</summary>
    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>
    /// Durable, gated write of database bytes (sync merge/download). The sync
    /// coordinator's <see cref="IVaultDocument.ReplaceAsync"/> adapter calls this;
    /// it only accepts the open vault and refuses when the file changed outside
    /// the application.
    /// </summary>
    internal async Task WriteLocalBytesAsync(string path, byte[] bytes)
    {
        if (_dbPath is null ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_dbPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "a sync write may only replace the database that is currently open");
        }
        try
        {
            await _vault.ReplaceAsync(bytes, Settings.BackupRetention, "sync");
        }
        catch (CoreException ex) when (ex.Code == "VaultChanged")
        {
            throw new InvalidOperationException(Loc.T(
                "The database was changed outside Keepassity. Reopen it before " +
                "saving; nothing was overwritten."));
        }
    }

    private void MarkLocalDirty()
    {
        if (_dbPath is null || !IsSyncConfigured)
        {
            return;
        }
        SyncState state = _syncStates.Load(Settings.WebDavUrl!.Trim(),
            Settings.WebDavUsername!.Trim(), _dbPath);
        state.LocalDirty = true;
        _syncStates.Save(Settings.WebDavUrl!.Trim(), Settings.WebDavUsername!.Trim(), _dbPath,
            state);
        SetSyncStatus("Not synced");
    }

    private async Task ShowBackupsAsync()
    {
        if (_dbPath is null || _core.IsOpen == false)
        {
            return;
        }
        List<BackupEntry> backups = BackupService.ListBackups(_dbPath);
        if (backups.Count == 0)
        {
            ShowToast(Loc.T("No backups yet. They are created before each save."), InfoBarSeverity.Informational);
            return;
        }
        var choices = new ComboBox
        {
            Header = Loc.T("Backup"),
            ItemsSource = backups,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = Loc.T("Restoring backs up the current database first. Keeping {0} copies.", Settings.BackupRetention),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(choices);
        if (await ShowDetailPanelAsync(Loc.T("Restore backup"), content, Loc.T("Restore"),
                primaryGlyph: "\uE777") !=
                RightPanelResult.Primary ||
            choices.SelectedItem is not BackupEntry selected)
        {
            return;
        }
        if (string.IsNullOrEmpty(_masterPassword))
        {
            ShowToast(Loc.T("Unlock the vault before restoring a backup."), InfoBarSeverity.Warning);
            return;
        }
        try
        {
            byte[] selectedBytes = await File.ReadAllBytesAsync(selected.Path);
            using (var probe = new CoreClient())
            {
                await probe.OpenAsync(selected.Path, _masterPassword);
                await probe.LockAsync();
            }
            await SaveAsync(silent: true, syncAfterSave: false);
            // The session keeps the write gate across the restore and the core
            // reopen, so a concurrent sync write cannot land in between.
            await _vault.RestoreAsync(selectedBytes, Settings.BackupRetention,
                ReopenFromLocalAsync);
            MarkLocalDirty();
            ShowToast(Loc.T("Backup restored"), InfoBarSeverity.Success);
            if (IsSyncConfigured)
            {
                _ = SyncAsync();
            }
        }
        catch (CoreException ex) when (ex.Code == "VaultChanged")
        {
            ShowToast(Loc.T("The database was changed outside Keepassity. Reopen it " +
                            "before saving; nothing was overwritten."),
                InfoBarSeverity.Error);
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("Restore failed: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    // ---------------------------------------------------------------- unlock

    private async void ChooseDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(".kdbx");
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }
        if (string.Equals(Settings.LastDatabaseKind, "webdav",
                StringComparison.OrdinalIgnoreCase))
        {
            // Do not carry a previous WebDAV target over to an unrelated local
            // file: saving it would overwrite that remote database.
            Settings.WebDavUrl = null;
            Settings.WebDavUsername = null;
        }
        else if (!string.Equals(Settings.LastDatabasePath, file.Path,
                     StringComparison.OrdinalIgnoreCase))
        {
            // Switching to a different local database must not inherit the
            // previous database's WebDAV target either.
            Settings.WebDavUrl = null;
            Settings.WebDavUsername = null;
        }
        _dbPath = file.Path;
        _webDavSource = false;
        _sourceWebDavUrl = null;
        Settings.LastDatabaseKind = "local";
        Settings.LastWebDavUrl = null;
        Settings.LastWebDavUsername = null;
        _settingsStore.Save();
        UpdateDatabasePath();
        Unlock.ClearStatus();
    }

    private async void OpenFromWebDavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        string initialUrl = Settings.LastWebDavUrl ?? Settings.WebDavUrl ?? string.Empty;
        string initialUsername = Settings.LastWebDavUsername ?? Settings.WebDavUsername
            ?? string.Empty;
        var urlBox = new TextBox
        {
            Header = Loc.T("WebDAV file URL"),
            Text = initialUrl,
            PlaceholderText = "https://dav.jianguoyun.com/dav/keepassity/Keywords.kdbx",
        };
        var userBox = new TextBox
        {
            Header = Loc.T("WebDAV username"),
            Text = initialUsername,
        };
        var passwordBox = new PasswordBox
        {
            Header = Loc.T("WebDAV password"),
            PasswordRevealMode = PasswordRevealMode.Peek,
            PlaceholderText = Loc.T("Leave blank to use the stored password"),
        };
        var rememberBox = new CheckBox
        {
            Content = Loc.T("Remember password with Windows DPAPI"),
            IsChecked = true,
        };
        var errorText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        // SY-09: server presets that fill in the URL shape of the common
        // self-hosted WebDAV endpoints.
        var preset = new ComboBox
        {
            Header = Loc.T("Server preset"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        preset.Items.Add(Loc.T("Custom / paste a URL"));
        preset.Items.Add("Nextcloud / ownCloud");
        preset.Items.Add("Synology WebDAV");
        preset.Items.Add("Jianguoyun (Nutstore)");
        preset.SelectedIndex = 0;
        preset.SelectionChanged += (_, _) =>
        {
            string? template = preset.SelectedIndex switch
            {
                1 => "https://your-server/remote.php/dav/files/your-user/keepassity/Keywords.kdbx",
                2 => "https://your-server:5006/keepassity/Keywords.kdbx",
                3 => "https://dav.jianguoyun.com/dav/keepassity/Keywords.kdbx",
                _ => null,
            };
            if (template is null)
            {
                return;
            }
            urlBox.Text = template;
            urlBox.SelectionStart = urlBox.Text.Length;
            urlBox.Focus(FocusState.Programmatic);
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(preset);
        content.Children.Add(urlBox);
        content.Children.Add(userBox);
        content.Children.Add(passwordBox);
        content.Children.Add(rememberBox);
        content.Children.Add(errorText);
        content.Children.Add(new TextBlock
        {
            Text = Loc.TLong("Dialog.WebDavOpenBody",
                "The .kdbx file is downloaded to an encrypted local working copy. " +
                "Saves sync back to this URL; conflicts never overwrite silently. " +
                "Nextcloud/ownCloud accounts with two-factor authentication need an " +
                "app password, not the account password."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = Loc.T("Open from WebDAV"),
            Content = content,
            PrimaryButtonText = Loc.T("Open"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string url = urlBox.Text.Trim();
            string username = userBox.Text.Trim();
            string? error = null;
            if (!IsWebDavUrl(url))
            {
                error = Loc.T("Enter an https:// URL that points to the .kdbx file (Basic authentication requires TLS).");
            }
            else if (username.Length == 0)
            {
                error = Loc.T("Enter the WebDAV username.");
            }
            else if (passwordBox.Password.Length == 0 &&
                     _credentials.LoadPassword(url, username) is null)
            {
                error = Loc.T("Enter the WebDAV password (or use a URL whose credential is stored).");
            }
            if (error is null)
            {
                return;
            }
            errorText.Text = error;
            errorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        string remoteUrl = urlBox.Text.Trim();
        string remoteUsername = userBox.Text.Trim();
        string remotePassword = passwordBox.Password.Length > 0
            ? passwordBox.Password
            : _credentials.LoadPassword(remoteUrl, remoteUsername) ?? string.Empty;
        bool rememberPassword = rememberBox.IsChecked == true;
        if (remotePassword.Length == 0)
        {
            ShowUnlockError(Loc.T("Cannot open database"), Loc.T("Enter the WebDAV password."));
            return;
        }

        _busy = true;
        SetUnlockControlsEnabled(false);
        Unlock.ClearStatus();
        try
        {
            // Never overwrite a working copy that still holds unsynced edits:
            // open it now and let the normal sync/merge path reconcile after unlock.
            string workingPath = RemoteDatabaseStore.PathFor(remoteUrl, remoteUsername);
            SyncState pendingState = _syncStates.Load(remoteUrl, remoteUsername, workingPath);
            if (File.Exists(workingPath) && pendingState.LocalDirty)
            {
                ClearPendingRemoteDownload();
                if (!TryAcquireVaultLock(workingPath))
                {
                    ShowUnlockError(Loc.T("Cannot open database"),
                        Loc.T("The database is open in another Keepassity window. " +
                              "Close it there first."));
                    return;
                }
                if (rememberPassword)
                {
                    try
                    {
                        _credentials.SavePassword(remoteUrl, remoteUsername, remotePassword);
                    }
                    catch
                    {
                        // The pending-change path must not fail on credential storage.
                    }
                }
                ApplyWebDavSource(remoteUrl, remoteUsername, workingPath);
                Unlock.ShowStatus(Loc.T("Unsynced local changes"),
                    Loc.TLong("Unlock.UnsyncedChangesBody",
                        "The local working copy has changes that were not " +
                        "uploaded yet. Opening it; conflicts will be handled " +
                        "after unlock."),
                    UnlockStatusSeverity.Warning);
                MasterPassword.Focus(FocusState.Programmatic);
                return;
            }
            // Download into memory only. The working copy is replaced when the
            // master password opens these exact bytes (RemoteVaultOpener), so a
            // corrupted download or a wrong file can never clobber the vault.
            PendingRemoteDatabase pending;
            using (var dav = await CreateWebDavClientAsync(remoteUrl, remoteUsername, remotePassword))
            {
                pending = await _remoteOpener.DownloadAsync(dav, remoteUrl, remoteUsername,
                    Settings.BackupRetention);
            }
            if (rememberPassword)
            {
                _credentials.SavePassword(remoteUrl, remoteUsername, remotePassword);
            }
            if (!TryAcquireVaultLock(pending.WorkingCopyPath))
            {
                ShowUnlockError(Loc.T("Cannot open database"),
                    Loc.T("The database is open in another Keepassity window. " +
                          "Close it there first."));
                return;
            }
            ApplyWebDavSource(remoteUrl, remoteUsername, pending.WorkingCopyPath);
            _pendingRemoteDownload = pending;
            Unlock.SetPendingRemote(pending);
            Unlock.ShowStatus(Loc.T("Downloaded from WebDAV"),
                Loc.T("Enter the master password to unlock. Saves will sync back to this file."),
                UnlockStatusSeverity.Success);
            MasterPassword.Focus(FocusState.Programmatic);
        }
        catch (WebDavException ex) when (ex.StatusCode == 404)
        {
            ShowUnlockError(Loc.T("Cannot open database"),
                Loc.T("The remote database file was not found. Check the WebDAV file URL."));
        }
        catch (WebDavException ex)
        {
            ShowUnlockError(Loc.T("Cannot open database"), ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ClearPendingRemoteDownload();
            string workingPath = RemoteDatabaseStore.PathFor(remoteUrl, remoteUsername);
            if (File.Exists(workingPath))
            {
                if (!TryAcquireVaultLock(workingPath))
                {
                    ShowUnlockError(Loc.T("Cannot open database"),
                        Loc.T("The database is open in another Keepassity window. " +
                              "Close it there first."));
                    return;
                }
                if (rememberPassword)
                {
                    try
                    {
                        _credentials.SavePassword(remoteUrl, remoteUsername, remotePassword);
                    }
                    catch
                    {
                        // Offline fallback must not fail because of credential storage.
                    }
                }
                ApplyWebDavSource(remoteUrl, remoteUsername, workingPath);
                Unlock.ShowStatus(Loc.T("WebDAV unreachable"),
                    Loc.T("Opening the local working copy. Changes will sync when the network is back."),
                    UnlockStatusSeverity.Warning);
                MasterPassword.Focus(FocusState.Programmatic);
            }
            else
            {
                ShowUnlockError(Loc.T("Cannot open database"), ex.Message);
            }
        }
        catch (Exception ex)
        {
                ShowUnlockError(Loc.T("Cannot open database"), ex.Message);
        }
        finally
        {
            _busy = false;
            SetUnlockControlsEnabled(true);
        }
    }

    /// <summary>
    /// WebDAV URLs must use HTTPS: the password travels with Basic
    /// authentication, which must never cross a plain-text connection.
    /// </summary>
    private static bool IsWebDavUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
           uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Forget a downloaded WebDAV database that has not been promoted yet (the
    /// user switched database, went offline, or the dialog was reopened).
    /// </summary>
    private void ClearPendingRemoteDownload()
    {
        _pendingRemoteDownload = null;
        Unlock.SetPendingRemote(null);
    }

    private void ApplyWebDavSource(string url, string username, string workingPath)
    {
        _dbPath = workingPath;
        _webDavSource = true;
        _sourceWebDavUrl = url;
        Settings.LastDatabaseKind = "webdav";
        Settings.LastWebDavUrl = url;
        Settings.LastWebDavUsername = username;
        Settings.LastDatabasePath = workingPath;
        Settings.WebDavUrl = url;
        Settings.WebDavUsername = username;
        _settingsStore.Save();
        UpdateDatabasePath();
        RememberRecentDatabase();
    }

    private void ShowUnlockError(string title, string message)
    {
        Unlock.ShowStatus(title, message, UnlockStatusSeverity.Error, recovery: true);
        ShowUnlockFailureActions();
        Announce(Loc.T("{0}: {1}", title, message));
    }

    private void SetUnlockControlsEnabled(bool enabled)
    {
        Unlock.SetBusy(!enabled);
        ChooseDatabaseButton.IsEnabled = enabled;
        OpenFromWebDavButton.IsEnabled = enabled;
        NewDatabaseButton.IsEnabled = enabled;
    }

    // x:Bind functions keep view-only conversions (bool -> Visibility, view
    // model severity -> WinUI enum) in the view instead of leaking UI types
    // into the view model.
    private Visibility ToVisibility(bool visible)
        => visible ? Visibility.Visible : Visibility.Collapsed;

    private InfoBarSeverity ToInfoBarSeverity(UnlockStatusSeverity severity) => severity switch
    {
        UnlockStatusSeverity.Success => InfoBarSeverity.Success,
        UnlockStatusSeverity.Warning => InfoBarSeverity.Warning,
        UnlockStatusSeverity.Error => InfoBarSeverity.Error,
        _ => InfoBarSeverity.Informational,
    };

    // ------------------------------------------------- recent databases

    private void RememberRecentDatabase()
    {
        if (_dbPath is null)
        {
            return;
        }
        if (_webDavSource && string.IsNullOrWhiteSpace(_sourceWebDavUrl))
        {
            // A local working copy without a known remote URL is just a path.
            _recentDatabases.Remember(_dbPath, "local");
        }
        else
        {
            _recentDatabases.Remember(_dbPath, _webDavSource ? "webdav" : "local",
                _webDavSource ? _sourceWebDavUrl : null,
                _webDavSource ? Settings.WebDavUsername : null);
        }
        RefreshRecentDatabaseList();
    }

    private void RefreshRecentDatabaseList()
    {
        RecentDatabasesPanel.Children.Clear();
        IReadOnlyList<RecentDatabaseEntry> entries = _recentDatabases.Entries;
        foreach (RecentDatabaseEntry entry in entries)
        {
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            string secondary = entry.Kind == "webdav" &&
                               !string.IsNullOrWhiteSpace(entry.WebDavUrl)
                ? "WebDAV · " + entry.WebDavUrl
                : entry.Path;
            content.Children.Add(new TextBlock
            {
                Text = secondary,
                FontSize = 10,
                MaxWidth = 320,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
            var button = new Button
            {
                Content = content,
                Style = (Style)Root.Resources["ToolbarButton"],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
            };
            AutomationProperties.SetName(button,
                Loc.T("Open recent database {0}", entry.DisplayName));
            AutomationProperties.SetHelpText(button, secondary);
            button.Click += (_, _) => SelectRecentDatabase(entry);
            var menu = new MenuFlyout();
            var remove = new MenuFlyoutItem { Text = Loc.T("Remove from recent list") };
            remove.Click += (_, _) =>
            {
                _recentDatabases.Remove(entry.Path);
                RefreshRecentDatabaseList();
            };
            menu.Items.Add(remove);
            button.ContextFlyout = menu;
            RecentDatabasesPanel.Children.Add(button);
        }
        // The start page gets the list; when a database is already selected the
        // unlock form takes its place.
        RecentDatabasesSection.Visibility = _dbPath is null && entries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SelectRecentDatabase(RecentDatabaseEntry entry)
    {
        if (_busy)
        {
            return;
        }
        if (!File.Exists(entry.Path))
        {
            Unlock.ShowStatus(Loc.T("Database file not found"),
                Loc.TLong("Unlock.DatabaseMissingBody",
                    "The database file is no longer at this path. It may have " +
                    "moved or be on a drive that is not connected. Use " +
                    "\"Open local file…\" to locate it again."),
                UnlockStatusSeverity.Warning);
            AppLog.Warn("recent database path is missing");
            return;
        }
        bool webDav = string.Equals(entry.Kind, "webdav", StringComparison.OrdinalIgnoreCase) &&
                      !string.IsNullOrWhiteSpace(entry.WebDavUrl);
        _dbPath = entry.Path;
        _webDavSource = webDav;
        _sourceWebDavUrl = webDav ? entry.WebDavUrl : null;
        Settings.LastDatabasePath = entry.Path;
        Settings.LastDatabaseKind = webDav ? "webdav" : "local";
        Settings.LastWebDavUrl = webDav ? entry.WebDavUrl : null;
        Settings.LastWebDavUsername = webDav ? entry.WebDavUsername : null;
        // Keep the sync target consistent with the selected database: a local
        // file must never inherit another database's WebDAV target.
        Settings.WebDavUrl = webDav ? entry.WebDavUrl : null;
        Settings.WebDavUsername = webDav ? entry.WebDavUsername : null;
        _settingsStore.Save();
        Unlock.ClearStatus();
        UpdateDatabasePath();
        MasterPassword.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------ unlock recovery

    private string CurrentBackupDirectory()
    {
        if (_dbPath is null)
        {
            return ApplicationPaths.Root;
        }
        return BackupService.BackupDirectory(_dbPath);
    }

    private void ShowUnlockFailureActions()
    {
        string backupDirectory = CurrentBackupDirectory();
        UnlockFailureHint.Text = Loc.TLong("Unlock.FailureHintBody",
            "If the database will not open, export diagnostics (logs and " +
            "settings only, no vault content or passwords) and keep them " +
            "with the issue report. Timestamped backups are created before " +
            "each save; the newest ones can be restored from the backup " +
            "folder below.");
        UnlockBackupPathText.Text = Loc.T("Backups: {0}", backupDirectory);
        ToolTipService.SetToolTip(UnlockBackupFolderButton, backupDirectory);
        Unlock.ShowRecovery();
    }

    private void HideUnlockFailureActions()
    {
        Unlock.HideRecovery();
    }

    private async void UnlockDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = await DiagnosticsService.ExportAsync(Settings);
            AppLog.Info("unlock page: diagnostics exported");
            ShowToast(Loc.T("Diagnostics exported: {0}", path), InfoBarSeverity.Success);
            await TryOpenFolderAsync(Path.GetDirectoryName(path));
        }
        catch (Exception ex)
        {
            AppLog.Error("diagnostics export failed", ex);
            ShowToast(Loc.T("Diagnostics export failed: {0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    private async void UnlockBackupFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string directory = CurrentBackupDirectory();
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // A read-only location still gets its path shown below.
        }
        if (!await TryOpenFolderAsync(directory))
        {
            ShowToast(Loc.T("Backup folder: {0}", directory), InfoBarSeverity.Informational);
        }
    }

    private static async Task<bool> TryOpenFolderAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }
        try
        {
            StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(path);
            return await Launcher.LaunchFolderAsync(folder);
        }
        catch
        {
            return false;
        }
    }

    private async void NewDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var nameBox = new TextBox
        {
            Header = Loc.T("Database name"),
            Text = "keepassity",
        };
        var targetChoices = new RadioButtons
        {
            Header = Loc.T("Save to"),
            ItemsSource = new[] { Loc.T("Local file"), "WebDAV" },
            SelectedIndex = 0,
        };
        var pathBox = new TextBox
        {
            Header = Loc.T("Save as"),
            Text = DefaultNewDatabasePath(),
        };
        var browse = new Button
        {
            Content = Loc.T("Browse…"),
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var pathRow = new Grid();
        pathRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = new GridLength(1, GridUnitType.Star),
        });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = GridLength.Auto,
        });
        Grid.SetColumn(pathBox, 0);
        Grid.SetColumn(browse, 1);
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browse);
        var localPanel = new StackPanel { Spacing = 12 };
        localPanel.Children.Add(pathRow);
        var urlBox = new TextBox
        {
            Header = Loc.T("WebDAV file URL"),
            Text = Settings.LastWebDavUrl ?? Settings.WebDavUrl ?? string.Empty,
            PlaceholderText = "https://dav.jianguoyun.com/dav/keepassity/Keywords.kdbx",
        };
        var userBox = new TextBox
        {
            Header = Loc.T("WebDAV username"),
            Text = Settings.LastWebDavUsername ?? Settings.WebDavUsername ?? string.Empty,
        };
        var remotePasswordBox = new PasswordBox
        {
            Header = Loc.T("WebDAV password"),
            PasswordRevealMode = PasswordRevealMode.Peek,
            PlaceholderText = Loc.T("Leave blank to use the stored password"),
        };
        var rememberBox = new CheckBox
        {
            Content = Loc.T("Remember password with Windows DPAPI"),
            IsChecked = true,
        };
        var webdavPanel = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        webdavPanel.Children.Add(urlBox);
        webdavPanel.Children.Add(userBox);
        webdavPanel.Children.Add(remotePasswordBox);
        webdavPanel.Children.Add(rememberBox);
        webdavPanel.Children.Add(new TextBlock
        {
            Text = Loc.TLong("Dialog.NewDatabaseWebDavBody",
                "The new database is uploaded to this URL first. An existing remote " +
                "file is never overwritten."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var passwordBox = new PasswordBox
        {
            Header = Loc.T("Master password"),
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var confirmBox = new PasswordBox
        {
            Header = Loc.T("Confirm master password"),
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var errorText = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(nameBox);
        content.Children.Add(targetChoices);
        content.Children.Add(localPanel);
        content.Children.Add(webdavPanel);
        content.Children.Add(passwordBox);
        content.Children.Add(confirmBox);
        content.Children.Add(errorText);
        content.Children.Add(new TextBlock
        {
            Text = Loc.TLong("Dialog.NewDatabaseBody",
                "Creates an empty KDBX 4.0 database with a General group. " +
                "The master password is not stored."),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = Loc.T("New database"),
            Content = content,
            PrimaryButtonText = Loc.T("Create"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        targetChoices.SelectionChanged += (_, _) =>
        {
            bool webdav = targetChoices.SelectedIndex == 1;
            localPanel.Visibility = webdav ? Visibility.Collapsed : Visibility.Visible;
            webdavPanel.Visibility = webdav ? Visibility.Visible : Visibility.Collapsed;
        };
        browse.Click += async (_, _) =>
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(this));
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }
            string stem = string.IsNullOrWhiteSpace(nameBox.Text) ? "keepassity" : nameBox.Text.Trim();
            pathBox.Text = Path.Combine(folder.Path, stem + ".kdbx");
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            string error = ValidateNewDatabaseNameAndPassword(nameBox.Text,
                passwordBox.Password, confirmBox.Password);
            if (error.Length == 0 && targetChoices.SelectedIndex == 0)
            {
                error = ValidateLocalDatabasePath(pathBox.Text);
            }
            if (error.Length == 0 && targetChoices.SelectedIndex == 1)
            {
                string url = urlBox.Text.Trim();
                string username = userBox.Text.Trim();
                if (!IsWebDavUrl(url))
                {
                    error = Loc.T("Enter an absolute http(s) URL for the new database.");
                }
                else if (username.Length == 0)
                {
                    error = Loc.T("Enter the WebDAV username.");
                }
                else if (remotePasswordBox.Password.Length == 0 &&
                         _credentials.LoadPassword(url, username) is null)
                {
                    error = Loc.T("Enter the WebDAV password (or use a URL whose credential is stored).");
                }
            }
            if (error.Length == 0)
            {
                return;
            }
            args.Cancel = true;
            errorText.Text = error;
            errorText.Visibility = Visibility.Visible;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        string databaseName = nameBox.Text.Trim();
        string password = passwordBox.Password;
        bool useWebDav = targetChoices.SelectedIndex == 1;
        string path = string.Empty;
        if (ValidateNewDatabaseNameAndPassword(databaseName, password,
                confirmBox.Password).Length > 0)
        {
            return;
        }
        if (!useWebDav)
        {
            try
            {
                path = Path.GetFullPath(pathBox.Text.Trim());
            }
            catch
            {
                return;
            }
            if (ValidateLocalDatabasePath(path).Length > 0)
            {
                return;
            }
        }
        string remoteUrl = urlBox.Text.Trim();
        string remoteUsername = userBox.Text.Trim();
        string remotePassword = remotePasswordBox.Password.Length > 0
            ? remotePasswordBox.Password
            : _credentials.LoadPassword(remoteUrl, remoteUsername) ?? string.Empty;
        bool rememberPassword = rememberBox.IsChecked == true;

        _busy = true;
        SetUnlockControlsEnabled(false);
        Unlock.ClearStatus();
        try
        {
            byte[] bytes = await _core.CreateDatabaseAsync(databaseName, password);
            if (useWebDav)
            {
                string workingPath = RemoteDatabaseStore.PathFor(remoteUrl, remoteUsername);
                string? etag;
                using (var dav = await CreateWebDavClientAsync(remoteUrl, remoteUsername, remotePassword))
                {
                    try
                    {
                        etag = await dav.UploadAsync(bytes, ifMatch: null);
                    }
                    catch (WebDavException ex) when (ex.Conflict)
                    {
                        throw new InvalidOperationException(Loc.T(
                            "The remote file already exists. Choose another URL or open it " +
                            "from WebDAV."));
                    }
                }
                if (rememberPassword)
                {
                    _credentials.SavePassword(remoteUrl, remoteUsername, remotePassword);
                }
                // Claim the working copy before writing it: another window may
                // own the same deterministic path even though this database is
                // new to this process.
                if (!TryAcquireVaultLock(workingPath))
                {
                    throw new InvalidOperationException(Loc.T(
                        "The database is open in another Keepassity window. " +
                        "Close it there first."));
                }
                await WriteLocalBytesAsync(workingPath, bytes);
                ApplyWebDavSource(remoteUrl, remoteUsername, workingPath);
                SyncState state = _syncStates.Load(remoteUrl, remoteUsername, workingPath);
                state.Etag = etag;
                state.Sha256 = Sha256Hex(bytes);
                state.Size = bytes.Length;
                state.RemoteModifiedUnix = null;
                state.LocalDirty = false;
                state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                _syncStates.Save(remoteUrl, remoteUsername, workingPath, state);
                RememberVaultFingerprint(workingPath);
                await _core.OpenAsync(workingPath, password);
                _masterPassword = password;
                _selectedGroupUuid = null;
                await ReloadVaultAsync(null);
                UpdateDatabasePath();
                RememberRecentDatabase();
                ShowMain();
                MasterPassword.Password = string.Empty;
                SetSyncStatus("Synced");
                ShowToast(Loc.T("Database created on WebDAV"), InfoBarSeverity.Success);
            }
            else
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                if (File.Exists(path))
                {
                    throw new InvalidOperationException(
                        Loc.T("That file already exists. Choose another path."));
                }
                string temp = path + ".keepassity-new-tmp";
                await File.WriteAllBytesAsync(temp, bytes);
                File.Move(temp, path);
                _dbPath = path;
                _webDavSource = false;
                _sourceWebDavUrl = null;
                if (!TryAcquireVaultLock(path))
                {
                    throw new InvalidOperationException(Loc.T(
                        "The database is open in another Keepassity window. " +
                        "Close it there first."));
                }
                RememberVaultFingerprint(path);
                await _core.OpenAsync(path, password);
                _masterPassword = password;
                _selectedGroupUuid = null;
                Settings.LastDatabaseKind = "local";
                Settings.LastWebDavUrl = null;
                Settings.LastWebDavUsername = null;
                Settings.LastDatabasePath = path;
                // A brand-new database starts without a sync target; carrying over
                // the previous WebDAV URL could overwrite that remote file.
                Settings.WebDavUrl = null;
                Settings.WebDavUsername = null;
                _settingsStore.Save();
                await ReloadVaultAsync(null);
                UpdateDatabasePath();
                RememberRecentDatabase();
                ShowMain();
                MasterPassword.Password = string.Empty;
                SetSyncStatus("Local only");
                ShowToast(Loc.T("Database created"), InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            Unlock.ShowStatus(Loc.T("Cannot create database"), ex.Message,
                UnlockStatusSeverity.Error, recovery: true);
        }
        finally
        {
            _busy = false;
            SetUnlockControlsEnabled(true);
            passwordBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
            remotePasswordBox.Password = string.Empty;
        }
    }

    private static string ValidateNewDatabaseNameAndPassword(string name, string password,
        string confirm)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Loc.T("Enter a database name.");
        }
        if (password.Length == 0)
        {
            return Loc.T("Enter a master password.");
        }
        if (password != confirm)
        {
            return Loc.T("The master passwords do not match.");
        }
        return string.Empty;
    }

    private static string ValidateLocalDatabasePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Loc.T("Choose where to save the database.");
        }
        if (!path.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase))
        {
            return Loc.T("The file name must end in .kdbx.");
        }
        return string.Empty;
    }
    private static string DefaultNewDatabasePath()
    {
        string directory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(directory))
        {
            directory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        string path = Path.Combine(directory, "keepassity.kdbx");
        int suffix = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"keepassity-{suffix++}.kdbx");
        }
        return path;
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e)
        => await UnlockAsync();

    private async void MasterPassword_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            await UnlockAsync();
        }
    }

    private async Task UnlockAsync()
    {
        if (_busy)
        {
            return;
        }
        Unlock.SetDatabasePath(_dbPath);
        if (_dbPath is not null && MasterPassword.Password.Length == 0)
        {
            MasterPassword.Focus(FocusState.Programmatic);
        }
        if (_dbPath is not null)
        {
            if (!TryAcquireVaultLock(_dbPath))
            {
                Unlock.ShowStatus(Loc.T("Cannot open database"),
                    Loc.T("The database is open in another Keepassity window. " +
                          "Close it there first."),
                    UnlockStatusSeverity.Error);
                Announce(Loc.T("Unlock failed: {0}", Unlock.StatusMessage));
                return;
            }
            int cleaned = AtomicFile.CleanupResidualTemps(_dbPath);
            if (cleaned > 0)
            {
                AppLog.Info($"removed {cleaned} residual save temporary file(s)");
            }
            RememberVaultFingerprint();
        }

        _busy = true;
        bool unlocked;
        try
        {
            unlocked = await Unlock.UnlockAsync(MasterPassword.Password);
        }
        finally
        {
            _busy = false;
        }
        if (!unlocked)
        {
            ReleaseVaultLock();
            Announce(Loc.T("Unlock failed: {0}", Unlock.StatusMessage));
            return;
        }

        string password = MasterPassword.Password;
        _masterPassword = password;
        // A pending WebDAV download was only promoted to the working copy by the
        // successful unlock; re-capture the file that is now on disk and forget
        // the in-memory copy.
        ClearPendingRemoteDownload();
        RememberVaultFingerprint();
        _selectedGroupUuid = null;
        if (_dbPath is not null)
        {
            Settings.LastDatabasePath = _dbPath;
            _settingsStore.Save();
        }
        await ReloadVaultAsync(null);
        Unlock.ClearStatus();
        AppLog.Info("vault unlocked");
        Announce(Loc.T("Vault unlocked"));
        _trayIcon?.SetVaultState(true, DisplayVaultName());
        RememberRecentDatabase();
        ShowMain();
        MasterPassword.Password = string.Empty;
        _ = SyncAsync(startup: true);
    }

    private void ShowMain()
    {
        UnlockRoot.Visibility = Visibility.Collapsed;
        MainRoot.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        HamburgerButton.Visibility = Visibility.Visible;
        VaultNameText.Text = DisplayVaultName();
        _totpTimer.Start();
        _autoLockTimer.Start();
        TouchActivity();
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        AnimateWorkspace();
        // Wait for the workspace to be laid out before taking focus, otherwise
        // the first focusable title-bar control keeps the focus visual.
        DispatcherQueue.TryEnqueue(() => EntryList.Focus(FocusState.Programmatic));
    }

    // ------------------------------------------------------------ vault data

    private async Task ReloadVaultAsync(string? selectEntryUuid, bool preserveScroll = true)
    {
        List<GroupDto> groups = await _core.ListGroupsAsync();
        List<EntrySummaryDto> allEntries = await _core.ListEntriesAsync();

        // Capture the wheel position after the awaits, right before the list is
        // rebuilt: the core IPC calls can take long enough that a position
        // captured earlier would drag the user back to where they were before the
        // reload (a merge reopen lands about a second after a save, so this window
        // is exactly where the list used to jump back to the drag-time position).
        double entryScrollOffset = preserveScroll
            ? FindDescendant<ScrollViewer>(EntryList)?.VerticalOffset ?? 0
            : 0;

        _groupDtos.Clear();
        _selectableGroups.Clear();
        _groupNames.Clear();
        _recycleBinGroupUuid = null;
        _rootGroupUuid = groups.FirstOrDefault(group => group.ParentUuid is null)?.Uuid;
        foreach (GroupDto group in groups)
        {
            _groupDtos.Add(group);
            string name = string.IsNullOrWhiteSpace(group.Name) ? Loc.T("Vault") : group.Name;
            _groupNames[group.Uuid] = name;
            if (_recycleBinGroupUuid is null && IsRecycleBinGroup(name))
            {
                _recycleBinGroupUuid = group.Uuid;
            }
            if (group.Uuid != _rootGroupUuid && !string.IsNullOrWhiteSpace(group.Name))
            {
                _selectableGroups.Add(group);
            }
        }
        if (_selectableGroups.Count == 0 && _groupDtos.Count > 0)
        {
            _selectableGroups.AddRange(_groupDtos);
        }

        Groups.Clear();
        int activeEntryCount = allEntries.Count(entry =>
            _recycleBinGroupUuid is null || entry.GroupUuid != _recycleBinGroupUuid);
        foreach (GroupNavItem item in GroupNavigationProjection.Build(
                     _groupDtos.Select(group =>
                         new GroupNavigationInput(group.Uuid, group.Name)),
                     _rootGroupUuid,
                     Loc.T("All entries"),
                     activeEntryCount,
                     uuid => allEntries.Count(entry => entry.GroupUuid == uuid),
                     IsRecycleBinGroup,
                     IsTemplatesGroup))
        {
            Groups.Add(item);
        }
        PopulateNavigation(_selectedGroupUuid);

        string title = _selectedGroupUuid is null
            ? Loc.T("All entries")
            : _groupNames.GetValueOrDefault(_selectedGroupUuid, Loc.T("Entries"));
        List<EntrySummaryDto> visible = _selectedGroupUuid is null
            ? allEntries
                .Where(entry => _recycleBinGroupUuid is null ||
                                entry.GroupUuid != _recycleBinGroupUuid)
                .ToList()
            : allEntries.Where(entry => entry.GroupUuid == _selectedGroupUuid).ToList();
        string? previousSelection = (EntryList.SelectedItem as EntryRowPresenter)?.Uuid;
        bool listRebuilt = PopulateEntries(visible, title, selectEntryUuid ?? previousSelection);
        if (!listRebuilt && EntryList.SelectedItem is EntryRowPresenter stillSelected &&
            string.Equals(stillSelected.Uuid, previousSelection, StringComparison.Ordinal))
        {
            // Saving a password-only edit changes nothing the middle column shows,
            // so the list keeps its rows, no selection event fires and the detail
            // pane would keep the old entry (copy/reveal returned the old secret
            // until the user re-selected a row). A remote merge can do the same to
            // a selected entry. Refresh the detail explicitly, without replaying
            // the entry animation.
            await ShowEntryAsync(stillSelected, animate: false);
        }

        // Restoring the wheel position after a reload is what keeps a merge or a
        // metadata change from jumping the list back to the top. A zero offset is
        // restored too: a rebuild that selects a row can scroll the list away from
        // the top, and the position the user actually had must win.
        if (preserveScroll)
        {
            RestoreEntryScrollOffset(entryScrollOffset);
        }
    }

    private static T? FindDescendant<T>(DependencyObject? root) where T : DependencyObject
    {
        if (root is null)
        {
            return null;
        }
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }
            T? nested = FindDescendant<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private void PopulateNavigation(string? selectedUuid)
    {
        _suppressNav = true;
        NavView.MenuItems.Clear();
        NavigationViewItem? selectedItem = null;
        NavigationViewItem? first = null;
        foreach (GroupNavItem group in Groups)
        {
            var item = new NavigationViewItem
            {
                Content = group.Name,
                Tag = group.Uuid ?? "all",
                Icon = new FontIcon { Glyph = group.Glyph },
                ContextFlyout = BuildGroupContextMenu(group),
            };
            // Real groups and the virtual "All entries" row are drop targets.
            // Dropping on "All entries" moves the entry to the vault root group,
            // i.e. back out of the folder to the top level.
            if (group.Uuid is not null || !string.IsNullOrEmpty(_rootGroupUuid))
            {
                item.AllowDrop = true;
                item.DragOver += GroupNavItem_DragOver;
                item.DragLeave += GroupNavItem_DragLeave;
                item.Drop += GroupNavItem_Drop;
            }
            if (group.CountValue > 0 &&
                !IsRecycleBinGroup(group.Name) &&
                !IsTemplatesGroup(group.Name))
            {
                item.InfoBadge = new InfoBadge { Value = group.CountValue };
            }
            NavView.MenuItems.Add(item);
            first ??= item;
            if ((selectedUuid is null && group.Uuid is null) ||
                (selectedUuid is not null && group.Uuid == selectedUuid))
            {
                selectedItem = item;
            }
        }
        NavView.SelectedItem = selectedItem ?? first;
        _suppressNav = false;
    }

    private void SelectNavItem(NavigationViewItem? item)
    {
        bool previousSuppress = _suppressNav;
        _suppressNav = true;
        NavView.SelectedItem = item;
        _suppressNav = previousSuppress;
    }

    private void SetSettingsHighlight(bool highlighted)
    {
        if (highlighted)
        {
            SettingsNavItem.Background =
                (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        }
        else
        {
            SettingsNavItem.ClearValue(Control.BackgroundProperty);
        }
    }

    private void RestoreGroupNavSelection()
    {
        string tag = _selectedGroupUuid ?? "all";
        NavigationViewItem? item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => (candidate.Tag as string) == tag);
        item ??= NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        if (item is not null)
        {
            SelectNavItem(item);
        }
    }

    private static bool IsRecycleBinGroup(string name)
    {
        string value = name.Trim();
        return value.Equals("回收站", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Recycle Bin", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("RecycleBin", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemplatesGroup(string name)
    {
        string value = name.Trim();
        return value.Equals("模板", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Templates", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Template", StringComparison.OrdinalIgnoreCase);
    }

    private MenuFlyout BuildGroupContextMenu(GroupNavItem group)
    {
        var flyout = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> action,
            bool enabled = true, bool danger = false)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontSize = 12 },
                IsEnabled = enabled,
            };
            if (danger)
            {
                item.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
            item.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    ShowToast(ex.Message, InfoBarSeverity.Error);
                }
            };
            flyout.Items.Add(item);
        }

        Add(Loc.T("New group…"), "\uE710", CreateGroupPromptAsync,
            enabled: !string.IsNullOrEmpty(_rootGroupUuid));
        if (group.Uuid is null)
        {
            Add(Loc.T("New entry"), "\uE710", () =>
            {
                NewEntryButton_Click(this, new RoutedEventArgs());
                return Task.CompletedTask;
            });
            return flyout;
        }

        bool recycleBin = IsRecycleBinGroup(group.Name);
        if (!recycleBin)
        {
            Add(Loc.T("New entry here"), "\uE710",
                () => NewEntryInGroupAsync(group.Uuid!));
        }
        if (recycleBin)
        {
            return flyout;
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(Loc.T("Rename group…"), "\uE70F", () => RenameGroupPromptAsync(group));
        Add(Loc.T("Delete group"), "\uE74D", () => DeleteGroupAsync(group), danger: true);
        return flyout;
    }

    private async Task CreateGroupPromptAsync()
    {
        if (string.IsNullOrEmpty(_rootGroupUuid))
        {
            return;
        }
        string? name = await PromptForNameAsync(Loc.T("New group"), Loc.T("Group name"), string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        string uuid = await _core.CreateGroupAsync(_rootGroupUuid, name);
        await SaveAsync();
        _selectedGroupUuid = uuid;
        await ReloadVaultAsync(null, preserveScroll: false);
        ShowToast(Loc.T("Group \"{0}\" created", name), InfoBarSeverity.Success);
    }

    private async Task RenameGroupPromptAsync(GroupNavItem group)
    {
        if (group.Uuid is null)
        {
            return;
        }
        string? name = await PromptForNameAsync(Loc.T("Rename group"), Loc.T("Group name"), group.Name);
        if (string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, group.Name, StringComparison.Ordinal))
        {
            return;
        }
        await _core.RenameGroupAsync(group.Uuid, name);
        await SaveAsync();
        await ReloadVaultAsync(_selectedGroupUuid);
        ShowToast(Loc.T("Group renamed"), InfoBarSeverity.Success);
    }

    private async Task DeleteGroupAsync(GroupNavItem group)
    {
        if (group.Uuid is null)
        {
            return;
        }
        bool hasEntries = group.CountValue > 0;
        var dialog = new ContentDialog
        {
            Title = hasEntries
                ? Loc.T("Delete group and contents?")
                : Loc.T("Delete group?"),
            Content = hasEntries
                ? Loc.T("\"{0}\" and everything inside it will be moved to the " +
                        "Recycle Bin when it is enabled, otherwise deleted " +
                        "permanently. This cannot be undone.", group.Name)
                : Loc.T("The empty group \"{0}\" will be removed.", group.Name),
            PrimaryButtonText = Loc.T("Delete"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        bool recycled = await _core.DeleteGroupAsync(group.Uuid);
        if (_selectedGroupUuid == group.Uuid)
        {
            _selectedGroupUuid = null;
        }
        await SaveAsync();
        await ReloadVaultAsync(null, preserveScroll: false);
        ShowToast(recycled
                ? Loc.T("Group moved to the Recycle Bin")
                : Loc.T("Group deleted"),
            InfoBarSeverity.Success);
    }

    private async Task<string?> PromptForNameAsync(string title, string label,
        string initial)
    {
        var box = new TextBox
        {
            Header = label,
            Text = initial,
        };
        var dialog = new ContentDialog
        {
            Title = title,
            Content = box,
            PrimaryButtonText = initial.Length == 0 ? Loc.T("Create") : Loc.T("Rename"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary
            ? box.Text.Trim()
            : null;
    }

    private async Task NewEntryInGroupAsync(string groupUuid)
    {
        _selectedGroupUuid = groupUuid;
        NavigationViewItem? item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => (candidate.Tag as string) == groupUuid);
        if (item is not null)
        {
            NavView.SelectedItem = item;
        }
        NewEntryButton_Click(this, new RoutedEventArgs());
        await Task.CompletedTask;
    }

    /// <summary>
    /// Resolves a navigation drop target to the group an entry would move into.
    /// Real group rows use their own uuid; the virtual "All entries" row maps to
    /// the vault root group, which is how an entry leaves a folder again.
    /// Returns null when the row cannot take drops.
    /// </summary>
    private string? ResolveDropTargetGroupUuid(NavigationViewItem item)
    {
        if (item.Tag is not string tag)
        {
            return null;
        }
        return tag == "all" ? _rootGroupUuid : tag;
    }

    private static bool IsTopLevelDropTarget(NavigationViewItem item)
        => (item.Tag as string) == "all";

    private void GroupNavItem_DragOver(object sender, DragEventArgs e)
    {
        if (sender is not NavigationViewItem item ||
            !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }
        string? targetGroupUuid = ResolveDropTargetGroupUuid(item);
        if (string.IsNullOrEmpty(targetGroupUuid))
        {
            return;
        }
        bool topLevel = IsTopLevelDropTarget(item);
        if (_draggedEntryGroupUuid is not null && _draggedEntryGroupUuid == targetGroupUuid)
        {
            // The row already lives in this group (or at the top level). Refuse
            // the drop here: the move path removes the row from the current group
            // view and, since the group does not change, never puts it back - the
            // row would be ejected for nothing. A hint explains the refusal.
            ClearGroupDragHighlight(item);
            e.AcceptedOperation = DataPackageOperation.None;
            e.DragUIOverride.Caption = topLevel
                ? Loc.T("Already at the top level")
                : Loc.T("Already in {0}", item.Content ?? string.Empty);
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
            if (!_dragSameGroupHintShown)
            {
                _dragSameGroupHintShown = true;
                string draggedTitle = _draggedEntryTitle ?? Loc.T("The entry");
                ShowToast(topLevel
                        ? Loc.T("\"{0}\" is already at the top level — nothing to move.",
                            draggedTitle)
                        : Loc.T("\"{0}\" is already in {1} — nothing to move.",
                            draggedTitle, item.Content ?? string.Empty),
                    InfoBarSeverity.Informational);
            }
            e.Handled = true;
            return;
        }
        item.Background =
            (Brush)Application.Current.Resources["AccentFillColorSecondaryBrush"];
        item.BorderBrush =
            (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        item.BorderThickness = new Thickness(1);
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = topLevel
            ? Loc.T("Move to the top level")
            : Loc.T("Move to {0}", item.Content ?? string.Empty);
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = false;
        e.Handled = true;
    }

    private void GroupNavItem_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is NavigationViewItem item)
        {
            ClearGroupDragHighlight(item);
        }
    }

    private static void ClearGroupDragHighlight(NavigationViewItem item)
    {
        item.ClearValue(Control.BackgroundProperty);
        item.ClearValue(Control.BorderBrushProperty);
        item.ClearValue(Control.BorderThicknessProperty);
    }

    private async void GroupNavItem_Drop(object sender, DragEventArgs e)
    {
        if (sender is not NavigationViewItem item ||
            !e.DataView.Contains(StandardDataFormats.Text))
        {
            return;
        }
        string? targetGroupUuid = ResolveDropTargetGroupUuid(item);
        if (string.IsNullOrEmpty(targetGroupUuid))
        {
            return;
        }
        bool topLevel = IsTopLevelDropTarget(item);
        string targetLabel = topLevel
            ? Loc.T("the top level")
            : item.Content as string ?? Loc.T("the group");
        e.Handled = true;
        ClearGroupDragHighlight(item);
        string entryUuid = (await e.DataView.GetTextAsync()).Trim();
        if (entryUuid.Length == 0)
        {
            return;
        }
        EntryRowPresenter? movedItem = Entries.FirstOrDefault(entry => entry.Uuid == entryUuid);
        string? sourceGroupUuid = movedItem?.GroupUuid;
        if (sourceGroupUuid is not null && sourceGroupUuid == targetGroupUuid)
        {
            // Defensive: DragOver already refuses this drop, but a stale or
            // programmatic drop must not turn into a pointless save + ejection.
            string movedTitle = movedItem?.Title ?? Loc.T("The entry");
            ShowToast(topLevel
                    ? Loc.T("\"{0}\" is already at the top level — nothing to move.",
                        movedTitle)
                    : Loc.T("\"{0}\" is already in {1} — nothing to move.",
                        movedTitle, targetLabel),
                InfoBarSeverity.Informational);
            return;
        }
        try
        {
            await _core.MoveEntryAsync(entryUuid, targetGroupUuid);
            await SaveAsync();
            // Capture the wheel position right before the list changes. The move
            // and the save (including its sync/merge tail) take long enough that
            // a position captured before them is stale: restoring it afterwards
            // drags the user back to where they were when they dropped.
            double scrollOffset = CaptureEntryScrollOffset();
            // Update only the affected row(s); a full reload would reset the
            // scroll position for a move that usually does not change the view.
            ApplyEntryMovedLocally(entryUuid, sourceGroupUuid, targetGroupUuid,
                item.Content as string ??
                _groupNames.GetValueOrDefault(targetGroupUuid, Loc.T("the group")));
            RestoreEntryScrollOffset(scrollOffset);
            ShowToast(Loc.T("Moved to {0}", targetLabel), InfoBarSeverity.Success);
            // Test-only: reproduce the delayed WebDAV merge reopen that lands a
            // moment after a move, to check that it does not steal the scroll.
            if (int.TryParse(TestHooks.EnvironmentValue("KEEPASSITY_RELOAD_AFTER_DRAG_MS"),
                    out int reloadDelayMs) && reloadDelayMs > 0)
            {
                _ = Task.Delay(reloadDelayMs).ContinueWith(_ => DispatcherQueue.TryEnqueue(
                    () => _ = ReopenFromLocalAsync()));
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static ScrollViewer? FindEntryScrollViewer(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }
            ScrollViewer? nested = FindEntryScrollViewer(child);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }

    private double CaptureEntryScrollOffset()
        => FindEntryScrollViewer(EntryList)?.VerticalOffset ?? 0;

    private void RestoreEntryScrollOffset(double offset)
    {
        void Apply()
        {
            ScrollViewer? scrollViewer = FindEntryScrollViewer(EntryList);
            if (scrollViewer is null)
            {
                return;
            }
            if (Math.Abs(scrollViewer.VerticalOffset - offset) > 0.5)
            {
                scrollViewer.ChangeView(null, offset, null, true);
            }
        }
        // Two deferred passes: the first runs after the rebuild, the second after
        // the ListView's own selection/layout work, which may scroll a selected
        // row into view and would otherwise win over a single restore.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            EntryList.UpdateLayout();
            Apply();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Apply);
        });
    }

    /// <summary>
    /// Rebuilds the middle column only when its visible content actually changed.
    /// Merges and moves should not make the list jump back to the top, so an
    /// identical entry list keeps the existing rows (and their scroll offset);
    /// when a rebuild is needed the previous selection is kept and the caller
    /// restores the wheel position afterwards.
    /// </summary>
    /// <param name="selectUuid">Row to select after a rebuild (previous selection when null).</param>
    /// <returns><see langword="true"/> when the list was rebuilt.</returns>
    private bool PopulateEntries(IEnumerable<EntrySummaryDto> entries, string title, string? selectUuid = null)
    {
        List<EntryRowInput> rows = entries
            .Select(entry => new EntryRowInput(
                entry.Uuid,
                entry.GroupUuid,
                _groupNames.GetValueOrDefault(entry.GroupUuid, Loc.T("Vault")),
                entry.Title,
                entry.Username,
                entry.Url,
                entry.HasTotp,
                entry.ModifiedUnix))
            .ToList();
        List<EntryRowInput> visible =
            EntryListProjection.Visible(rows, _entrySort, _onlyTotp);

        string fingerprint = EntryListProjection.Fingerprint(visible, title);
        if (fingerprint == _lastEntryFingerprint && Entries.Count == visible.Count)
        {
            // Same rows in the same order: keep the items (and the scroll offset).
            CurrentGroupTitle.Text = title;
            UpdateEntryCountText();
            if (selectUuid is not null)
            {
                SelectEntryRow(selectUuid);
            }
            return false;
        }

        string? keepUuid = selectUuid ?? (EntryList.SelectedItem as EntryRowPresenter)?.Uuid;
        Entries.Clear();
        foreach (EntryRowInput row in visible)
        {
            Entries.Add(new EntryRowPresenter(row));
        }
        _lastEntryFingerprint = fingerprint;
        CurrentGroupTitle.Text = title;
        UpdateEntryCountText();
        EmptyListHint.Text = _onlyTotp
            ? Loc.T("No entries with TOTP in this view. Clear the filter to see all entries.")
            : Loc.T("Create the first entry with New.");
        if (keepUuid is not null)
        {
            SelectEntryRow(keepUuid);
        }
        if (!ReferenceEquals(EntryList.SelectedItem, null))
        {
            return true;
        }
        if (Entries.Count > 0)
        {
            EntryList.SelectedIndex = 0;
        }
        else
        {
            EntryList.SelectedIndex = -1;
            ClearDetail();
        }
        return true;
    }

    private void UpdateEntryCountText()
    {
        string count = Entries.Count == 1
            ? Loc.T("{0} entry", Entries.Count)
            : Loc.T("{0} entries", Entries.Count);
        EntryCountText.Text = count +
                              (_onlyTotp ? " · " + Loc.T("TOTP only") : string.Empty);
        EmptyListState.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyListHint()
    {
        EmptyListHint.Text = _onlyTotp
            ? Loc.T("No entries with TOTP in this view. Clear the filter to see all entries.")
            : Loc.T("Create the first entry with New.");
    }

    private void SelectEntryRow(string uuid)
    {
        EntryRowPresenter? item = Entries.FirstOrDefault(entry => entry.Uuid == uuid);
        if (item is not null && !ReferenceEquals(EntryList.SelectedItem, item))
        {
            EntryList.SelectedItem = item;
        }
    }

    /// <summary>
    /// Applies a successful drag-move to the middle column in place instead of
    /// reloading the whole vault: the moved row either keeps its position (All
    /// entries / search view, group label updated) or is removed from the group
    /// view it just left. Neither path touches the scroll position.
    /// </summary>
    private void ApplyEntryMovedLocally(string entryUuid, string? sourceGroupUuid,
        string targetGroupUuid, string targetGroupName)
    {
        if (sourceGroupUuid is not null && sourceGroupUuid == targetGroupUuid)
        {
            // Nothing moved: removing the row would drop it from the group view
            // that still contains it (it is only put back by a later reload).
            return;
        }
        string targetName = _groupNames.GetValueOrDefault(targetGroupUuid, targetGroupName);
        EntryRowPresenter? item = Entries.FirstOrDefault(entry => entry.Uuid == entryUuid);
        if (item is not null)
        {
            bool searchActive = SearchBox.Text.Trim().Length > 0;
            if (_selectedGroupUuid is null || searchActive)
            {
                item.UpdateGroup(targetGroupUuid, targetName);
            }
            else if (sourceGroupUuid == _selectedGroupUuid)
            {
                int index = Entries.IndexOf(item);
                bool wasSelected = ReferenceEquals(EntryList.SelectedItem, item);
                Entries.Remove(item);
                if (wasSelected && Entries.Count > 0)
                {
                    EntryList.SelectedIndex = Math.Min(index, Entries.Count - 1);
                }
                else if (Entries.Count == 0)
                {
                    EntryList.SelectedIndex = -1;
                    ClearDetail();
                }
                UpdateEntryCountText();
            }
        }
        if (_currentFull is not null && _currentFull.Uuid == entryUuid)
        {
            _currentFull.GroupUuid = targetGroupUuid;
        }
        if (sourceGroupUuid != targetGroupUuid)
        {
            AdjustGroupCount(sourceGroupUuid, -1);
            AdjustGroupCount(targetGroupUuid, 1);
            PopulateNavigation(_selectedGroupUuid);
        }
        RefreshEntryListFingerprint(CurrentGroupTitle.Text);
    }

    /// <summary>
    /// Recomputes the "already rendered" identity after an in-place row update,
    /// so a following reload does not rebuild the whole list for the same rows.
    /// </summary>
    private void RefreshEntryListFingerprint(string title)
    {
        List<EntryRowInput> rows = Entries
            .Select(item => new EntryRowInput(
                item.Uuid,
                item.GroupUuid,
                item.GroupName,
                item.Title,
                item.Username,
                item.Url,
                item.HasTotp,
                0))
            .ToList();
        _lastEntryFingerprint = EntryListProjection.Fingerprint(rows, title);
    }

    private void AdjustGroupCount(string? groupUuid, int delta)
    {
        if (groupUuid is null || delta == 0)
        {
            return;
        }
        for (int index = 0; index < Groups.Count; index++)
        {
            GroupNavItem group = Groups[index];
            if (group.Uuid == groupUuid)
            {
                Groups[index] = new GroupNavItem(group.Uuid, group.Name,
                    Math.Max(0, group.CountValue + delta), group.Glyph);
                return;
            }
        }
    }

    private void ListOptionsButton_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        void AddSort(string label, EntrySort value)
        {
            var item = new RadioMenuFlyoutItem
            {
                Text = label,
                GroupName = "entry-sort",
                IsChecked = _entrySort == value,
            };
            item.Click += (_, _) =>
            {
                _entrySort = value;
                RefreshEntries();
            };
            flyout.Items.Add(item);
        }
        AddSort(Loc.T("Sort by title"), EntrySort.Title);
        AddSort(Loc.T("Sort by recently modified"), EntrySort.Modified);
        AddSort(Loc.T("Sort by group"), EntrySort.Group);
        flyout.Items.Add(new MenuFlyoutSeparator());
        var totpOnly = new ToggleMenuFlyoutItem
        {
            Text = Loc.T("Only entries with TOTP"),
            IsChecked = _onlyTotp,
        };
        totpOnly.Click += (_, _) =>
        {
            _onlyTotp = totpOnly.IsChecked;
            RefreshEntries();
        };
        flyout.Items.Add(totpOnly);
        flyout.Placement = FlyoutPlacementMode.Bottom;
        flyout.ShowAt(ListOptionsButton);
    }

    private async void RefreshEntries()
    {
        try
        {
            await LoadSelectedGroupAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void NavView_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_suppressNav ||
            args.SelectedItemContainer is not NavigationViewItem item ||
            item.Tag is not string tag)
        {
            return;
        }
        // Footer actions are invoked explicitly, never treated as groups.
        if (tag is "settings" or "sync" or "backups" or "lock")
        {
            return;
        }
        ClosePanels();
        _selectedGroupUuid = tag == "all" ? null : tag;
        try
        {
            await LoadSelectedGroupAsync();
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void HamburgerButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleNavigationPane();
    }

    // The built-in NavigationView pane transition only slides the content with a
    // render transform: the SplitView puts the content at its final width on the
    // first frame, so the workspace columns jump while the pane still looks
    // animated. Resizing the pane length in layout instead makes the SplitView
    // re-measure on every frame, so the entry list and detail pane follow the
    // pane width.
    private const int PaneOpenMilliseconds = 200;
    private const int PaneCloseMilliseconds = 180;

    /// <summary>
    /// Opens or closes the navigation pane, resizing it in layout so the
    /// workspace columns follow the pane continuously.
    /// </summary>
    private void ToggleNavigationPane()
    {
        _paneOpen = !_paneOpen;
        if (!_uiSettings.AnimationsEnabled)
        {
            // Respect the system "show animations" preference and use the plain
            // pane toggle instead.
            StopPaneAnimation();
            NavView.OpenPaneLength = GetPaneOpenLength();
            NavView.IsPaneOpen = _paneOpen;
            return;
        }

        bool opening = _paneOpen;
        double compactLength = Math.Max(0, NavView.CompactPaneLength);
        double openLength = GetPaneOpenLength();

        if (opening && !NavView.IsPaneOpen)
        {
            // The pane is fully closed (icon rail). Open it while its length
            // still equals the rail width, so the SplitView's own transition has
            // nothing to move; the width animation below provides the motion.
            NavView.OpenPaneLength = compactLength;
            NavView.IsPaneOpen = true;
        }

        double from = Math.Min(NavView.OpenPaneLength, NavView.ActualWidth);
        if (from <= 0)
        {
            from = compactLength;
        }
        double to = opening ? openLength : compactLength;
        StartPaneAnimation(from, to,
            opening ? PaneOpenMilliseconds : PaneCloseMilliseconds);
    }

    private double GetPaneOpenLength()
    {
        if (_paneOpenLength <= 0)
        {
            // OpenPaneLength is overwritten while the pane is animated, so keep
            // the configured length to reopen to.
            _paneOpenLength = NavView.OpenPaneLength > 0 ? NavView.OpenPaneLength : 232;
        }
        return _paneOpenLength;
    }

    private void StartPaneAnimation(double from, double to, int milliseconds)
    {
        if (Math.Abs(to - from) < 0.5)
        {
            CompletePaneAnimation(to);
            return;
        }

        _paneAnimationFrom = from;
        _paneAnimationTo = to;
        _paneAnimationMilliseconds = Math.Max(1, milliseconds);
        _paneAnimationStartedAt = null;
        _paneAnimating = true;
        if (!_paneAnimationFrameHooked)
        {
            CompositionTarget.Rendering += OnPaneAnimationFrame;
            _paneAnimationFrameHooked = true;
        }
    }

    private void OnPaneAnimationFrame(object? sender, object args)
    {
        if (!_paneAnimating)
        {
            return;
        }

        TimeSpan now = (args as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        _paneAnimationStartedAt ??= now;
        double progress = Math.Clamp(
            (now - _paneAnimationStartedAt.Value).TotalMilliseconds /
                _paneAnimationMilliseconds,
            0,
            1);
        // Ease out so the pane settles instead of stopping abruptly, matching the
        // shape of the built-in pane transition.
        double eased = 1 - Math.Pow(1 - progress, 3);
        NavView.OpenPaneLength = _paneAnimationFrom +
            (_paneAnimationTo - _paneAnimationFrom) * eased;
        if (progress >= 1)
        {
            CompletePaneAnimation(_paneAnimationTo);
        }
    }

    private void CompletePaneAnimation(double width)
    {
        NavView.OpenPaneLength = width;
        if (!_paneOpen)
        {
            // The pane is already at the compact rail width, so closing it here
            // does not change the layout and cannot jump.
            NavView.IsPaneOpen = false;
        }
        StopPaneAnimation();
    }

    private void StopPaneAnimation()
    {
        _paneAnimating = false;
        _paneAnimationStartedAt = null;
        if (_paneAnimationFrameHooked)
        {
            CompositionTarget.Rendering -= OnPaneAnimationFrame;
            _paneAnimationFrameHooked = false;
        }
    }

    private void TitleBarButton_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        // Buttons in the title bar must never fall through to the caption's
        // double-click maximize/restore behavior.
        e.Handled = true;
    }

    private async Task LoadSelectedGroupAsync()
    {
        string? query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            List<SearchMatchDto> matches = await _core.SearchAsync(query);
            IEnumerable<EntrySummaryDto> results = matches.Select(match => match.Entry);
            if (_recycleBinGroupUuid is not null)
            {
                bool viewingRecycleBin = _selectedGroupUuid == _recycleBinGroupUuid;
                results = viewingRecycleBin
                    ? results.Where(entry => entry.GroupUuid == _recycleBinGroupUuid)
                    : results.Where(entry => entry.GroupUuid != _recycleBinGroupUuid);
            }
            PopulateEntries(results, Loc.T("Search results"));
            return;
        }
        List<EntrySummaryDto> entries = await _core.ListEntriesAsync(_selectedGroupUuid);
        if (_selectedGroupUuid is null && _recycleBinGroupUuid is not null)
        {
            entries = entries
                .Where(entry => entry.GroupUuid != _recycleBinGroupUuid)
                .ToList();
        }
        string title = _selectedGroupUuid is null
            ? Loc.T("All entries")
            : _groupNames.GetValueOrDefault(_selectedGroupUuid, Loc.T("Entries"));
        PopulateEntries(entries, title);
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput || !_core.IsOpen)
        {
            return;
        }
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        CancellationToken token = _searchCts.Token;
        try
        {
            await Task.Delay(220, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        try
        {
            await LoadSelectedGroupAsync();
        }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
            {
                ShowToast(ex.Message, InfoBarSeverity.Error);
            }
        }
    }

    private async void SyncStatusButton_Click(object sender, RoutedEventArgs e)
    {
        if (!IsSyncConfigured)
        {
            ShowToast(Loc.T("Configure WebDAV in Settings first."), InfoBarSeverity.Informational);
            SettingsButton_Click(this, new RoutedEventArgs());
            return;
        }
        await SyncAsync();
    }

    private void NavView_ItemInvoked(NavigationView sender,
        NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer is not NavigationViewItem item ||
            item.Tag is not string tag)
        {
            return;
        }
        ClosePanels();
        switch (tag)
        {
            case "sync":
                SyncStatusButton_Click(this, new RoutedEventArgs());
                break;
            case "backups":
                _ = ShowBackupsAsync();
                break;
            case "lock":
                LockButton_Click(this, new RoutedEventArgs());
                break;
            case "settings":
                SettingsButton_Click(this, new RoutedEventArgs());
                break;
        }
    }

    // -------------------------------------------------------------- detail

    private async void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRowPresenter item)
        {
            ClearDetail();
            return;
        }
        try
        {
            await ShowEntryAsync(item);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private void EntryList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        _draggedEntryGroupUuid = null;
        _draggedEntryTitle = null;
        _dragSameGroupHintShown = false;
        if (e.Items.Count == 0 || e.Items[0] is not EntryRowPresenter entry)
        {
            e.Cancel = true;
            return;
        }
        _draggedEntryGroupUuid = entry.GroupUuid;
        _draggedEntryTitle = entry.Title;
        e.Data.SetText(entry.Uuid);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        e.Data.Properties.Title = entry.Title;
    }

    private void EntryList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        _draggedEntryGroupUuid = null;
        _draggedEntryTitle = null;
        _dragSameGroupHintShown = false;
    }

    private void EntryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }
        ListViewItem? container = FindAncestor<ListViewItem>(source);
        if (container?.DataContext is not EntryRowPresenter entry)
        {
            return;
        }
        EntryList.SelectedItem = entry;
        BuildEntryContextMenu(entry).ShowAt(container);
        e.Handled = true;
    }

    private MenuFlyout BuildEntryContextMenu(EntryRowPresenter entry)
    {
        var flyout = new MenuFlyout();
        void Add(string text, string glyph, Func<Task> action, bool danger = false)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontSize = 12 },
            };
            if (danger)
            {
                item.Foreground =
                    (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
            }
            item.Click += async (_, _) =>
            {
                try
                {
                    await action();
                }
                catch (Exception ex)
                {
                    ShowToast(ex.Message, InfoBarSeverity.Error);
                }
            };
            flyout.Items.Add(item);
        }

        if (!string.IsNullOrWhiteSpace(entry.Username))
        {
            Add(Loc.T("Copy username"), "\uE8C8", () =>
            {
                CopyText(entry.Username, Loc.T("Username copied"));
                return Task.CompletedTask;
            });
        }
        Add(Loc.T("Copy password"), "\uE8C8", () => CopyEntryPasswordAsync(entry));
        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            Add(Loc.T("Copy website"), "\uE8C8", () =>
            {
                CopyText(entry.Url, Loc.T("Website copied"));
                return Task.CompletedTask;
            });
            Add(Loc.T("Open website"), "\uE8A7", () => OpenEntryUrlAsync(entry));
        }
        if (entry.HasTotp)
        {
            Add(Loc.T("Copy TOTP"), "\uE8C8", () => CopyEntryTotpAsync(entry));
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add(Loc.T("Edit entry"), "\uE70F", () => RunEntryActionAsync(entry, () =>
            EditEntryButton_Click(this, new RoutedEventArgs())));
        Add(Loc.T("Move entry"), "\uE8DE", () => RunEntryActionAsync(entry, () =>
            MoveEntryButton_Click(this, new RoutedEventArgs())));
        Add(Loc.T("Delete entry"), "\uE74D", () => RunEntryActionAsync(entry, () =>
            DeleteEntryButton_Click(this, new RoutedEventArgs())), danger: true);
        return flyout;
    }

    private async Task CopyEntryPasswordAsync(EntryRowPresenter entry)
    {
        EntryFullDto full = await _core.GetEntryAsync(entry.Uuid);
        CopyText(full.PasswordSecret, Loc.T("Password copied"));
    }

    private async Task CopyEntryTotpAsync(EntryRowPresenter entry)
    {
        TotpCodeDto? code = await _core.TotpCodeAsync(entry.Uuid);
        if (code is null)
        {
            ShowToast(Loc.T("No TOTP configured for this entry."), InfoBarSeverity.Warning);
            return;
        }
        CopyText(code.Code, Loc.T("One-time code copied"));
    }

    private async Task OpenEntryUrlAsync(EntryRowPresenter entry)
    {
        if (Uri.TryCreate(entry.Url, UriKind.Absolute, out Uri? uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private async Task RunEntryActionAsync(EntryRowPresenter entry, Action action)
    {
        await ShowEntryAsync(entry);
        action();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private async Task ShowEntryAsync(EntryRowPresenter summary, bool animate = true)
    {
        int generation = ++_showGeneration;
        _currentSummary = summary;
        _passwordVisible = false;
        EntryFullDto full = await _core.GetEntryAsync(summary.Uuid);
        if (generation != _showGeneration)
        {
            return;
        }
        _currentFull = full;
        string groupName = _groupNames.GetValueOrDefault(full.GroupUuid, Loc.T("Vault"));
        CustomFieldDto? custom = full.CustomFields.FirstOrDefault(field => !IsTotpField(field.Key));
        Detail.Load(new EntryDetailInput(
            full.Title,
            groupName,
            full.Username,
            full.PasswordSecret,
            full.Url,
            full.Notes,
            full.Tags,
            custom?.Key,
            custom?.Value,
            full.Totp is not null));
        if (Detail.HasTotp)
        {
            Totp.SelectEntry(full.Uuid);
            await Totp.TickAsync();
            UpdateTotpPresentation();
        }
        else
        {
            Totp.SelectEntry(null);
            UpdateTotpPresentation();
        }
        if (animate)
        {
            AnimateDetailContent();
        }
    }

    private void ClearDetail()
    {
        _currentFull = null;
        _currentSummary = null;
        Detail.Clear(Loc.T("Select an entry"));
        Totp.Reset();
        UpdateTotpPresentation();
    }

    private async Task TickTotpAsync()
    {
        await Totp.TickAsync();
        UpdateTotpPresentation();
        if (Detail.HasTotp)
        {
            ScheduleTotpTimerTick(DateTimeOffset.UtcNow);
        }
    }

    private void UpdateTotpPresentation()
    {
        bool sectionVisible = Detail.HasTotp;
        TotpSection.Visibility = ToVisibility(sectionVisible);
        TotpDivider.Visibility = TotpSection.Visibility;
        bool codeVisible = sectionVisible && Totp.HasCode;
        TotpRemainingText.Visibility = ToVisibility(codeVisible);
        TotpProgress.Visibility = ToVisibility(codeVisible);
        TotpCode.FontSize = codeVisible ? 24 : 13;
        if (sectionVisible && !Totp.HasCode)
        {
            TotpCode.Text = Loc.T("No TOTP configured");
        }
        else if (!sectionVisible)
        {
            TotpCode.Text = string.Empty;
        }
        TotpProgress.Value = Totp.ProgressValue;
        TotpProgress.Maximum = Math.Max(1, Totp.Period);
        TotpRemainingText.Text = codeVisible
            ? Loc.T("Refreshes in {0}s", Totp.RemainingSeconds)
            : string.Empty;
    }

    private void ScheduleTotpTimerTick(DateTimeOffset now)
    {
        if (Totp.WindowEndUnix == long.MaxValue)
        {
            _totpTimer.Interval = TimeSpan.FromSeconds(1);
            return;
        }
        DateTimeOffset boundary = DateTimeOffset.FromUnixTimeSeconds(Totp.WindowEndUnix);
        TimeSpan untilBoundary = boundary - now;
        TimeSpan interval = TotpTiming.NextTick(untilBoundary);
        if (Math.Abs((_totpTimer.Interval - interval).TotalMilliseconds) >= 10)
        {
            // Changing the interval restarts the timer, so the next tick lands
            // on (or just after) the TOTP window boundary.
            _totpTimer.Interval = interval;
        }
    }
    private static bool IsTotpField(string key)
        => key == "otp"
           || key == "TOTP Seed"
           || key == "TOTP Settings"
           || key.StartsWith("TimeOtp-", StringComparison.OrdinalIgnoreCase);

    private void RevealPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null)
        {
            return;
        }
        _passwordVisible = !_passwordVisible;
        Detail.SetPasswordVisible(_passwordVisible);
    }

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.Username, Loc.T("Username copied"));
        }
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.PasswordSecret, Loc.T("Password copied"));
        }
    }

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.Url, Loc.T("URL copied"));
        }
    }

    private void CopyTotpButton_Click(object sender, RoutedEventArgs e)
    {
        string? code = Totp.CurrentCode;
        if (!string.IsNullOrEmpty(code))
        {
            CopyText(code, Loc.T("One-time code copied"));
        }
    }

    private void CopyCustomFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(Detail.CustomFieldValue))
        {
            CopyText(Detail.CustomFieldValue, Loc.T("Custom field copied"));
        }
    }

    private void CopyText(string text, string label)
    {
        var package = new DataPackage();
        package.SetText(text);
        // Keep copied secrets out of Win+V history and cloud clipboard where
        // Windows honors the request.
        var options = new ClipboardContentOptions
        {
            IsAllowedInHistory = false,
            IsRoamable = false,
        };
        try
        {
            Clipboard.SetContentWithOptions(package, options);
        }
        catch
        {
            // Older shells may not support content options; plain copy still works.
            Clipboard.SetContent(package);
        }
        _lastClipboardText = text;
        _clipboardTimer.Stop();
        if (Settings.ClearClipboardAfter30Seconds)
        {
            _clipboardTimer.Start();
            ShowToast(Loc.T("{0} — clears in 30 seconds", label), InfoBarSeverity.Success);
        }
        else
        {
            ShowToast(label, InfoBarSeverity.Success);
        }
    }

    private async void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null)
        {
            return;
        }
        if (Uri.TryCreate(_currentFull.Url, UriKind.Absolute, out Uri? uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    // ---------------------------------------------------------------- CRUD

    private async void NewEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_core.IsOpen)
        {
            return;
        }
        var groupChoice = new ComboBox
        {
            Header = Loc.T("Group"),
            ItemsSource = _selectableGroups,
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        groupChoice.SelectedItem = _selectableGroups.FirstOrDefault(group =>
            group.Uuid == (_selectedGroupUuid ?? _selectableGroups.FirstOrDefault()?.Uuid));

        var title = new TextBox { Header = Loc.T("Title"), PlaceholderText = Loc.T("Entry name") };
        var username = new TextBox { Header = Loc.T("Username"), PlaceholderText = "name@example.com" };
        var password = new PasswordBox { Header = Loc.T("Password"), PasswordRevealMode = PasswordRevealMode.Peek };
        var url = new TextBox { Header = Loc.T("Website"), PlaceholderText = "https://" };
        var notes = new TextBox
        {
            Header = Loc.T("Notes"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84,
        };
        UIElement? formSlot = null;
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(groupChoice);
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(BuildPasswordRow(password, () =>
        {
            if (formSlot is not null)
            {
                ShowPasswordGeneratorPage(password, formSlot, Loc.T("New entry"));
            }
        }));
        content.Children.Add(url);
        content.Children.Add(notes);
        UIElement form = CreateFormSection(Loc.T("Details"), content);
        formSlot = form;
        if (await ShowDetailPanelAsync(Loc.T("New entry"), form, Loc.T("Create"),
                primaryGlyph: "\uE710") !=
                RightPanelResult.Primary ||
            groupChoice.SelectedItem is not GroupDto group)
        {
            return;
        }
        try
        {
            var editor = EntryEditorViewModel.New(group.Uuid);
            editor.Title = title.Text;
            editor.Username = username.Text;
            editor.Password = password.Password;
            editor.Url = url.Text;
            editor.Notes = notes.Text;
            string uuid = await _core.CreateEntryAsync(editor.GroupUuid!,
                editor.CreateTitle(Loc.T("Untitled entry")),
                editor.NormalizedUsername(), editor.Password, editor.NormalizedUrl(),
                editor.Notes);
            await SaveAsync();
            await ReloadVaultAsync(uuid, preserveScroll: false);
            ShowToast(Loc.T("Entry created and saved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void EditEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null)
        {
            return;
        }
        EntryEditorViewModel editor = EntryEditorViewModel.FromEntry(
            _currentFull.Uuid,
            _currentFull.Title,
            _currentFull.Username,
            _currentFull.PasswordSecret,
            _currentFull.Url,
            _currentFull.Tags,
            _currentFull.Notes,
            _currentFull.Totp?.SecretBase32,
            _currentFull.Totp?.Algorithm,
            _currentFull.Totp?.Digits,
            _currentFull.Totp?.Period);
        var title = new TextBox { Header = Loc.T("Title"), Text = _currentFull.Title };
        var username = new TextBox { Header = Loc.T("Username"), Text = _currentFull.Username };
        var password = new PasswordBox
        {
            Header = Loc.T("Password"),
            Password = _currentFull.PasswordSecret,
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var url = new TextBox { Header = Loc.T("Website"), Text = _currentFull.Url };
        var tags = new TextBox
        {
            Header = Loc.T("Tags (comma-separated)"),
            Text = string.Join(", ", _currentFull.Tags),
        };
        var notes = new TextBox
        {
            Header = Loc.T("Notes"),
            Text = _currentFull.Notes,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84,
        };
        TotpConfigDto? existingTotp = _currentFull.Totp;
        var totpSecret = new TextBox
        {
            Header = Loc.T("Base32 secret"),
            Text = existingTotp?.SecretBase32 ?? string.Empty,
            PlaceholderText = "JBSWY3DPEHPK3PXP",
            FontFamily = new FontFamily("Consolas"),
        };
        var totpAlgorithm = new ComboBox
        {
            Header = Loc.T("Algorithm"),
            ItemsSource = new[] { "SHA1", "SHA256", "SHA512" },
            SelectedItem = existingTotp?.Algorithm ?? "SHA1",
        };
        var totpDigits = new ComboBox
        {
            Header = Loc.T("Digits"),
            ItemsSource = new[] { "6", "8" },
            SelectedItem = (existingTotp?.Digits ?? 6).ToString(),
        };
        var totpPeriod = new NumberBox
        {
            Header = Loc.T("Period (seconds)"),
            Value = existingTotp?.Period ?? 30,
            Minimum = 1,
            Maximum = 300,
        };
        var totpHint = new TextBlock
        {
            Text = existingTotp is null
                ? Loc.T("No TOTP yet. Paste or import a Base32 secret to add one.")
                : Loc.T("Leave the secret empty and save to remove TOTP."),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var totpStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var totpFields = new StackPanel { Spacing = 12 };
        totpFields.Children.Add(totpHint);
        totpFields.Children.Add(BuildTotpImportRow(
            totpSecret, totpAlgorithm, totpDigits, totpPeriod, totpStatus));
        totpFields.Children.Add(totpSecret);
        totpFields.Children.Add(totpAlgorithm);
        totpFields.Children.Add(totpDigits);
        totpFields.Children.Add(totpPeriod);

        var form = new StackPanel { Spacing = 20 };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(BuildPasswordRow(password,
            () => ShowPasswordGeneratorPage(password, form, Loc.T("Edit entry"))));
        content.Children.Add(url);
        content.Children.Add(tags);
        content.Children.Add(notes);
        form.Children.Add(CreateFormSection(Loc.T("Details"), content));
        form.Children.Add(CreateFormSection(Loc.T("One-time password"), totpFields));
        if (await ShowDetailPanelAsync(Loc.T("Edit entry"), form, Loc.T("Save")) !=
            RightPanelResult.Primary)
        {
            return;
        }
        try
        {
            editor.Title = title.Text;
            editor.Username = username.Text;
            editor.Password = password.Password;
            editor.Url = url.Text;
            editor.TagsText = tags.Text;
            editor.Notes = notes.Text;
            editor.TotpSecret = totpSecret.Text;
            editor.TotpAlgorithm = totpAlgorithm.SelectedItem as string ?? "SHA1";
            editor.TotpDigits = totpDigits.SelectedItem as string ?? "6";
            editor.TotpPeriod = (int)Math.Clamp(totpPeriod.Value, 1, 300);

            List<string> tagList = editor.TagList();
            string uuid = editor.Uuid!;
            await _core.UpdateEntryAsync(uuid, editor.UpdateTitle(),
                editor.NormalizedUsername(), editor.Password, editor.NormalizedUrl(),
                editor.Notes, tagList);
            if (editor.ShouldDeleteTotp)
            {
                await _core.TotpDeleteAsync(uuid);
            }
            else if (editor.BuildTotp() is { } totp)
            {
                await _core.TotpSetAsync(uuid, new TotpConfigDto
                {
                    SecretBase32 = totp.SecretBase32,
                    Algorithm = totp.Algorithm,
                    Digits = totp.Digits,
                    Period = totp.Period,
                    Encoder = "totp",
                });
            }
            await SaveAsync();
            await ReloadVaultAsync(uuid);
            ShowToast(Loc.T("Entry updated and saved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void MoveEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null || _selectableGroups.Count == 0)
        {
            return;
        }
        // Offer the vault root as "Top level (no folder)" so an entry that was
        // moved into a folder can always be moved back out without dragging.
        var moveTargets = new List<GroupDto>(_selectableGroups);
        if (!string.IsNullOrEmpty(_rootGroupUuid) &&
            moveTargets.All(group => group.Uuid != _rootGroupUuid))
        {
            moveTargets.Insert(0, new GroupDto
            {
                Uuid = _rootGroupUuid!,
                Name = Loc.T("Top level (no folder)"),
            });
        }
        var choices = new ComboBox
        {
            Header = Loc.T("Move to"),
            ItemsSource = moveTargets,
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        choices.SelectedItem = moveTargets.FirstOrDefault(group =>
            group.Uuid == _currentFull.GroupUuid) ?? moveTargets[0];
        if (await ShowDetailPanelAsync(Loc.T("Move entry"), choices, Loc.T("Move"),
                primaryGlyph: "\uE8DE") !=
                RightPanelResult.Primary ||
            choices.SelectedItem is not GroupDto target ||
            _currentFull is null)
        {
            return;
        }
        string uuid = _currentFull.Uuid;
        bool toTopLevel = !string.IsNullOrEmpty(_rootGroupUuid) &&
            target.Uuid == _rootGroupUuid;
        string targetLabel = toTopLevel ? Loc.T("the top level") : target.Name;
        if (target.Uuid == _currentFull.GroupUuid)
        {
            ShowToast(toTopLevel
                    ? Loc.T("\"{0}\" is already at the top level — nothing to move.",
                        _currentFull.Title)
                    : Loc.T("\"{0}\" is already in {1} — nothing to move.",
                        _currentFull.Title, target.Name),
                InfoBarSeverity.Informational);
            return;
        }
        try
        {
            await _core.MoveEntryAsync(uuid, target.Uuid);
            await SaveAsync();
            _selectedGroupUuid = toTopLevel ? null : target.Uuid;
            await ReloadVaultAsync(uuid);
            ShowToast(Loc.T("Moved to {0}", targetLabel), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null)
        {
            return;
        }
        string title = _currentFull.Title;
        string uuid = _currentFull.Uuid;
        var dialog = new ContentDialog
        {
            Title = Loc.T("Delete entry?"),
            Content = Loc.T("\"{0}\" will be moved to the recycle bin.", title),
            PrimaryButtonText = Loc.T("Delete"),
            CloseButtonText = Loc.T("Cancel"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            await _core.DeleteEntryAsync(uuid);
            await SaveAsync();
            await ReloadVaultAsync(null);
            ShowToast(Loc.T("Entry moved to the recycle bin"), InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private static UIElement CreateFormSection(string label, UIElement content)
    {
        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18),
            Child = content,
        };
        var section = new StackPanel { Spacing = 8 };
        section.Children.Add(new TextBlock
        {
            Text = label,
            Style = (Style)Application.Current.Resources["SectionLabel"],
            Margin = new Thickness(2, 0, 0, 0),
        });
        section.Children.Add(card);
        return section;
    }

    private StackPanel BuildTotpImportRow(TextBox secret, ComboBox algorithm,
        ComboBox digits, NumberBox period, TextBlock status)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var importFile = new Button
        {
            Content = CreateIconLabel(Loc.T("Import QR image…"), "\uE8B7"),
        };
        var importClipboard = new Button
        {
            Content = CreateIconLabel(Loc.T("Paste QR / otpauth…"), "\uE77F"),
        };
        async Task ApplyImportedUriAsync(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                status.Text = Loc.T("No otpauth QR code found. Paste a Base32 secret instead.");
                return;
            }
            try
            {
                TotpConfigDto parsed = await _core.ParseTotpUriAsync(uri);
                secret.Text = parsed.SecretBase32;
                algorithm.SelectedItem = parsed.Algorithm;
                digits.SelectedItem = parsed.Digits.ToString();
                period.Value = Math.Clamp(parsed.Period, 1, 300);
                string account = string.Join(" / ",
                    new[] { parsed.Issuer, parsed.Account }.Where(value =>
                        !string.IsNullOrWhiteSpace(value)));
                status.Text = account.Length == 0
                    ? Loc.T("QR imported. Review the parameters and save.")
                    : Loc.T("Imported {0}. Review and save.", account);
            }
            catch (Exception ex)
            {
                status.Text = Loc.T("Could not read otpauth URI: {0}", ex.Message);
            }
        }
        importFile.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                };
                foreach (string extension in new[]
                         {
                             ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
                         })
                {
                    picker.FileTypeFilter.Add(extension);
                }
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    await ApplyImportedUriAsync(await QrDecoder.DecodeFileAsync(file));
                }
            }
            catch (Exception ex)
            {
                status.Text = Loc.T("QR image failed: {0}", ex.Message);
            }
        };
        importClipboard.Click += async (_, _) =>
        {
            try
            {
                await ApplyImportedUriAsync(await QrDecoder.DecodeClipboardAsync());
            }
            catch (Exception ex)
            {
                status.Text = Loc.T("Clipboard QR failed: {0}", ex.Message);
            }
        };
        row.Children.Add(importFile);
        row.Children.Add(importClipboard);
        var wrapper = new StackPanel { Spacing = 8 };
        wrapper.Children.Add(row);
        wrapper.Children.Add(status);
        return wrapper;
    }

    private Grid BuildPasswordRow(PasswordBox password, Action openGenerator)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(password);
        var generate = new Button
        {
            Content = CreateIconLabel(Loc.T("Generate"), "\uE945"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        SetButtonAutomation(generate, Loc.T("Generate password"),
            Loc.T("Opens the password generator options"), "GeneratePasswordButton");
        generate.Click += (_, _) => openGenerator();
        Grid.SetColumn(generate, 1);
        row.Children.Add(generate);
        return row;
    }

    private static ToggleSwitch CreateGeneratorToggle(string header, bool isOn)
    {
        var toggle = new ToggleSwitch
        {
            Header = header,
            IsOn = isOn,
            OnContent = Loc.T("On"),
            OffContent = Loc.T("Off"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(toggle, header);
        return toggle;
    }

    /// <summary>
    /// Password generator page. It replaces the entry form inside the right pane
    /// while open; Back and Done return to the form. Every option change
    /// regenerates through the core and writes the result into the surrounding
    /// password box, so Done only restores the form and Save stores the entry.
    /// </summary>
    private void ShowPasswordGeneratorPage(PasswordBox target, UIElement form, string formTitle)
    {
        if (!_core.IsOpen)
        {
            ShowToast(Loc.T("Unlock the vault before generating a password."),
                InfoBarSeverity.Warning);
            return;
        }
        if (_detailPanelContent is null || _detailPanelTitle is null ||
            _detailPanelActions is null)
        {
            return;
        }
        var length = new NumberBox
        {
            Header = Loc.T("Length"),
            Value = 20,
            Minimum = 8,
            Maximum = 64,
            SmallChange = 1,
            LargeChange = 8,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(length, Loc.T("Password length"));
        AutomationProperties.SetAutomationId(length, "GeneratorLength");
        ToggleSwitch lowercase = CreateGeneratorToggle(Loc.T("Lowercase (a-z)"), isOn: true);
        ToggleSwitch uppercase = CreateGeneratorToggle(Loc.T("Uppercase (A-Z)"), isOn: true);
        ToggleSwitch digits = CreateGeneratorToggle(Loc.T("Digits (0-9)"), isOn: true);
        ToggleSwitch symbols = CreateGeneratorToggle(Loc.T("Symbols (!@#$…)"), isOn: true);
        ToggleSwitch excludeAmbiguous =
            CreateGeneratorToggle(Loc.T("Exclude ambiguous characters (I l 1 O 0 o)"), isOn: true);
        AutomationProperties.SetAutomationId(lowercase, "GeneratorLowercase");
        AutomationProperties.SetAutomationId(uppercase, "GeneratorUppercase");
        AutomationProperties.SetAutomationId(digits, "GeneratorDigits");
        AutomationProperties.SetAutomationId(symbols, "GeneratorSymbols");
        AutomationProperties.SetAutomationId(excludeAmbiguous, "GeneratorExcludeAmbiguous");
        var result = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 16,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = TextWrapping.NoWrap,
        };
        AutomationProperties.SetName(result, Loc.T("Password"));
        AutomationProperties.SetAutomationId(result, "GeneratorResult");
        var copy = new Button { Content = CreateIconLabel(Loc.T("Copy"), "\uE8C8") };
        SetButtonAutomation(copy, Loc.T("Copy password"), null, "GeneratorCopyButton");
        copy.Click += (_, _) =>
        {
            if (result.Text.Length > 0)
            {
                CopyText(result.Text, Loc.T("Password copied"));
            }
        };
        var resultRow = new Grid { ColumnSpacing = 8 };
        resultRow.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resultRow.Children.Add(result);
        Grid.SetColumn(copy, 1);
        resultRow.Children.Add(copy);
        var resultLabel = new TextBlock
        {
            Text = Loc.T("Password"),
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };
        var entropy = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.T("Entropy: —"),
        };
        AutomationProperties.SetAutomationId(entropy, "GeneratorEntropy");
        var hint = new TextBlock
        {
            FontSize = 11,
            MaxWidth = 460,
            HorizontalAlignment = HorizontalAlignment.Left,
            TextWrapping = TextWrapping.Wrap,
            Text = Loc.TLong("Generator.OptionsBody",
                "Options are applied by the core generator; the password is written " +
                "into the field and Save stores it with the entry."),
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        };
        var status = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        AutomationProperties.SetAutomationId(status, "GeneratorStatus");
        var regenerate = new Button
        {
            Content = CreateIconLabel(Loc.T("Regenerate"), "\uE72C"),
        };
        SetButtonAutomation(regenerate, Loc.T("Regenerate password"), null,
            "GeneratorRegenerateButton");
        var done = new Button { Content = Loc.T("Done") };
        SetButtonAutomation(done, Loc.T("Close password generator"), null, "GeneratorDoneButton");
        var actions = new Grid();
        actions.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(regenerate);
        Grid.SetColumn(done, 1);
        actions.Children.Add(done);
        var toggles = new Grid { ColumnSpacing = 12, RowSpacing = 2 };
        toggles.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toggles.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        ToggleSwitch[] classToggles = { lowercase, uppercase, digits, symbols };
        for (int index = 0; index < classToggles.Length; index++)
        {
            Grid.SetRow(classToggles[index], index / 2);
            Grid.SetColumn(classToggles[index], index % 2);
            toggles.Children.Add(classToggles[index]);
        }
        Grid.SetRow(excludeAmbiguous, 2);
        Grid.SetColumnSpan(excludeAmbiguous, 2);
        toggles.Children.Add(excludeAmbiguous);
        var content = new StackPanel { Spacing = 10, MaxWidth = 520 };
        content.Children.Add(resultLabel);
        content.Children.Add(resultRow);
        content.Children.Add(entropy);
        content.Children.Add(hint);
        content.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        });
        content.Children.Add(length);
        content.Children.Add(toggles);
        content.Children.Add(status);
        content.Children.Add(actions);
        int generation = 0;
        async Task RegenerateAsync()
        {
            int ticket = ++generation;
            int lengthValue = double.IsNaN(length.Value)
                ? 20
                : (int)Math.Clamp(length.Value, 8, 64);
            bool lowerOn = lowercase.IsOn;
            bool upperOn = uppercase.IsOn;
            bool digitsOn = digits.IsOn;
            bool symbolsOn = symbols.IsOn;
            if (!lowerOn && !upperOn && !digitsOn && !symbolsOn)
            {
                status.Text = Loc.T("Select at least one character class.");
                return;
            }
            try
            {
                PasswordGenerateDto generated = await _core.GeneratePasswordAsync(
                    lengthValue, excludeAmbiguous.IsOn, lowerOn, upperOn, digitsOn, symbolsOn);
                if (ticket != generation)
                {
                    return;
                }
                target.Password = generated.Password;
                result.Text = generated.Password;
                result.SelectionStart = result.Text.Length;
                entropy.Text = Loc.T("Entropy: {0:F1} bits", generated.EntropyBits);
                status.Text = Loc.T("Password generated. Save the entry to keep it.");
            }
            catch (Exception ex)
            {
                if (ticket == generation)
                {
                    status.Text = Loc.T("Generation failed: {0}", ex.Message);
                }
            }
        }
        void EnsureClassSelected(ToggleSwitch toggled)
        {
            if (toggled.IsOn || lowercase.IsOn || uppercase.IsOn || digits.IsOn ||
                symbols.IsOn)
            {
                return;
            }
            // At least one class must stay enabled: revert the change that would
            // empty the alphabet (the core rejects an empty charset).
            toggled.IsOn = true;
            status.Text = Loc.T("At least one character class must stay enabled.");
        }
        length.ValueChanged += (_, _) => _ = RegenerateAsync();
        lowercase.Toggled += (_, _) =>
        {
            EnsureClassSelected(lowercase);
            _ = RegenerateAsync();
        };
        uppercase.Toggled += (_, _) =>
        {
            EnsureClassSelected(uppercase);
            _ = RegenerateAsync();
        };
        digits.Toggled += (_, _) =>
        {
            EnsureClassSelected(digits);
            _ = RegenerateAsync();
        };
        symbols.Toggled += (_, _) =>
        {
            EnsureClassSelected(symbols);
            _ = RegenerateAsync();
        };
        excludeAmbiguous.Toggled += (_, _) => _ = RegenerateAsync();
        regenerate.Click += (_, _) => _ = RegenerateAsync();
        done.Click += (_, _) => RestoreForm();
        _ = RegenerateAsync();

        void RestoreForm()
        {
            if (_detailPanelContent is null || _detailPanelTitle is null ||
                _detailPanelActions is null)
            {
                return;
            }
            _detailPanelContent.Content = form;
            _detailPanelTitle.Text = formTitle;
            _detailPanelActions.Visibility = Visibility.Visible;
            _generatorReturn = null;
            target.Focus(FocusState.Programmatic);
        }

        _generatorReturn = RestoreForm;
        _detailPanelTitle!.Text = Loc.T("Generate password");
        _detailPanelActions!.Visibility = Visibility.Collapsed;
        _detailPanelContent!.Content = content;
    }

    private async void TotpSetupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is null)
        {
            return;
        }
        EntryFullDto current = _currentFull;
        TotpConfigDto? existing = current.Totp;
        var secret = new TextBox
        {
            Header = Loc.T("Base32 secret"),
            Text = existing?.SecretBase32 ?? string.Empty,
            FontFamily = new FontFamily("Consolas"),
        };
        var algorithm = new ComboBox
        {
            Header = Loc.T("Algorithm"),
            ItemsSource = new[] { "SHA1", "SHA256", "SHA512" },
            SelectedItem = existing?.Algorithm ?? "SHA1",
        };
        var digits = new ComboBox
        {
            Header = Loc.T("Digits"),
            ItemsSource = new[] { "6", "8" },
            SelectedItem = (existing?.Digits ?? 6).ToString(),
        };
        var period = new NumberBox
        {
            Header = Loc.T("Period (seconds)"),
            Value = existing?.Period ?? 30,
            Minimum = 1,
            Maximum = 300,
        };
        var importStatus = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var importFile = new Button { Content = Loc.T("Import QR image…") };
        var importClipboard = new Button { Content = Loc.T("Paste QR / otpauth…") };
        var importRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        importRow.Children.Add(importFile);
        importRow.Children.Add(importClipboard);
        async Task ApplyImportedUriAsync(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri) ||
                !uri.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
            {
                importStatus.Text = Loc.T("No otpauth QR code found. Paste a Base32 secret instead.");
                return;
            }
            try
            {
                TotpConfigDto parsed = await _core.ParseTotpUriAsync(uri);
                secret.Text = parsed.SecretBase32;
                algorithm.SelectedItem = parsed.Algorithm;
                digits.SelectedItem = parsed.Digits.ToString();
                period.Value = Math.Clamp(parsed.Period, 1, 300);
                string account = string.Join(" / ",
                    new[] { parsed.Issuer, parsed.Account }.Where(value =>
                        !string.IsNullOrWhiteSpace(value)));
                importStatus.Text = account.Length == 0
                    ? Loc.T("QR imported. Review the parameters and save.")
                    : Loc.T("Imported {0}. Review and save.", account);
            }
            catch (Exception ex)
            {
                importStatus.Text = Loc.T("Could not read otpauth URI: {0}", ex.Message);
            }
        }
        importFile.Click += async (_, _) =>
        {
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                };
                foreach (string extension in new[]
                         {
                             ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
                         })
                {
                    picker.FileTypeFilter.Add(extension);
                }
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                StorageFile? file = await picker.PickSingleFileAsync();
                if (file is not null)
                {
                    await ApplyImportedUriAsync(await QrDecoder.DecodeFileAsync(file));
                }
            }
            catch (Exception ex)
            {
                importStatus.Text = Loc.T("QR image failed: {0}", ex.Message);
            }
        };
        importClipboard.Click += async (_, _) =>
        {
            try
            {
                await ApplyImportedUriAsync(await QrDecoder.DecodeClipboardAsync());
            }
            catch (Exception ex)
            {
                importStatus.Text = Loc.T("Clipboard QR failed: {0}", ex.Message);
            }
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(importRow);
        content.Children.Add(importStatus);
        content.Children.Add(secret);
        content.Children.Add(algorithm);
        content.Children.Add(digits);
        content.Children.Add(period);
        RightPanelResult result = await ShowDetailPanelAsync(
            existing is null ? Loc.T("Set up TOTP") : Loc.T("Edit TOTP"),
            content,
            Loc.T("Save"),
            existing is null ? null : Loc.T("Remove"));
        if (result == RightPanelResult.Secondary)
        {
            try
            {
                await _core.TotpDeleteAsync(current.Uuid);
                await SaveAsync();
                await ReloadVaultAsync(current.Uuid);
                ShowToast(Loc.T("TOTP removed"), InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, InfoBarSeverity.Error);
            }
            return;
        }
        if (result != RightPanelResult.Primary)
        {
            return;
        }
        try
        {
            await _core.TotpSetAsync(current.Uuid, new TotpConfigDto
            {
                SecretBase32 = secret.Text.Trim(),
                Algorithm = algorithm.SelectedItem as string ?? "SHA1",
                Digits = int.TryParse(digits.SelectedItem as string, out int parsedDigits)
                    ? parsedDigits
                    : 6,
                Period = (int)Math.Clamp(period.Value, 1, 300),
                Encoder = "totp",
            });
            await SaveAsync();
            await ReloadVaultAsync(current.Uuid);
            ShowToast(Loc.T("TOTP saved"), InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    // ------------------------------------------------------------ save/lock

    /// <summary>
    /// Take the cross-process vault lock, keeping the current one when it is for
    /// the same path. Returns false when another Keepassity window owns it.
    /// </summary>
    private bool TryAcquireVaultLock(string path)
    {
        if (_vault.TryAcquire(path, out string? error))
        {
            return true;
        }
        AppLog.Warn($"vault lock unavailable: {error}");
        return false;
    }

    private void ReleaseVaultLock() => _vault.Release();

    /// <summary>Record the database bytes the in-memory model is based on.</summary>
    private void RememberVaultFingerprint(string? path = null)
        => _vault.CaptureFingerprint(path ?? _dbPath);

    private async Task<bool> SaveAsync(bool silent = false, bool syncAfterSave = true)
    {
        if (_dbPath is null)
        {
            return false;
        }
        try
        {
            await _vault.SaveAsync(_core, Settings.BackupRetention);
            MarkLocalDirty();
            if (!silent)
            {
                ShowToast(Loc.T("Saved"), InfoBarSeverity.Success);
            }
            if (syncAfterSave && Settings.SyncOnSave && IsSyncConfigured)
            {
                _ = SyncAsync();
            }
            return true;
        }
        catch (CoreException ex) when (ex.Code == "VaultChanged")
        {
            AppLog.Warn("save refused: the database changed outside this window");
            ShowToast(Loc.T("The database was changed outside Keepassity. Reopen it " +
                            "before saving; nothing was overwritten."),
                InfoBarSeverity.Error);
            return false;
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("Save failed: {0}", ex.Message), InfoBarSeverity.Error);
            throw;
        }
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
        => await LockVaultAsync();

    private async Task LockVaultAsync()
    {
        if (_busy)
        {
            return;
        }
        ClearPendingRemoteDownload();
        ClosePanels();
        _busy = true;
        try
        {
            if (_core.IsOpen)
            {
                // Every edit path saves the database immediately. Saving again
                // here would re-encrypt the working copy and mark it dirty even
                // when nothing changed, so the next WebDAV open would report
                // unsynced local changes and force an unnecessary merge.
                await _core.LockAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast(Loc.T("Lock failed: {0}", ex.Message), InfoBarSeverity.Error);
            _busy = false;
            return;
        }
        _autoLockTimer.Stop();
        _totpTimer.Stop();
        AppLog.Info("vault locked");
        Announce(Loc.T("Vault locked"));
        _currentFull = null;
        _currentSummary = null;
        Entries.Clear();
        Groups.Clear();
        NavView.MenuItems.Clear();
        ClearDetail();
        MainRoot.Visibility = Visibility.Collapsed;
        UnlockRoot.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;
        HamburgerButton.Visibility = Visibility.Collapsed;
        SearchBox.Text = string.Empty;
        MasterPassword.Password = string.Empty;
        _masterPassword = null;
        ReleaseVaultLock();
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        Unlock.ClearStatus();
        UpdateDatabasePath();
        _trayIcon?.SetVaultState(false, null);
        _busy = false;
        MasterPassword.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------- right-side panel

    private static StackPanel CreateIconLabel(string text, string glyph)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
        };
        panel.Children.Add(new FontIcon { Glyph = glyph, FontSize = 12 });
        panel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return panel;
    }

    private Button CreateToolbarButton(string text, string glyph)
    {
        var button = new Button
        {
            Content = CreateIconLabel(text, glyph),
            Style = (Style)Root.Resources["ToolbarButton"],
        };
        // Dynamic buttons carry no accessible name from an icon-only label;
        // derive it from the visible text.
        AutomationProperties.SetName(button, text);
        return button;
    }

    private static Button CreateActionButton(string text, string glyph, bool accent)
    {
        var button = new Button { Content = CreateIconLabel(text, glyph) };
        if (accent)
        {
            button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        }
        AutomationProperties.SetName(button, text);
        return button;
    }

    private void RightPanel_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }
        if (_rightPanelOpen)
        {
            CloseRightPanel();
            e.Handled = true;
        }
        else if (_detailPanelOpen)
        {
            CloseDetailPanel();
            e.Handled = true;
        }
    }

    private void EnsureRightPanel()
    {
        if (_rightPanel is not null)
        {
            return;
        }
        _rightPanel = new Grid { Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(_rightPanel, "RightPanel");
        _rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _rightPanel.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _rightPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(32, 22, 32, 18) };
        _rightPanelTitle = new TextBlock
        {
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AutomationProperties.SetAutomationId(_rightPanelTitle, "RightPanelTitle");
        header.Children.Add(_rightPanelTitle);
        _rightPanel.Children.Add(header);

        _rightPanelContent = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(32, 0, 32, 0),
        };
        var scroll = new ScrollViewer
        {
            Content = _rightPanelContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(scroll, 1);
        _rightPanel.Children.Add(scroll);

        _rightPanelActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Padding = new Thickness(32, 16, 32, 24),
        };
        Grid.SetRow(_rightPanelActions, 2);
        _rightPanel.Children.Add(_rightPanelActions);
        WorkspacePanel.Children.Add(_rightPanel);
    }

    private void SetRightPanelUnderlyingVisible(bool visible)
    {
        foreach (UIElement child in WorkspacePanel.Children)
        {
            if (!ReferenceEquals(child, _rightPanel))
            {
                child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void SetDetailPanelUnderlyingVisible(bool visible)
    {
        foreach (UIElement child in DetailPane.Children)
        {
            if (!ReferenceEquals(child, _detailPanel))
            {
                child.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private async Task<RightPanelResult> ShowRightPanelAsync(string title, UIElement content,
        string primaryText, string? secondaryText = null, string? closeText = null,
        string primaryGlyph = "\uE74E")
    {
        if (_rightPanelOpen)
        {
            return RightPanelResult.Close;
        }
        closeText ??= Loc.T("Cancel");
        CloseDetailPanel();
        EnsureRightPanel();
        _rightPanelOpen = true;
        _rightPanelTitle!.Text = title;
        AutomationProperties.SetName(_rightPanel, Loc.T("{0} panel", title));
        _rightPanelContent!.Content = content;
        _rightPanelActions!.Children.Clear();
        var completion = new TaskCompletionSource<RightPanelResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _rightPanelCompletion = completion;
        void Finish(RightPanelResult result) => completion.TrySetResult(result);

        if (!string.IsNullOrWhiteSpace(closeText))
        {
            var close = CreateActionButton(closeText, "\uE711", accent: false);
            close.Click += (_, _) => Finish(RightPanelResult.Close);
            _rightPanelActions.Children.Add(close);
        }
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var secondary = CreateActionButton(secondaryText, "\uE74D", accent: false);
            secondary.Click += (_, _) => Finish(RightPanelResult.Secondary);
            _rightPanelActions.Children.Add(secondary);
        }
        var primary = CreateActionButton(primaryText, primaryGlyph, accent: true);
        primary.Click += (_, _) => Finish(RightPanelResult.Primary);
        _rightPanelActions.Children.Add(primary);

        SetRightPanelUnderlyingVisible(false);
        _rightPanel!.Visibility = Visibility.Visible;
        AnimatePanelIn(_rightPanel);
        RightPanelResult result = await completion.Task;
        await AnimatePanelOutAsync(_rightPanel);
        SetRightPanelUnderlyingVisible(true);
        _rightPanelContent.Content = null;
        _rightPanelActions.Children.Clear();
        _rightPanelOpen = false;
        _rightPanelCompletion = null;
        return result;
    }

    private void CloseRightPanel()
    {
        if (_rightPanelOpen)
        {
            _rightPanelCompletion?.TrySetResult(RightPanelResult.Close);
        }
    }

    private void EnsureDetailPanel()
    {
        if (_detailPanel is not null)
        {
            return;
        }
        _detailPanel = new Grid
        {
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(-32, -22, -32, -24),
        };
        AutomationProperties.SetAutomationId(_detailPanel, "DetailDialog");
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detailPanel.RowDefinitions.Add(
            new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _detailPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Grid { Padding = new Thickness(32, 22, 32, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var back = CreateToolbarButton(Loc.T("Back"), "\uE72B");
        back.Click += (_, _) =>
        {
            // While the generator page is open, Back returns to the entry form
            // instead of closing the whole dialog.
            if (_generatorReturn is not null)
            {
                Action restore = _generatorReturn;
                _generatorReturn = null;
                restore();
                return;
            }
            CloseDetailPanel();
        };
        header.Children.Add(back);
        _detailPanelTitle = new TextBlock
        {
            FontSize = 22,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AutomationProperties.SetAutomationId(_detailPanelTitle, "DetailDialogTitle");
        Grid.SetColumn(_detailPanelTitle, 1);
        header.Children.Add(_detailPanelTitle);
        _detailPanel.Children.Add(header);

        _detailPanelContent = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(32, 0, 32, 0),
        };
        var scroll = new ScrollViewer
        {
            Content = _detailPanelContent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 8, 0),
        };
        Grid.SetRow(scroll, 1);
        _detailPanel.Children.Add(scroll);

        _detailPanelActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Padding = new Thickness(32, 16, 32, 24),
        };
        Grid.SetRow(_detailPanelActions, 2);
        _detailPanel.Children.Add(_detailPanelActions);
        Grid.SetRow(_detailPanel, 0);
        Grid.SetRowSpan(_detailPanel, 2);
        DetailPane.Children.Add(_detailPanel);
    }

    private async Task<RightPanelResult> ShowDetailPanelAsync(string title, UIElement content,
        string primaryText, string? secondaryText = null, string? closeText = null,
        string primaryGlyph = "\uE74E")
    {
        if (_detailPanelOpen)
        {
            return RightPanelResult.Close;
        }
        closeText ??= Loc.T("Cancel");
        CloseRightPanel();
        EnsureDetailPanel();
        _detailPanelOpen = true;
        _detailPanelTitle!.Text = title;
        AutomationProperties.SetName(_detailPanel, Loc.T("{0} dialog", title));
        _detailPanelContent!.Content = content;
        _detailPanelActions!.Children.Clear();
        _detailPanelActions.Visibility = Visibility.Visible;
        _generatorReturn = null;
        var completion = new TaskCompletionSource<RightPanelResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _detailPanelCompletion = completion;
        void Finish(RightPanelResult result) => completion.TrySetResult(result);

        if (!string.IsNullOrWhiteSpace(closeText))
        {
            var close = CreateActionButton(closeText, "\uE711", accent: false);
            close.Click += (_, _) => Finish(RightPanelResult.Close);
            _detailPanelActions.Children.Add(close);
        }
        if (!string.IsNullOrWhiteSpace(secondaryText))
        {
            var secondary = CreateActionButton(secondaryText, "\uE74D", accent: false);
            secondary.Click += (_, _) => Finish(RightPanelResult.Secondary);
            _detailPanelActions.Children.Add(secondary);
        }
        var primary = CreateActionButton(primaryText, primaryGlyph, accent: true);
        primary.Click += (_, _) => Finish(RightPanelResult.Primary);
        _detailPanelActions.Children.Add(primary);

        SetDetailPanelUnderlyingVisible(false);
        _detailPanel!.Visibility = Visibility.Visible;
        AnimatePanelIn(_detailPanel);
        RightPanelResult result = await completion.Task;
        await AnimatePanelOutAsync(_detailPanel);
        SetDetailPanelUnderlyingVisible(true);
        _detailPanelContent.Content = null;
        _detailPanelActions.Children.Clear();
        _detailPanelOpen = false;
        _detailPanelCompletion = null;
        return result;
    }

    private void CloseDetailPanel()
    {
        if (_detailPanelOpen)
        {
            _detailPanelCompletion?.TrySetResult(RightPanelResult.Close);
        }
    }

    private void ClosePanels()
    {
        CloseRightPanel();
        CloseDetailPanel();
    }

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
        Announce(message);
    }
}
