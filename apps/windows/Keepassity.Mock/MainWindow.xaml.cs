using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Collections.ObjectModel;
using System.Numerics;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using Windows.UI;

namespace Keepassity.Mock;

public sealed partial class MainWindow : Window
{
    public ObservableCollection<GroupItem> Groups { get; } = new();
    public ObservableCollection<EntryItem> Entries { get; } = new();

    private readonly List<EntryItem> _allEntries = new();
    private readonly DispatcherTimer _totpTimer = new();
    private readonly DispatcherTimer _toastTimer = new();
    private string? _selectedGroup;
    private bool _passwordVisible;

    public MainWindow()
    {
        InitializeComponent();
        Title = "keepassity — UI Prototype";
        AppWindow.MoveAndResize(new RectInt32(28, 40, 1650, 970));
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragArea);
        ApplyTitleBarTheme();
        Root.ActualThemeChanged += (_, _) => ApplyTitleBarTheme();
        SearchBox.Visibility = Visibility.Collapsed;
        TrySetMica();
        WorkspacePanel.Shadow = new ThemeShadow();
        WorkspacePanel.Translation = new Vector3(0, 0, 32);

        SeedMockData();
        GroupList.ItemsSource = Groups;
        EntryList.ItemsSource = Entries;
        GroupList.SelectedIndex = 0;
        FilterEntries(null);

