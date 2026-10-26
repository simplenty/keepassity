// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Collections.ObjectModel;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace Keepassity.Mock;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<GroupItem> Groups { get; } = new();
    public ObservableCollection<EntryItem> Entries { get; } = new();

    private readonly CoreClient _core = new();
    private readonly DispatcherTimer _totpTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private readonly List<GroupDto> _groupDtos = new();
    private readonly List<GroupDto> _selectableGroups = new();
    private readonly Dictionary<string, string> _groupNames = new();

    private string? _dbPath;
    private string? _selectedGroupUuid;
    private string? _rootGroupUuid;
    private EntryFullDto? _currentFull;
    private EntryItem? _currentSummary;
    private string? _currentTotpCode;
    private CancellationTokenSource? _searchCts;
    private bool _passwordVisible;
    private bool _busy;
    private bool _suppressNav;
    private int _showGeneration;

    public MainWindow()
    {
        InitializeComponent();
        if (Environment.GetCommandLineArgs().Contains("--light"))
        {
            Root.RequestedTheme = ElementTheme.Light;
        }
        Title = "keepassity";
        AppWindow.MoveAndResize(new RectInt32(28, 40, 1650, 970));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        SearchBox.Visibility = Visibility.Collapsed;
        TrySetMica();
        EntryList.ItemsSource = Entries;
        ParseArguments();
        UpdateDatabasePath();

        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += async (_, _) => await UpdateTotpAsync(_showGeneration);
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.IsOpen = false;
        };
        Root.Loaded += Root_Loaded;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
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
            _selectedGroupUuid = null;
            await ReloadVaultAsync(null);
            ShowMain();
            MasterPassword.Password = string.Empty;
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
        ShowToast("Local database mode. Sync is not configured in this build.",
            InfoBarSeverity.Informational);
        await Task.CompletedTask;
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
        ShowToast(label, InfoBarSeverity.Success);
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
        var content = new StackPanel { Spacing = 10 };
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

    private async Task SaveAsync(bool silent = false)
    {
        if (_dbPath is null)
        {
            return;
        }
        try
        {
            await _core.SaveAsync(_dbPath);
            if (!silent)
            {
                ShowToast("Saved", InfoBarSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Save failed: {ex.Message}", InfoBarSeverity.Error);
            throw;
        }
    }

    private async void LockButton_Click(object sender, RoutedEventArgs e)
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
                await SaveAsync(silent: true);
                await _core.LockAsync();
            }
        }
        catch (Exception ex)
        {
            ShowToast($"Lock failed: {ex.Message}", InfoBarSeverity.Error);
            _busy = false;
            return;
        }
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
            SelectedIndex = Root.RequestedTheme switch
            {
                ElementTheme.Light => 1,
                ElementTheme.Dark => 2,
                _ => 0,
            },
        };
        theme.SelectionChanged += (_, _) =>
        {
            Root.RequestedTheme = theme.SelectedIndex switch
            {
                1 => ElementTheme.Light,
                2 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(theme);
        content.Children.Add(new TextBlock
        {
            Text = "Sync, backup and credential settings are not implemented in this build.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        var dialog = new ContentDialog
        {
            Title = "Settings",
            Content = content,
            CloseButtonText = "Done",
            XamlRoot = Root.XamlRoot,
        };
        await dialog.ShowAsync();
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
