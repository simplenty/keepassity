// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Keepassity.Mock;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<GroupItem> Groups { get; } = new();
    public ObservableCollection<EntryItem> Entries { get; } = new();

    private readonly CoreClient _core = new();
    private readonly AppSettingsStore _settingsStore = new();
    private readonly SyncStateStore _syncStates = new();
    private readonly CredentialStore _credentials = new();
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly DispatcherTimer _totpTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private readonly DispatcherTimer _autoLockTimer = new();
    private readonly DispatcherTimer _clipboardTimer = new();
    private readonly List<GroupDto> _groupDtos = new();
    private readonly List<GroupDto> _selectableGroups = new();
    private readonly Dictionary<string, string> _groupNames = new();

    private string? _dbPath;
    private string? _selectedGroupUuid;
    private string? _rootGroupUuid;
    private EntryFullDto? _currentFull;
    private EntryItem? _currentSummary;
    private string? _currentTotpCode;
    private string? _lastClipboardText;
    private string? _masterPassword;
    private TrayIcon? _trayIcon;
    private bool _exitRequested;
    private bool _trayHintShown;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private CancellationTokenSource? _searchCts;
    private bool _passwordVisible;
    private bool _busy;
    private bool _suppressNav;
    private int _showGeneration;

    private AppSettings Settings => _settingsStore.Current;

    public MainWindow()
    {
        InitializeComponent();
        bool forceLight = Environment.GetCommandLineArgs().Contains("--light");
        ApplyThemePreference(forceLight ? "Light" : Settings.Theme);
        Title = "keepassity";
        RestoreWindowBounds();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        SearchBox.Visibility = Visibility.Collapsed;
        TrySetMica();
        EntryList.ItemsSource = Entries;
        ParseArguments();
        if (_dbPath is null &&
            !string.IsNullOrWhiteSpace(Settings.LastDatabasePath) &&
            File.Exists(Settings.LastDatabasePath))
        {
            _dbPath = Settings.LastDatabasePath;
        }
        UpdateDatabasePath();

        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += async (_, _) => await UpdateTotpAsync(_showGeneration);
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
        Root.Loaded += Root_Loaded;
        Closed += (_, _) =>
        {
            SaveWindowBounds();
            _trayIcon?.Dispose();
            _trayIcon = null;
            _core.Dispose();
        };
    }

    private void InitializeTray()
    {
        if (Environment.GetCommandLineArgs().Contains("--no-tray", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }
        try
        {
            _trayIcon = new TrayIcon("keepassity", ShowFromTray, LockFromTray, ExitFromTray);
            AppWindow.Closing += AppWindow_Closing;
        }
        catch
        {
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
        AppWindow.Show();
        Activate();
        if (_core.IsOpen)
        {
            _ = LockVaultAsync();
        }
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
            _trayIcon.ShowInfo("keepassity",
                "Still running in the notification area. Use its menu to exit.");
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
                ShowToast("Clipboard cleared", InfoBarSeverity.Informational);
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
        string? devPassword = Environment.GetEnvironmentVariable("KEEPASSITY_MOCK_PASSWORD");
        if (_dbPath is not null && !string.IsNullOrEmpty(devPassword))
        {
            MasterPassword.Password = devPassword;
            await UnlockAsync();
        }
    }

    private void ParseArguments()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "--db");
        if (index >= 0 && index + 1 < args.Length)
        {
            string candidate = args[index + 1];
            if (File.Exists(candidate))
            {
                _dbPath = Path.GetFullPath(candidate);
            }
        }
    }

    private void UpdateDatabasePath()
    {
        DatabasePathText.Text = _dbPath is null
            ? "No database selected"
            : Path.GetFileName(_dbPath);
        ToolTipService.SetToolTip(DatabasePathText, _dbPath ?? "Choose a .kdbx database");
    }

    private void TrySetMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is unavailable on some Windows builds; keep the solid fallback.
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

    /// <summary>Two-pane workspace slides in from the bottom-right corner.</summary>
    private void AnimateWorkspace()
    {
        var transform = new TranslateTransform { X = 56, Y = 72 };
        WorkspacePanel.RenderTransform = transform;
        WorkspacePanel.Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        var duration = new Duration(TimeSpan.FromMilliseconds(320));
        var slideX = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slideX, transform);
        Storyboard.SetTargetProperty(slideX, "X");
        var slideY = new DoubleAnimation { To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(slideY, transform);
        Storyboard.SetTargetProperty(slideY, "Y");
        var fade = new DoubleAnimation { To = 1, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, WorkspacePanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        storyboard.Children.Add(slideX);
        storyboard.Children.Add(slideY);
        storyboard.Children.Add(fade);
        storyboard.Begin();
    }

    // ---------------------------------------------------------------- sync

    private bool IsSyncConfigured =>
        !string.IsNullOrWhiteSpace(Settings.WebDavUrl) &&
        !string.IsNullOrWhiteSpace(Settings.WebDavUsername);

    private void SetSyncStatus(string text)
    {
        SyncNavItem.Content = text;
    }

    private async Task SyncAsync(bool startup = false)
    {
        if (!await _syncGate.WaitAsync(0))
        {
            return;
        }
        try
        {
            if (!_core.IsOpen || _dbPath is null)
            {
                return;
            }
            if (!IsSyncConfigured)
            {
                SetSyncStatus("Local only");
                if (!startup)
                {
                    ShowToast("Configure WebDAV in Settings first.", InfoBarSeverity.Informational);
                }
                return;
            }
            string url = Settings.WebDavUrl!.Trim();
            string username = Settings.WebDavUsername!.Trim();
            string? password = _credentials.LoadPassword(url, username);
            if (password is null)
            {
                SetSyncStatus("No credentials");
                if (!startup)
                {
                    ShowToast("Set the WebDAV password in Settings.", InfoBarSeverity.Warning);
                }
                return;
            }

            SetSyncStatus("Syncing…");
            SyncState state = _syncStates.Load(url, username, _dbPath);
            if (!string.IsNullOrEmpty(state.Etag) || !string.IsNullOrEmpty(state.Sha256))
            {
                await _core.SyncCommitAsync(state.Etag, state.Sha256);
            }
            using var dav = new WebDavClient(url, username, password);
            WebDavFileInfo? metadata = await dav.GetMetadataAsync();
            if (metadata is null)
            {
                await UploadLocalAsync(dav, state, url, username);
                return;
            }

            string remoteMeta = JsonSerializer.Serialize(new
            {
                etag = metadata.ETag,
                size = metadata.Size,
                modified_unix = metadata.ModifiedUnix,
                reliability = metadata.ETag is null ? "degraded" : "full",
            });
            SyncDecisionDto decision = await _core.SyncDecideAsync(remoteMeta, state.LocalDirty);
            switch (decision.Action)
            {
                case "up_to_date":
                    state.Etag = metadata.ETag;
                    state.Size = metadata.Size;
                    state.RemoteModifiedUnix = metadata.ModifiedUnix;
                    state.LocalDirty = false;
                    state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    await _core.SyncCommitAsync(state.Etag, state.Sha256);
                    _syncStates.Save(url, username, _dbPath, state);
                    SetSyncStatus(decision.Degraded ? "Synced (degraded)" : "Synced");
                    break;
                case "upload":
                    await UploadLocalAsync(dav, state, url, username);
                    break;
                case "download":
                    await DownloadRemoteAsync(dav, metadata, state, url, username);
                    break;
                case "download_and_merge":
                case "conflict":
                    await MergeRemoteAsync(dav, metadata, state, url, username);
                    break;
                case "retry":
                    SetSyncStatus("Sync retry");
                    break;
                case "offline":
                default:
                    SetSyncStatus("Offline");
                    if (!startup)
                    {
                        ShowToast($"Sync skipped: {decision.Reason}", InfoBarSeverity.Warning);
                    }
                    break;
            }
        }
        catch (WebDavException ex)
        {
            SetSyncStatus(ex.Conflict ? "Conflict" : "Sync error");
            if (!startup)
            {
                ShowToast(ex.Message, InfoBarSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            SetSyncStatus("Sync error");
            if (!startup)
            {
                ShowToast($"Sync failed: {ex.Message}", InfoBarSeverity.Error);
            }
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task UploadLocalAsync(WebDavClient dav, SyncState state, string url,
        string username)
    {
        byte[] bytes = await _core.SaveBytesAsync();
        await WriteLocalBytesAsync(_dbPath!, bytes);
        string? etag;
        try
        {
            etag = await dav.UploadAsync(bytes, state.Etag);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            WebDavFileInfo? metadata = await dav.GetMetadataAsync();
            if (metadata is null)
            {
                throw;
            }
            await MergeRemoteAsync(dav, metadata, state, url, username);
            return;
        }
        string sha = Sha256Hex(bytes);
        await _core.SyncCommitAsync(etag, sha);
        state.Etag = etag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _syncStates.Save(url, username, _dbPath!, state);
        SetSyncStatus("Synced");
    }

    private async Task DownloadRemoteAsync(WebDavClient dav, WebDavFileInfo metadata,
        SyncState state, string url, string username)
    {
        byte[] bytes = await dav.DownloadAsync();
        await WriteLocalBytesAsync(_dbPath!, bytes);
        await ReopenFromLocalAsync();
        string sha = Sha256Hex(bytes);
        await _core.SyncCommitAsync(metadata.ETag, sha);
        state.Etag = metadata.ETag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        state.RemoteModifiedUnix = metadata.ModifiedUnix;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _syncStates.Save(url, username, _dbPath!, state);
        SetSyncStatus("Synced");
        ShowToast("Downloaded remote changes", InfoBarSeverity.Success);
    }

    private async Task MergeRemoteAsync(WebDavClient dav, WebDavFileInfo metadata,
        SyncState state, string url, string username)
    {
        byte[] remote = await dav.DownloadAsync();
        string? localCopy = CreateConflictCopy();
        string? remoteCopy = CreateRemoteConflictCopy(remote);
        SyncMergeResult merged = await ProbeMergeAsync(remote);
        if (merged.Conflicts.Count == 0)
        {
            await ApplyMergedAsync(dav, metadata, state, url, username, merged,
                localCopy, remoteCopy, autoMerged: true);
            return;
        }

        ConflictChoice choice = await ShowConflictDialogAsync(localCopy, remoteCopy,
            merged.Conflicts);
        switch (choice)
        {
            case ConflictChoice.KeepLocal:
                await KeepLocalConflictAsync(dav, metadata, state, url, username);
                break;
            case ConflictChoice.KeepRemote:
                await KeepRemoteConflictAsync(dav, metadata, state, url, username, remote);
                break;
            case ConflictChoice.KeepBoth:
                await ApplyMergedAsync(dav, metadata, state, url, username, merged,
                    localCopy, remoteCopy, autoMerged: false);
                break;
            default:
                state.LocalDirty = true;
                _syncStates.Save(url, username, _dbPath!, state);
                SetSyncStatus("Conflict");
                ShowToast("Sync paused. Local and remote are unchanged; conflict copies were saved.",
                    InfoBarSeverity.Warning);
                break;
        }
    }

    private enum ConflictChoice
    {
        Cancel,
        KeepLocal,
        KeepRemote,
        KeepBoth,
    }

    private async Task<ConflictChoice> ShowConflictDialogAsync(string? localCopy,
        string? remoteCopy, List<SyncConflictDto> conflicts)
    {
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock
        {
            Text = "Local and remote both changed. Conflict copies have been saved " +
                   "before asking what to do.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"Local copy: {Path.GetFileName(localCopy)}\n" +
                   $"Remote copy: {Path.GetFileName(remoteCopy)}",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        if (conflicts.Count > 0)
        {
            var lines = conflicts.Take(6).Select(conflict =>
            {
                string fields = conflict.Fields.Count == 0
                    ? "entry changed on both sides"
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

        ConflictChoice choice = ConflictChoice.Cancel;
        var keepBoth = new Button
        {
            Content = "Keep both (recommended)",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        var keepLocal = new Button
        {
            Content = "Keep local",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var keepRemote = new Button
        {
            Content = "Keep remote",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var cancel = new Button
        {
            Content = "Cancel and keep both files untouched",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        panel.Children.Add(keepBoth);
        panel.Children.Add(keepLocal);
        panel.Children.Add(keepRemote);
        panel.Children.Add(cancel);
        var dialog = new ContentDialog
        {
            Title = "Sync conflict",
            Content = panel,
            XamlRoot = Root.XamlRoot,
        };
        keepBoth.Click += (_, _) =>
        {
            choice = ConflictChoice.KeepBoth;
            dialog.Hide();
        };
        keepLocal.Click += (_, _) =>
        {
            choice = ConflictChoice.KeepLocal;
            dialog.Hide();
        };
        keepRemote.Click += (_, _) =>
        {
            choice = ConflictChoice.KeepRemote;
            dialog.Hide();
        };
        cancel.Click += (_, _) =>
        {
            choice = ConflictChoice.Cancel;
            dialog.Hide();
        };
        dialog.Opened += (_, _) => keepBoth.Focus(FocusState.Programmatic);
        await dialog.ShowAsync();
        return choice;
    }

    private async Task<SyncMergeResult> ProbeMergeAsync(byte[] remote)
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

    private async Task ApplyMergedAsync(WebDavClient dav, WebDavFileInfo metadata,
        SyncState state, string url, string username, SyncMergeResult merged,
        string? localCopy, string? remoteCopy, bool autoMerged)
    {
        await WriteLocalBytesAsync(_dbPath!, merged.Bytes);
        await ReopenFromLocalAsync();
        string? etag;
        try
        {
            etag = await dav.UploadAsync(merged.Bytes, metadata.ETag);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            state.LocalDirty = true;
            _syncStates.Save(url, username, _dbPath!, state);
            SetSyncStatus("Conflict");
            ShowToast("Remote changed again; merged locally, will retry next sync.",
                InfoBarSeverity.Warning);
            return;
        }
        await CommitSyncedAsync(state, merged.Bytes, etag, metadata, url, username,
            autoMerged ? "Synced" : "Merged");
        string copies = $"local: {Path.GetFileName(localCopy)}; remote: {Path.GetFileName(remoteCopy)}";
        ShowToast(autoMerged
                ? $"Merged remote changes automatically ({copies})."
                : $"Kept both versions; merged and uploaded ({copies}).",
            InfoBarSeverity.Success);
    }

    private async Task KeepLocalConflictAsync(WebDavClient dav, WebDavFileInfo metadata,
        SyncState state, string url, string username)
    {
        byte[] localBytes = await File.ReadAllBytesAsync(_dbPath!);
        string? etag;
        try
        {
            etag = await dav.UploadAsync(localBytes, metadata.ETag);
        }
        catch (WebDavException ex) when (ex.Conflict)
        {
            state.LocalDirty = true;
            _syncStates.Save(url, username, _dbPath!, state);
            SetSyncStatus("Conflict");
            ShowToast("Remote changed again; local version kept, will retry next sync.",
                InfoBarSeverity.Warning);
            return;
        }
        await CommitSyncedAsync(state, localBytes, etag, metadata, url, username, "Synced");
        ShowToast("Kept local version; remote copy was saved.", InfoBarSeverity.Success);
    }

    private async Task KeepRemoteConflictAsync(WebDavClient dav, WebDavFileInfo metadata,
        SyncState state, string url, string username, byte[] remote)
    {
        await WriteLocalBytesAsync(_dbPath!, remote);
        await ReopenFromLocalAsync();
        await CommitSyncedAsync(state, remote, metadata.ETag, metadata, url, username,
            "Synced");
        ShowToast("Kept remote version; local copy was saved.", InfoBarSeverity.Success);
    }

    private async Task CommitSyncedAsync(SyncState state, byte[] bytes, string? etag,
        WebDavFileInfo metadata, string url, string username, string status)
    {
        string sha = Sha256Hex(bytes);
        await _core.SyncCommitAsync(etag, sha);
        state.Etag = etag;
        state.Sha256 = sha;
        state.Size = bytes.Length;
        state.RemoteModifiedUnix = metadata.ModifiedUnix;
        state.LocalDirty = false;
        state.LastSyncUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _syncStates.Save(url, username, _dbPath!, state);
        SetSyncStatus(status);
    }

    private async Task ReopenFromLocalAsync()
    {
        if (_dbPath is null || string.IsNullOrEmpty(_masterPassword))
        {
            return;
        }
        await _core.OpenAsync(_dbPath, _masterPassword);
        _selectedGroupUuid = null;
        await ReloadVaultAsync(_currentFull?.Uuid);
    }

    private string? CreateConflictCopy()
    {
        if (_dbPath is null)
        {
            return null;
        }
        string directory = Path.GetDirectoryName(_dbPath)!;
        string stem = Path.GetFileNameWithoutExtension(_dbPath);
        string extension = Path.GetExtension(_dbPath);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(directory, $"{stem} (conflict {stamp}){extension}");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory, $"{stem} (conflict {stamp}-{suffix++}){extension}");
        }
        File.Copy(_dbPath, path);
        return path;
    }

    private string? CreateRemoteConflictCopy(byte[] remote)
    {
        if (_dbPath is null)
        {
            return null;
        }
        string directory = Path.GetDirectoryName(_dbPath)!;
        string stem = Path.GetFileNameWithoutExtension(_dbPath);
        string extension = Path.GetExtension(_dbPath);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        string path = Path.Combine(directory, $"{stem} (remote conflict {stamp}){extension}");
        int suffix = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(directory,
                $"{stem} (remote conflict {stamp}-{suffix++}){extension}");
        }
        File.WriteAllBytes(path, remote);
        return path;
    }

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private async Task WriteLocalBytesAsync(string path, byte[] bytes)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        string temp = Path.Combine(directory,
            $".{Path.GetFileName(path)}.keepassity-tmp-{Guid.NewGuid():N}");
        await File.WriteAllBytesAsync(temp, bytes);
        if (File.Exists(path))
        {
            BackupService.CreateBackup(path, Settings.BackupRetention, "sync");
            File.Replace(temp, path, path + ".keepassity-bak", true);
        }
        else
        {
            File.Move(temp, path);
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
            ShowToast("No backups yet. They are created before each save.", InfoBarSeverity.Informational);
            return;
        }
        var choices = new ComboBox
        {
            Header = "Backup",
            ItemsSource = backups,
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = $"Restoring backs up the current database first. Keeping {Settings.BackupRetention} copies.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        content.Children.Add(choices);
        var dialog = new ContentDialog
        {
            Title = "Restore backup",
            Content = content,
            PrimaryButtonText = "Restore",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            choices.SelectedItem is not BackupEntry selected)
        {
            return;
        }
        if (string.IsNullOrEmpty(_masterPassword))
        {
            ShowToast("Unlock the vault before restoring a backup.", InfoBarSeverity.Warning);
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
            BackupService.RestoreBytes(_dbPath, selectedBytes, Settings.BackupRetention);
            await ReopenFromLocalAsync();
            MarkLocalDirty();
            ShowToast("Backup restored", InfoBarSeverity.Success);
            if (IsSyncConfigured)
            {
                _ = SyncAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Restore failed: {ex.Message}", InfoBarSeverity.Error);
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
        _dbPath = file.Path;
        UpdateDatabasePath();
        UnlockError.IsOpen = false;
    }

    private async void NewDatabaseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }
        var nameBox = new TextBox
        {
            Header = "Database name",
            Text = "keepassity",
        };
        var pathBox = new TextBox
        {
            Header = "Save as",
            Text = DefaultNewDatabasePath(),
        };
        var browse = new Button
        {
            Content = "Browse…",
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
        var passwordBox = new PasswordBox
        {
            Header = "Master password",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var confirmBox = new PasswordBox
        {
            Header = "Confirm master password",
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
        content.Children.Add(pathRow);
        content.Children.Add(passwordBox);
        content.Children.Add(confirmBox);
        content.Children.Add(errorText);
        content.Children.Add(new TextBlock
        {
            Text = "Creates an empty KDBX 4.0 database with a General group. " +
                   "The master password is not stored.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = "New database",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
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
            string error = ValidateNewDatabase(nameBox.Text, pathBox.Text,
                passwordBox.Password, confirmBox.Password);
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
        string path;
        try
        {
            path = Path.GetFullPath(pathBox.Text.Trim());
        }
        catch
        {
            return;
        }
        string password = passwordBox.Password;
        if (ValidateNewDatabase(databaseName, path, password, confirmBox.Password).Length > 0)
        {
            return;
        }

        _busy = true;
        UnlockError.IsOpen = false;
        NewDatabaseButton.IsEnabled = false;
        ChooseDatabaseButton.IsEnabled = false;
        UnlockButton.IsEnabled = false;
        UnlockProgress.IsActive = true;
        try
        {
            byte[] bytes = await _core.CreateDatabaseAsync(databaseName, password);
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            if (File.Exists(path))
            {
                throw new InvalidOperationException("That file already exists. Choose another path.");
            }
            string temp = path + ".keepassity-new-tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, path);
            _dbPath = path;
            await _core.OpenAsync(path, password);
            _masterPassword = password;
            _selectedGroupUuid = null;
            Settings.LastDatabasePath = path;
            _settingsStore.Save();
            await ReloadVaultAsync(null);
            UpdateDatabasePath();
            ShowMain();
            MasterPassword.Password = string.Empty;
            SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
            ShowToast("Database created", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            UnlockError.Title = "Cannot create database";
            UnlockError.Message = ex.Message;
            UnlockError.IsOpen = true;
        }
        finally
        {
            _busy = false;
            NewDatabaseButton.IsEnabled = true;
            ChooseDatabaseButton.IsEnabled = true;
            UnlockButton.IsEnabled = true;
            UnlockProgress.IsActive = false;
            passwordBox.Password = string.Empty;
            confirmBox.Password = string.Empty;
        }
    }

    private static string ValidateNewDatabase(string name, string path, string password,
        string confirm)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Enter a database name.";
        }
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Choose where to save the database.";
        }
        if (!path.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase))
        {
            return "The file name must end in .kdbx.";
        }
        if (password.Length == 0)
        {
            return "Enter a master password.";
        }
        if (password != confirm)
        {
            return "The master passwords do not match.";
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
        if (_dbPath is null)
        {
            UnlockError.Message = "Choose a .kdbx database first.";
            UnlockError.IsOpen = true;
            return;
        }
        if (MasterPassword.Password.Length == 0)
        {
            UnlockError.Message = "Enter the master password.";
            UnlockError.IsOpen = true;
            MasterPassword.Focus(FocusState.Programmatic);
            return;
        }

        _busy = true;
        UnlockError.IsOpen = false;
        UnlockButton.IsEnabled = false;
        ChooseDatabaseButton.IsEnabled = false;
        UnlockProgress.IsActive = true;
        try
        {
            await _core.OpenAsync(_dbPath, MasterPassword.Password);
            _masterPassword = MasterPassword.Password;
            _selectedGroupUuid = null;
            if (_dbPath is not null)
            {
                Settings.LastDatabasePath = _dbPath;
                _settingsStore.Save();
            }
            await ReloadVaultAsync(null);
            ShowMain();
            MasterPassword.Password = string.Empty;
            _ = SyncAsync(startup: true);
        }
        catch (CoreException ex)
        {
            UnlockError.Title = ex.Code switch
            {
                "BadPassword" => "Wrong master password",
                "CorruptDatabase" => "Database is damaged",
                "UnsupportedFormat" => "Unsupported database",
                _ => "Cannot open database",
            };
            UnlockError.Message = ex.Message;
            UnlockError.IsOpen = true;
        }
        catch (Exception ex)
        {
            UnlockError.Title = "Cannot open database";
            UnlockError.Message = ex.Message;
            UnlockError.IsOpen = true;
        }
        finally
        {
            _busy = false;
            UnlockButton.IsEnabled = true;
            ChooseDatabaseButton.IsEnabled = true;
            UnlockProgress.IsActive = false;
        }
    }

    private void ShowMain()
    {
        UnlockRoot.Visibility = Visibility.Collapsed;
        MainRoot.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        VaultNameText.Text = _dbPath is null
            ? "Vault"
            : Path.GetFileNameWithoutExtension(_dbPath);
        _totpTimer.Start();
        _autoLockTimer.Start();
        TouchActivity();
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        AnimateWorkspace();
        EntryList.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------ vault data

    private async Task ReloadVaultAsync(string? selectEntryUuid)
    {
        List<GroupDto> groups = await _core.ListGroupsAsync();
        List<EntrySummaryDto> allEntries = await _core.ListEntriesAsync();

        _groupDtos.Clear();
        _selectableGroups.Clear();
        _groupNames.Clear();
        _rootGroupUuid = groups.FirstOrDefault(group => group.ParentUuid is null)?.Uuid;
        foreach (GroupDto group in groups)
        {
            _groupDtos.Add(group);
            string name = string.IsNullOrWhiteSpace(group.Name) ? "Vault" : group.Name;
            _groupNames[group.Uuid] = name;
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
        Groups.Add(new GroupItem(null, "All entries", allEntries.Count, "\uE8FD"));
        foreach (GroupDto group in _groupDtos)
        {
            if (group.Uuid == _rootGroupUuid || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }
            int count = allEntries.Count(entry => entry.GroupUuid == group.Uuid);
            Groups.Add(new GroupItem(group.Uuid, group.Name, count, "\uE8B7"));
        }
        PopulateNavigation(_selectedGroupUuid);

        string title = _selectedGroupUuid is null
            ? "All entries"
            : _groupNames.GetValueOrDefault(_selectedGroupUuid, "Entries");
        List<EntrySummaryDto> visible = _selectedGroupUuid is null
            ? allEntries
            : allEntries.Where(entry => entry.GroupUuid == _selectedGroupUuid).ToList();
        PopulateEntries(visible, title);

        if (selectEntryUuid is not null)
        {
            EntryItem? select = Entries.FirstOrDefault(entry => entry.Uuid == selectEntryUuid);
            if (select is not null)
            {
                EntryList.SelectedItem = select;
            }
        }
    }

    private void PopulateNavigation(string? selectedUuid)
    {
        _suppressNav = true;
        NavView.MenuItems.Clear();
        NavigationViewItem? selectedItem = null;
        NavigationViewItem? first = null;
        foreach (GroupItem group in Groups)
        {
            var item = new NavigationViewItem
            {
                Content = group.Name,
                Tag = group.Uuid ?? "all",
                Icon = new FontIcon { Glyph = group.Glyph },
            };
            if (group.CountValue > 0)
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

    private void PopulateEntries(IEnumerable<EntrySummaryDto> entries, string title)
    {
        Entries.Clear();
        foreach (EntrySummaryDto entry in entries)
        {
            string groupName = _groupNames.GetValueOrDefault(entry.GroupUuid, "Vault");
            Entries.Add(new EntryItem(entry, groupName));
        }
        CurrentGroupTitle.Text = title;
        EntryCountText.Text = $"{Entries.Count} {(Entries.Count == 1 ? "entry" : "entries")}";
        if (Entries.Count > 0)
        {
            EntryList.SelectedIndex = 0;
        }
        else
        {
            EntryList.SelectedIndex = -1;
            ClearDetail();
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

    private async Task LoadSelectedGroupAsync()
    {
        string? query = SearchBox.Text.Trim();
        if (query.Length > 0)
        {
            List<SearchMatchDto> matches = await _core.SearchAsync(query);
            PopulateEntries(matches.Select(match => match.Entry), "Search results");
            return;
        }
        List<EntrySummaryDto> entries = await _core.ListEntriesAsync(_selectedGroupUuid);
        string title = _selectedGroupUuid is null
            ? "All entries"
            : _groupNames.GetValueOrDefault(_selectedGroupUuid, "Entries");
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
            ShowToast("Configure WebDAV in Settings first.", InfoBarSeverity.Informational);
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
        if (EntryList.SelectedItem is not EntryItem item)
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

    private async Task ShowEntryAsync(EntryItem summary)
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
        DetailTitle.Text = full.Title;
        string host = Uri.TryCreate(full.Url, UriKind.Absolute, out Uri? uri)
            ? uri.Host
            : full.Url;
        string groupName = _groupNames.GetValueOrDefault(full.GroupUuid, "Vault");
        DetailSubtitle.Text = string.IsNullOrEmpty(host)
            ? groupName
            : $"{host}  ·  {groupName}";
        DetailUsername.Text = full.Username;
        DetailUrl.Text = full.Url;
        DetailNotes.Text = full.Notes;
        DetailPassword.Text = "••••••••••••";
        DetailTags.Text = full.Tags.Count > 0 ? string.Join("  ·  ", full.Tags) : string.Empty;

        CustomFieldDto? custom = full.CustomFields.FirstOrDefault(field => !IsTotpField(field.Key));
        CustomFieldsSection.Visibility = custom is null ? Visibility.Collapsed : Visibility.Visible;
        CustomFieldName.Text = custom?.Key ?? string.Empty;
        CustomFieldValue.Text = custom?.Value ?? string.Empty;

        if (full.Totp is not null)
        {
            TotpSection.Visibility = Visibility.Visible;
            TotpCode.FontSize = 24;
            TotpRemainingText.Visibility = Visibility.Visible;
            TotpProgress.Visibility = Visibility.Visible;
            await UpdateTotpAsync(generation);
        }
        else
        {
            TotpSection.Visibility = Visibility.Visible;
            TotpCode.FontSize = 13;
            TotpCode.Text = "No TOTP configured";
            TotpRemainingText.Text = string.Empty;
            TotpRemainingText.Visibility = Visibility.Collapsed;
            TotpProgress.Value = 0;
            TotpProgress.Visibility = Visibility.Collapsed;
            _currentTotpCode = null;
        }
    }

    private void ClearDetail()
    {
        _currentFull = null;
        _currentSummary = null;
        _currentTotpCode = null;
        DetailTitle.Text = "Select an entry";
        DetailSubtitle.Text = string.Empty;
        DetailUsername.Text = string.Empty;
        DetailPassword.Text = string.Empty;
        DetailUrl.Text = string.Empty;
        DetailTags.Text = string.Empty;
        DetailNotes.Text = string.Empty;
        CustomFieldsSection.Visibility = Visibility.Collapsed;
        TotpSection.Visibility = Visibility.Collapsed;
        TotpProgress.Value = 0;
        TotpRemainingText.Text = string.Empty;
        TotpCode.Text = "—";
    }

    private async Task UpdateTotpAsync(int generation)
    {
        EntryFullDto? full = _currentFull;
        if (full is null || full.Totp is null)
        {
            TotpCode.FontSize = 13;
            TotpCode.Text = "No TOTP configured";
            TotpRemainingText.Text = string.Empty;
            TotpRemainingText.Visibility = Visibility.Collapsed;
            TotpProgress.Value = 0;
            TotpProgress.Visibility = Visibility.Collapsed;
            _currentTotpCode = null;
            return;
        }
        TotpCode.FontSize = 24;
        TotpRemainingText.Visibility = Visibility.Visible;
        TotpProgress.Visibility = Visibility.Visible;
        TotpCodeDto? code = await _core.TotpCodeAsync(full.Uuid);
        if (generation != _showGeneration || _currentFull?.Uuid != full.Uuid)
        {
            return;
        }
        if (code is null)
        {
            TotpCode.FontSize = 13;
            TotpCode.Text = "No TOTP configured";
            TotpRemainingText.Text = string.Empty;
            TotpRemainingText.Visibility = Visibility.Collapsed;
            TotpProgress.Value = 0;
            TotpProgress.Visibility = Visibility.Collapsed;
            _currentTotpCode = null;
            return;
        }
        _currentTotpCode = code.Code;
        TotpCode.Text = FormatTotp(code.Code, code.Digits);
        TotpRemainingText.Text = $"Refreshes in {code.Remaining}s";
        TotpProgress.Maximum = code.Period;
        TotpProgress.Value = Math.Max(0, code.Period - code.Remaining);
    }

    private static string FormatTotp(string code, int digits)
    {
        if (digits == 8 && code.Length == 8)
        {
            return $"{code[..4]} {code[4..]}";
        }
        if (code.Length == 6)
        {
            return $"{code[..3]} {code[3..]}";
        }
        return code;
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
        DetailPassword.Text = _passwordVisible
            ? _currentFull.PasswordSecret
            : "••••••••••••";
    }

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.Username, "Username copied");
        }
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.PasswordSecret, "Password copied");
        }
    }

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentFull is not null)
        {
            CopyText(_currentFull.Url, "URL copied");
        }
    }

    private void CopyTotpButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_currentTotpCode))
        {
            CopyText(_currentTotpCode, "One-time code copied");
        }
    }

    private void CopyCustomFieldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(CustomFieldValue.Text))
        {
            CopyText(CustomFieldValue.Text, "Custom field copied");
        }
    }

    private void CopyText(string text, string label)
    {
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        _lastClipboardText = text;
        _clipboardTimer.Stop();
        if (Settings.ClearClipboardAfter30Seconds)
        {
            _clipboardTimer.Start();
            ShowToast($"{label} — clears in 30 seconds", InfoBarSeverity.Success);
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
            Header = "Group",
            ItemsSource = _selectableGroups,
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        groupChoice.SelectedItem = _selectableGroups.FirstOrDefault(group =>
            group.Uuid == (_selectedGroupUuid ?? _selectableGroups.FirstOrDefault()?.Uuid));

        var title = new TextBox { Header = "Title", PlaceholderText = "Entry name" };
        var username = new TextBox { Header = "Username", PlaceholderText = "name@example.com" };
        var password = new PasswordBox { Header = "Password", PasswordRevealMode = PasswordRevealMode.Peek };
        var url = new TextBox { Header = "Website", PlaceholderText = "https://" };
        var notes = new TextBox
        {
            Header = "Notes",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(groupChoice);
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(password);
        content.Children.Add(url);
        content.Children.Add(notes);
        var dialog = new ContentDialog
        {
            Title = "New entry",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            groupChoice.SelectedItem is not GroupDto group)
        {
            return;
        }
        try
        {
            string uuid = await _core.CreateEntryAsync(group.Uuid,
                string.IsNullOrWhiteSpace(title.Text) ? "Untitled entry" : title.Text.Trim(),
                username.Text.Trim(), password.Password, url.Text.Trim(), notes.Text);
            await SaveAsync();
            await ReloadVaultAsync(uuid);
            ShowToast("Entry created and saved", InfoBarSeverity.Success);
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
        var title = new TextBox { Header = "Title", Text = _currentFull.Title };
        var username = new TextBox { Header = "Username", Text = _currentFull.Username };
        var password = new PasswordBox
        {
            Header = "Password",
            Password = _currentFull.PasswordSecret,
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var url = new TextBox { Header = "Website", Text = _currentFull.Url };
        var tags = new TextBox
        {
            Header = "Tags (comma-separated)",
            Text = string.Join(", ", _currentFull.Tags),
        };
        var notes = new TextBox
        {
            Header = "Notes",
            Text = _currentFull.Notes,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 84,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(password);
        content.Children.Add(url);
        content.Children.Add(tags);
        content.Children.Add(notes);
        var dialog = new ContentDialog
        {
            Title = "Edit entry",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        try
        {
            List<string> tagList = tags.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            await _core.UpdateEntryAsync(_currentFull.Uuid, title.Text.Trim(),
                username.Text.Trim(), password.Password, url.Text.Trim(), notes.Text, tagList);
            await SaveAsync();
            await ReloadVaultAsync(_currentFull.Uuid);
            ShowToast("Entry updated and saved", InfoBarSeverity.Success);
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
        var choices = new ComboBox
        {
            Header = "Move to group",
            ItemsSource = _selectableGroups,
            DisplayMemberPath = "Name",
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        choices.SelectedItem = _selectableGroups.FirstOrDefault(group =>
            group.Uuid == _currentFull.GroupUuid) ?? _selectableGroups[0];
        var dialog = new ContentDialog
        {
            Title = "Move entry",
            Content = choices,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            choices.SelectedItem is not GroupDto target ||
            _currentFull is null)
        {
            return;
        }
        string uuid = _currentFull.Uuid;
        try
        {
            await _core.MoveEntryAsync(uuid, target.Uuid);
            await SaveAsync();
            _selectedGroupUuid = target.Uuid;
            await ReloadVaultAsync(uuid);
            ShowToast($"Moved to {target.Name}", InfoBarSeverity.Success);
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
            Title = "Delete entry?",
            Content = $"\"{title}\" will be moved to the recycle bin.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
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
            ShowToast("Entry moved to the recycle bin", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void GeneratePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_core.IsOpen)
        {
            return;
        }
        try
        {
            PasswordGenerateDto generated = await _core.GeneratePasswordAsync();
            var preview = new TextBox
            {
                Text = generated.Password,
                IsReadOnly = true,
                FontFamily = new FontFamily("Consolas"),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(preview);
            content.Children.Add(new TextBlock
            {
                Text = $"{generated.EntropyBits:F0} bits of entropy",
                FontSize = 11,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            var dialog = new ContentDialog
            {
                Title = "Generated password",
                Content = content,
                PrimaryButtonText = _currentFull is null ? "Copy" : "Use password",
                SecondaryButtonText = _currentFull is null ? null : "Copy",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };
            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (_currentFull is null)
                {
                    CopyText(generated.Password, "Generated password copied");
                }
                else
                {
                    EntryFullDto current = _currentFull;
                    await _core.UpdateEntryAsync(current.Uuid, current.Title, current.Username,
                        generated.Password, current.Url, current.Notes, current.Tags);
                    await SaveAsync();
                    await ReloadVaultAsync(current.Uuid);
                    ShowToast("Password replaced and saved", InfoBarSeverity.Success);
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                CopyText(generated.Password, "Generated password copied");
            }
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
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
            Header = "Base32 secret",
            Text = existing?.SecretBase32 ?? string.Empty,
            FontFamily = new FontFamily("Consolas"),
        };
        var algorithm = new ComboBox
        {
            Header = "Algorithm",
            ItemsSource = new[] { "SHA1", "SHA256", "SHA512" },
            SelectedItem = existing?.Algorithm ?? "SHA1",
        };
        var digits = new ComboBox
        {
            Header = "Digits",
            ItemsSource = new[] { "6", "8" },
            SelectedItem = (existing?.Digits ?? 6).ToString(),
        };
        var period = new NumberBox
        {
            Header = "Period (seconds)",
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
        var importFile = new Button { Content = "Import QR image…" };
        var importClipboard = new Button { Content = "Paste QR / otpauth…" };
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
                importStatus.Text = "No otpauth QR code found. Paste a Base32 secret instead.";
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
                    ? "QR imported. Review the parameters and save."
                    : $"Imported {account}. Review and save.";
            }
            catch (Exception ex)
            {
                importStatus.Text = $"Could not read otpauth URI: {ex.Message}";
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
                importStatus.Text = $"QR image failed: {ex.Message}";
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
                importStatus.Text = $"Clipboard QR failed: {ex.Message}";
            }
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(importRow);
        content.Children.Add(importStatus);
        content.Children.Add(secret);
        content.Children.Add(algorithm);
        content.Children.Add(digits);
        content.Children.Add(period);
        var dialog = new ContentDialog
        {
            Title = existing is null ? "Set up TOTP" : "Edit TOTP",
            Content = content,
            PrimaryButtonText = "Save",
            SecondaryButtonText = existing is null ? null : "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        ContentDialogResult result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Secondary)
        {
            try
            {
                await _core.TotpDeleteAsync(current.Uuid);
                await SaveAsync();
                await ReloadVaultAsync(current.Uuid);
                ShowToast("TOTP removed", InfoBarSeverity.Informational);
            }
            catch (Exception ex)
            {
                ShowToast(ex.Message, InfoBarSeverity.Error);
            }
            return;
        }
        if (result != ContentDialogResult.Primary)
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
            ShowToast("TOTP saved", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast(ex.Message, InfoBarSeverity.Error);
        }
    }

    // ------------------------------------------------------------ save/lock

    private async Task SaveAsync(bool silent = false, bool syncAfterSave = true)
    {
        if (_dbPath is null)
        {
            return;
        }
        try
        {
            await _core.SaveAsync(_dbPath, Settings.BackupRetention);
            MarkLocalDirty();
            if (!silent)
            {
                ShowToast("Saved", InfoBarSeverity.Success);
            }
            if (syncAfterSave && Settings.SyncOnSave && IsSyncConfigured)
            {
                _ = SyncAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}", InfoBarSeverity.Error);
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
        _busy = true;
        try
        {
            if (_core.IsOpen)
            {
                await SaveAsync(silent: true, syncAfterSave: false);
                await _core.LockAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lock failed: {ex.Message}", InfoBarSeverity.Error);
            _busy = false;
            return;
        }
        _autoLockTimer.Stop();
        _totpTimer.Stop();
        _currentFull = null;
        _currentSummary = null;
        _currentTotpCode = null;
        Entries.Clear();
        Groups.Clear();
        NavView.MenuItems.Clear();
        ClearDetail();
        MainRoot.Visibility = Visibility.Collapsed;
        UnlockRoot.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Collapsed;
        SearchBox.Text = string.Empty;
        MasterPassword.Password = string.Empty;
        _masterPassword = null;
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        UpdateDatabasePath();
        _busy = false;
        MasterPassword.Focus(FocusState.Programmatic);
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = new RadioButtons
        {
            Header = "Appearance",
            Items =
            {
                "Use system setting",
                "Light",
                "Dark",
            },
            SelectedIndex = Settings.Theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0,
            },
        };
        var clipboard = new ToggleSwitch
        {
            Header = "Clear copied secrets after 30 seconds",
            OnContent = "On",
            OffContent = "Off",
            IsOn = Settings.ClearClipboardAfter30Seconds,
        };
        var autoLock = new ToggleSwitch
        {
            Header = "Lock the vault after inactivity",
            OnContent = "On",
            OffContent = "Off",
            IsOn = Settings.AutoLockEnabled,
        };
        var minutes = new NumberBox
        {
            Header = "Inactivity minutes before lock",
            Value = Settings.AutoLockMinutes,
            Minimum = 1,
            Maximum = 240,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var retention = new NumberBox
        {
            Header = "Backup copies to keep",
            Value = Settings.BackupRetention,
            Minimum = 1,
            Maximum = 200,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var webdavUrl = new TextBox
        {
            Header = "WebDAV file URL",
            Text = Settings.WebDavUrl ?? string.Empty,
            PlaceholderText = "https://dav.example.com/keepassity.kdbx",
        };
        var webdavUser = new TextBox
        {
            Header = "WebDAV username",
            Text = Settings.WebDavUsername ?? string.Empty,
        };
        var webdavPassword = new PasswordBox
        {
            Header = "WebDAV password (leave blank to keep the stored one)",
            PasswordRevealMode = PasswordRevealMode.Peek,
        };
        var syncOnSave = new ToggleSwitch
        {
            Header = "Sync after saving changes",
            OnContent = "On",
            OffContent = "Off",
            IsOn = Settings.SyncOnSave,
        };
        theme.SelectionChanged += (_, _) =>
        {
            Settings.Theme = theme.SelectedIndex switch
            {
                1 => "Light",
                2 => "Dark",
                _ => "System",
            };
            ApplyThemePreference(Settings.Theme);
            _settingsStore.Save();
        };
        clipboard.Toggled += (_, _) =>
        {
            Settings.ClearClipboardAfter30Seconds = clipboard.IsOn;
            _settingsStore.Save();
        };
        autoLock.Toggled += (_, _) =>
        {
            Settings.AutoLockEnabled = autoLock.IsOn;
            _settingsStore.Save();
        };
        minutes.ValueChanged += (_, _) =>
        {
            if (!double.IsNaN(minutes.Value))
            {
                Settings.AutoLockMinutes = Math.Clamp((int)minutes.Value, 1, 240);
                _settingsStore.Save();
            }
        };
        retention.ValueChanged += (_, _) =>
        {
            if (!double.IsNaN(retention.Value))
            {
                Settings.BackupRetention = Math.Clamp((int)retention.Value, 1, 200);
                _settingsStore.Save();
            }
        };
        syncOnSave.Toggled += (_, _) =>
        {
            Settings.SyncOnSave = syncOnSave.IsOn;
            _settingsStore.Save();
        };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(theme);
        content.Children.Add(clipboard);
        content.Children.Add(autoLock);
        content.Children.Add(minutes);
        content.Children.Add(retention);
        content.Children.Add(new TextBlock
        {
            Text = "WebDAV",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });
        content.Children.Add(webdavUrl);
        content.Children.Add(webdavUser);
        content.Children.Add(webdavPassword);
        content.Children.Add(syncOnSave);
        content.Children.Add(new TextBlock
        {
            Text = "The URL must point to the .kdbx file. The WebDAV password is stored " +
                   "with Windows DPAPI, not in the plain settings file.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }
        string url = webdavUrl.Text.Trim();
        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
             (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
        {
            ShowToast("WebDAV URL must be an absolute http(s) file URL.", InfoBarSeverity.Error);
            return;
        }
        string username = webdavUser.Text.Trim();
        Settings.WebDavUrl = url.Length == 0 ? null : url;
        Settings.WebDavUsername = username.Length == 0 ? null : username;
        try
        {
            if (webdavPassword.Password.Length > 0)
            {
                if (string.IsNullOrEmpty(Settings.WebDavUrl) ||
                    string.IsNullOrEmpty(Settings.WebDavUsername))
                {
                    ShowToast("Enter the WebDAV URL and username before the password.",
                        InfoBarSeverity.Warning);
                    return;
                }
                _credentials.SavePassword(Settings.WebDavUrl, Settings.WebDavUsername,
                    webdavPassword.Password);
            }
            _settingsStore.Save();
        }
        catch (Exception ex)
        {
            ShowToast($"Cannot store WebDAV credential: {ex.Message}", InfoBarSeverity.Error);
            return;
        }
        SetSyncStatus(IsSyncConfigured ? "Not synced" : "Local only");
        if (IsSyncConfigured)
        {
            ShowToast("WebDAV settings saved. Syncing…", InfoBarSeverity.Informational);
            _ = SyncAsync(startup: true);
        }
    }

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}

public sealed class GroupItem
{
    public GroupItem(string? uuid, string name, int count, string glyph)
    {
        Uuid = uuid;
        Name = name;
        Count = count.ToString();
        Glyph = glyph;
    }

    public string? Uuid { get; }
    public string Name { get; }
    public string Count { get; }
    public string Glyph { get; }
    public int CountValue => int.TryParse(Count, out int value) ? value : 0;
}

public sealed class EntryItem
{
    private static readonly Color[] Palette =
    {
        Color.FromArgb(255, 0, 120, 212),
        Color.FromArgb(255, 135, 100, 184),
        Color.FromArgb(255, 16, 124, 16),
        Color.FromArgb(255, 202, 80, 16),
        Color.FromArgb(255, 0, 153, 188),
        Color.FromArgb(255, 197, 15, 31),
    };

    public EntryItem(EntrySummaryDto entry, string groupName)
    {
        Uuid = entry.Uuid;
        GroupUuid = entry.GroupUuid;
        Title = entry.Title;
        Username = entry.Username;
        Url = entry.Url;
        GroupName = groupName;
        HasTotp = entry.HasTotp;
        Initial = entry.Title.Length > 0 ? entry.Title[..1].ToUpperInvariant() : "?";
        int hash = 0;
        foreach (char character in entry.Uuid)
        {
            hash = unchecked((hash * 31) + character);
        }
        AvatarBrush = new SolidColorBrush(Palette[(hash & int.MaxValue) % Palette.Length]);
    }

    public string Uuid { get; }
    public string GroupUuid { get; }
    public string Title { get; }
    public string Username { get; }
    public string Url { get; }
    public string GroupName { get; }
    public bool HasTotp { get; }
    public string Initial { get; }
    public Brush AvatarBrush { get; }
    public Visibility TotpVisibility => HasTotp ? Visibility.Visible : Visibility.Collapsed;
}