        _totpTimer.Interval = TimeSpan.FromSeconds(1);
        _totpTimer.Tick += (_, _) => UpdateTotp();
        _toastTimer.Interval = TimeSpan.FromSeconds(4);
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.IsOpen = false;
        };

        if (Environment.GetCommandLineArgs().Contains("--unlocked"))
        {
            UnlockRoot.Visibility = Visibility.Collapsed;
            MainRoot.Visibility = Visibility.Visible;
            SearchBox.Visibility = Visibility.Visible;
            _totpTimer.Start();
            UpdateTotp();
            AnimateWorkspace();
        }
    }

    private void TrySetMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica is unavailable on some Windows builds; the solid page colour
            // remains readable.
        }
    }

    private void ApplyTitleBarTheme()
    {
        var titleBar = AppWindow.TitleBar;
        var light = Root.ActualTheme == ElementTheme.Light;
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

    private void SeedMockData()
    {
        Groups.Add(new GroupItem("All entries", "12", "\uE8FD", null));
        Groups.Add(new GroupItem("Personal", "5", "\uE77B", "Personal"));
        Groups.Add(new GroupItem("Work", "4", "\uE821", "Work"));
        Groups.Add(new GroupItem("Finance", "2", "\uE825", "Finance"));
        Groups.Add(new GroupItem("Social", "1", "\uE8D4", "Social"));
        Groups.Add(new GroupItem("Archive", "3", "\uE7B8", "Archive"));

        AddEntry("GitHub", "alex@example.com", "https://github.com", "Work", "2FA", true,
            "Personal account. Recovery codes are stored in the off-line backup.");
        AddEntry("Microsoft 365", "alex.work@contoso.com", "https://outlook.office.com", "Work", "work", true,
            "Work tenant. MFA is enforced by the organisation.");
        AddEntry("Proton Mail", "alex@proton.me", "https://mail.proton.me", "Personal", "personal", true,
            "Primary private mailbox. Recovery phrase is in the safe.");
        AddEntry("Bank of Example", "alex_private", "https://bank.example.com", "Finance", "finance", false,
            "Online banking. Never share the one-time codes.");
        AddEntry("Figma", "alex@example.com", "https://figma.com", "Work", "design", false,
            "Design team workspace.");
        AddEntry("Netflix", "family@example.com", "https://netflix.com", "Personal", "media", false,
            "Family plan, 4 screens.");
        AddEntry("X / Twitter", "@alex", "https://x.com", "Social", "social", true,
            "Read-only account. 2FA enabled.");
        AddEntry("Amazon", "alex@example.com", "https://amazon.com", "Personal", "shopping", false,
            "Default shipping address is in the old vault.");
        AddEntry("Vanguard", "alex_invest", "https://vanguard.com", "Finance", "finance", true,
            "Long-term index funds. 2FA enabled.");
        AddEntry("Slack", "alex@contoso.com", "https://contoso.slack.com", "Work", "work", false,
            "Design workspace.");
        AddEntry("Steam", "alex_games", "https://store.steampowered.com", "Personal", "media", true,
            "Steam Guard is enabled.");
        AddEntry("Old forum", "alex2008", "https://forum.example.org", "Archive", "archive", false,
            "No longer used. Kept for reference.");
    }

    private void AddEntry(string title, string username, string url, string group, string tag,
        bool hasTotp, string notes)
    {
        var entry = new EntryItem(title, username, url, group, tag, hasTotp, notes);
        _allEntries.Add(entry);
        Entries.Add(entry);
    }

    private void UnlockButton_Click(object sender, RoutedEventArgs e)
    {
        if (MasterPassword.Password.Length == 0)
        {
            UnlockError.Message = "Enter a password first. In this prototype any value works.";
            UnlockError.IsOpen = true;
            MasterPassword.Focus(FocusState.Programmatic);
            return;
        }
        UnlockError.IsOpen = false;
        UnlockRoot.Visibility = Visibility.Collapsed;
        MainRoot.Visibility = Visibility.Visible;
        SearchBox.Visibility = Visibility.Visible;
        _totpTimer.Start();
        UpdateTotp();
        AnimateWorkspace();
        EntryList.Focus(FocusState.Programmatic);
    }

    private void MasterPassword_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            UnlockButton_Click(sender, e);
        }
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

    private void LockButton_Click(object sender, RoutedEventArgs e)
    {
        _totpTimer.Stop();
        _passwordVisible = false;
        DetailPassword.Text = "••••••••••••";
        MasterPassword.Password = string.Empty;
        MainRoot.Visibility = Visibility.Collapsed;
        SearchBox.Visibility = Visibility.Collapsed;
        UnlockRoot.Visibility = Visibility.Visible;
        MasterPassword.Focus(FocusState.Programmatic);
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is GroupItem group)
        {
            _selectedGroup = group.Category;
            FilterEntries(group.Name == "All entries" ? null : group.Category);
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            FilterEntries(_selectedGroup);
        }
    }

    private void FilterEntries(string? category)
    {
        var query = SearchBox?.Text?.Trim() ?? string.Empty;
        var filtered = _allEntries.Where(entry =>
            (string.IsNullOrEmpty(category) || entry.Category == category) &&
            (query.Length == 0 ||
             entry.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             entry.Username.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             entry.Url.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             entry.Notes.Contains(query, StringComparison.OrdinalIgnoreCase)));
        Entries.Clear();
        foreach (var entry in filtered)
        {
            Entries.Add(entry);
        }
        CurrentGroupTitle.Text = string.IsNullOrEmpty(category) ? "All entries" : category;
        EntryCountText.Text = $"{Entries.Count} {(Entries.Count == 1 ? "entry" : "entries")}";
        if (Entries.Count > 0)
        {
            EntryList.SelectedIndex = 0;
        }
        else
        {
            EntryList.SelectedIndex = -1;
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryItem entry)
        {
            ShowEntry(entry);
        }
    }

    private void ShowEntry(EntryItem entry)
    {
        DetailTitle.Text = entry.Title;
        DetailSubtitle.Text = $"{entry.Host}  ·  {entry.Category}";
        DetailUsername.Text = entry.Username;
        DetailUrl.Text = entry.Url;
        DetailNotes.Text = entry.Notes;
        DetailPassword.Text = _passwordVisible ? entry.Password : "••••••••••••";
        DetailTags.Text = $"{entry.Tag}  ·  {entry.Category}";
        TotpCode.Text = FormatCode(entry);
        UpdateTotp();
    }

    private void UpdateTotp()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = 30 - (int)(now % 30);
        var code = ((now / 30) * 73939 + 142857) % 1_000_000;
        TotpCode.Text = $"{code / 1000:D3} {code % 1000:D3}";
        TotpRemainingText.Text = $"Refreshes in {remaining}s";
    }

    private static string FormatCode(EntryItem entry) => entry.HasTotp ? "428 913" : "No TOTP";

    private void UpdateAutoLock()
    {
        // Removed from the compact system-style UI.
    }

    private void RevealPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryItem entry)
        {
            return;
        }
        _passwordVisible = !_passwordVisible;
        DetailPassword.Text = _passwordVisible ? entry.Password : "••••••••••••";
    }

    private void CopyUsernameButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryItem entry)
        {
            CopyText(entry.Username, "Username copied");
        }
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryItem entry)
        {
            CopyText(entry.Password, "Password copied — clears in 30 seconds (prototype)");
        }
    }

    private void CopyUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryItem entry)
        {
            CopyText(entry.Url, "URL copied");
        }
    }

    private void CopyRecoveryButton_Click(object sender, RoutedEventArgs e)
    {
        CopyText("alex.recovery@proton.me", "Recovery email copied");
    }

    private void CopyTotpButton_Click(object sender, RoutedEventArgs e)
    {
        CopyText(TotpCode.Text.Replace(" ", string.Empty), "One-time code copied");
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
        if (EntryList.SelectedItem is EntryItem entry &&
            Uri.TryCreate(entry.Url, UriKind.Absolute, out var uri))
        {
            await Launcher.LaunchUriAsync(uri);
        }
    }

    private async void NewEntryButton_Click(object sender, RoutedEventArgs e)
    {
        var title = new TextBox { Header = "Title", PlaceholderText = "Entry name" };
        var username = new TextBox { Header = "Username", PlaceholderText = "name@example.com" };
        var password = new PasswordBox { Header = "Password", PasswordRevealMode = PasswordRevealMode.Peek };
        var url = new TextBox { Header = "Website", PlaceholderText = "https://" };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(password);
        content.Children.Add(url);
        var dialog = new ContentDialog
        {
            Title = "New entry",
            Content = content,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            var entry = new EntryItem(
                string.IsNullOrWhiteSpace(title.Text) ? "Untitled entry" : title.Text.Trim(),
                username.Text.Trim(),
                string.IsNullOrWhiteSpace(url.Text) ? "https://example.com" : url.Text.Trim(),
                _selectedGroup ?? "Personal",
                "new",
                false,
                "Created in the UI prototype.");
            _allEntries.Add(entry);
            FilterEntries(_selectedGroup);
            EntryList.SelectedItem = entry;
            ShowToast("Entry created (prototype only)", InfoBarSeverity.Success);
        }
    }

    private async void EditEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryItem entry)
        {
            return;
        }
        var title = new TextBox { Header = "Title", Text = entry.Title };
        var username = new TextBox { Header = "Username", Text = entry.Username };
        var url = new TextBox { Header = "Website", Text = entry.Url };
        var notes = new TextBox
        {
            Header = "Notes",
            Text = entry.Notes,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 96,
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(title);
        content.Children.Add(username);
        content.Children.Add(url);
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
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            entry.Update(title.Text.Trim(), username.Text.Trim(), url.Text.Trim(), notes.Text);
            ShowEntry(entry);
            FilterEntries(_selectedGroup);
            ShowToast("Entry updated (prototype only)", InfoBarSeverity.Success);
        }
    }

    private async void MoveEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryItem entry)
        {
            return;
        }
        var choices = new ComboBox
        {
            Header = "Move to group",
            ItemsSource = Groups.Where(group => group.Category is not null)
                .Select(group => group.Name)
                .ToList(),
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "Move entry",
            Content = choices,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary &&
            choices.SelectedItem is string target)
        {
            entry.MoveTo(target);
            FilterEntries(_selectedGroup);
            EntryList.SelectedItem = entry;
            ShowToast($"Moved to {target}", InfoBarSeverity.Success);
        }
    }

    private async void GeneratePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%^&*";
        var random = new Random();
        var generated = new string(Enumerable.Range(0, 20)
            .Select(_ => alphabet[random.Next(alphabet.Length)])
            .ToArray());
        var preview = new TextBox
        {
            Text = generated,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dialog = new ContentDialog
        {
            Title = "Generated password",
            Content = preview,
            PrimaryButtonText = "Use password",
            SecondaryButtonText = "Copy",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            if (EntryList.SelectedItem is EntryItem entry)
            {
                entry.SetPassword(generated);
                _passwordVisible = true;
                ShowEntry(entry);
                ShowToast("Password replaced (prototype only)", InfoBarSeverity.Success);
            }
        }
        else if (result == ContentDialogResult.Secondary)
        {
            CopyText(generated, "Generated password copied");
        }
    }

    private async void TotpSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var secret = new TextBox
        {
            Header = "Base32 secret",
            Text = "JBSWY3DPEHPK3PXP",
            FontFamily = new FontFamily("Consolas"),
        };
        var digits = new ComboBox { Header = "Digits", ItemsSource = new[] { "6", "8" }, SelectedIndex = 0 };
        var period = new NumberBox { Header = "Period (seconds)", Value = 30, Minimum = 15, Maximum = 120 };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(secret);
        content.Children.Add(digits);
        content.Children.Add(period);
        var dialog = new ContentDialog
        {
            Title = "TOTP setup",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ShowToast("TOTP configuration saved (prototype only)", InfoBarSeverity.Success);
        }
    }

    private async void DeleteEntryButton_Click(object sender, RoutedEventArgs e)    {
        if (EntryList.SelectedItem is not EntryItem entry)
        {
            return;
        }
        var dialog = new ContentDialog
        {
            Title = "Delete entry?",
            Content = $"\"{entry.Title}\" will be moved to the recycle bin.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Root.XamlRoot,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _allEntries.Remove(entry);
            FilterEntries(_selectedGroup);
            ShowToast("Entry moved to recycle bin (prototype only)", InfoBarSeverity.Informational);
        }
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        ShowToast("More actions would open here (prototype)", InfoBarSeverity.Informational);
    }

    private void ToolsPlaceholder_Click(object sender, RoutedEventArgs e)
    {
        var label = (sender as Button)?.Tag as string ?? "Tool";
        ShowToast($"{label} would open here (prototype)", InfoBarSeverity.Informational);
    }

    private async void SyncStatusButton_Click(object sender, RoutedEventArgs e)
    {
        SyncStatusButton.IsEnabled = false;
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = "A newer version of this entry was changed on another device.",
                TextWrapping = TextWrapping.Wrap,
            });
            var conflictCard = new Border
            {
                Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
            };
            var conflictText = new StackPanel { Spacing = 4 };
            conflictText.Children.Add(new TextBlock
            {
                Text = "GitHub  ·  Work",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            conflictText.Children.Add(new TextBlock
            {
                Text = "Changed fields: Password, Notes\nLocal  Oct 26, 2026 09:12  ·  Remote  Oct 26, 2026 09:14",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap,
            });
            conflictCard.Child = conflictText;
            content.Children.Add(conflictCard);
            content.Children.Add(new TextBlock
            {
                Text = "A conflict copy is written before any choice. Nothing is overwritten silently.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
            content.Children.Add(new TextBlock
            {
                Text = "Recommended: Keep both — preserve the conflict copy and merge the two versions.",
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            var dialog = new ContentDialog
            {
                Title = "Resolve sync conflict",
                Content = content,
                PrimaryButtonText = "Keep both",
                SecondaryButtonText = "Keep local",
                CloseButtonText = "Keep remote",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Root.XamlRoot,
            };
            var result = await dialog.ShowAsync();
            var choice = result switch
            {
                ContentDialogResult.Primary => "Keep both",
                ContentDialogResult.Secondary => "Keep local",
                _ => "Keep remote",
            };
            ShowToast($"{choice}: conflict copy would be written first (prototype)", InfoBarSeverity.Success);
        }
        finally
        {
            SyncStatusButton.IsEnabled = true;
        }
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var theme = new RadioButtons
        {
            Header = "Appearance",
            Items = { "Use system setting", "Light", "Dark" },
            SelectedIndex = 0,
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
        var clipboard = new ToggleSwitch
        {
            Header = "Clear copied secrets after 30 seconds",
            OnContent = "On",
            OffContent = "Off",
            IsOn = true,
        };
        var autoLock = new ToggleSwitch
        {
            Header = "Lock after 15 minutes of inactivity",
            OnContent = "On",
            OffContent = "Off",
            IsOn = true,
        };
        var content = new StackPanel { Spacing = 14 };
        content.Children.Add(theme);
        content.Children.Add(clipboard);
        content.Children.Add(autoLock);
        content.Children.Add(new TextBlock
        {
            Text = "Sync, backup and credential settings would live here. This prototype only changes the theme.",
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
    public GroupItem(string name, string count, string glyph, string? category)
    {
        Name = name;
        Count = count;
        Glyph = glyph;
        Category = category;
    }

    public string Name { get; }
    public string Count { get; }
    public string Glyph { get; }
    public string? Category { get; }
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

    private static int _paletteIndex;

    public EntryItem(string title, string username, string url, string category, string tag,
        bool hasTotp, string notes)
    {
        Title = title;
        Username = username;
        Url = url;
        Category = category;
        Tag = tag;
        HasTotp = hasTotp;
        Notes = notes;
        Password = $"P{title.Length}!k#pQ2m@{DateTime.UtcNow.Ticks % 9973:D4}";
        Initial = title.Length > 0 ? title[..1].ToUpperInvariant() : "?";
        AvatarBrush = new SolidColorBrush(Palette[_paletteIndex++ % Palette.Length]);
        UuidShort = Guid.NewGuid().ToString("N")[..8];
    }

    public string Title { get; private set; }
    public string Username { get; private set; }
    public string Url { get; private set; }
    public string Category { get; private set; }
    public string Tag { get; }
    public bool HasTotp { get; }
    public string Notes { get; private set; }
    public string Password { get; private set; }
    public string Initial { get; private set; }
    public Brush AvatarBrush { get; }
    public string UuidShort { get; }
    public Visibility TotpVisibility => HasTotp ? Visibility.Visible : Visibility.Collapsed;
    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var uri) ? uri.Host : Url;

    public void Update(string title, string username, string url, string notes)
    {
        Title = title;
        Username = username;
        Url = url;
        Notes = notes;
        Initial = title.Length > 0 ? title[..1].ToUpperInvariant() : "?";
    }

    public void MoveTo(string category)
    {
        Category = category;
    }

    public void SetPassword(string password)
    {
        Password = password;
    }
}
