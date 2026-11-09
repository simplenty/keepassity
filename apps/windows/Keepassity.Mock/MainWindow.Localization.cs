// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Keepassity.Mock;

/// <summary>
/// Language switching for the shell. The static XAML labels update through
/// <see cref="LocMarkup"/> when <see cref="Loc.LanguageChanged"/> fires; this file
/// handles everything the code builds or assigns itself: the unlock-page picker,
/// the settings picker, the group navigation/entry list headers, the sync status
/// and the tray icon, so the switch is complete without restarting the window.
/// </summary>
public sealed partial class MainWindow
{
    private bool _languagePickerUpdating;
    private DispatcherQueueTimer? _settingsReopenTimer;
    private string? _pendingUnlockLanguageSetting;

    private void InitializeLanguageUi()
    {
        UnlockLanguageCombo.SelectionChanged += UnlockLanguageCombo_SelectionChanged;
        UnlockLanguageCombo.DropDownClosed += UnlockLanguageCombo_DropDownClosed;
        Loc.LanguageChanged += OnLanguageChanged;
        RefreshLanguagePickers();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Root.Language = Loc.IsChinese ? "zh-CN" : "en-US";
        // Defer the code-built text refresh: the change usually comes from a
        // ComboBox selection handler, and rebuilding that same picker (or opening
        // a new panel) while the selection event is still on the stack can take
        // the XAML stack down with a native "catastrophic failure". The attached
        // XAML properties have already been re-applied synchronously by LocMarkup.
        DispatcherQueue.TryEnqueue(RelocalizeDynamicUi);
    }

    private void UnlockLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_languagePickerUpdating)
        {
            return;
        }
        if (UnlockLanguageCombo.SelectedItem is not ComboBoxItem { Tag: string setting })
        {
            return;
        }
        // Applying the language while the picker's popup is still open re-enters
        // the ComboBox/XAML stack and ends in a native catastrophic failure
        // (0x8000FFFF). Remember the choice and apply it from DropDownClosed,
        // after WinUI has torn the popup down. Keyboard changes on a closed
        // ComboBox (no popup) are applied immediately.
        if (UnlockLanguageCombo.IsDropDownOpen)
        {
            _pendingUnlockLanguageSetting = setting;
            return;
        }
        ApplyLanguageSetting(setting);
    }

    private void UnlockLanguageCombo_DropDownClosed(object? sender, object e)
    {
        string? pending = _pendingUnlockLanguageSetting;
        _pendingUnlockLanguageSetting = null;
        if (pending is not null)
        {
            ApplyLanguageSetting(pending);
            return;
        }
        RefreshLanguagePickers();
    }

    /// <summary>
    /// Stores the choice, persists it and lets <see cref="Loc"/> raise the change
    /// event that refreshes the XAML labels and the dynamic UI. Never saves from
    /// the diagnostic override path (that setting is forced, not chosen).
    /// </summary>
    private void ApplyLanguageSetting(string setting)
    {
        Settings.Language = setting;
        _settingsStore.Save();
        Loc.SetLanguage(setting);
    }

    /// <summary>
    /// Re-applies every string this window assigns from code. Called on every
    /// language change, after the XAML attached properties refreshed themselves.
    /// </summary>
    private void RelocalizeDynamicUi()
    {
        try
        {
            RefreshLanguagePickers();
            UpdateDatabasePath();
            if (MainRoot.Visibility != Visibility.Visible)
            {
                _trayIcon?.ApplyLanguage();
                return;
            }
            VaultNameText.Text = DisplayVaultName();
            RelocalizeGroupNavigation();
            RelocalizeCurrentGroupTitle();
            UpdateEntryCountText();
            UpdateEmptyListHint();
            RenderSyncStatus();
            _trayIcon?.ApplyLanguage();
        }
        catch (Exception ex)
        {
            // Re-rendering the localized shell must never take the window down;
            // keep the previous labels and record the failure instead.
            AppLog.Error("relocalizing the UI after a language change failed", ex);
        }
    }

    private void RefreshLanguagePickers()
    {
        if (UnlockLanguageCombo.IsDropDownOpen)
        {
            // Rebuilding a ComboBox whose popup is still open (the common case:
            // the user just picked an item there) can fail inside the XAML stack.
            // DropDownClosed re-runs this method once the popup is gone.
            return;
        }
        _languagePickerUpdating = true;
        try
        {
            PopulateLanguageCombo(UnlockLanguageCombo);
        }
        catch (Exception ex)
        {
            AppLog.Error("refreshing the language pickers failed", ex);
        }
        finally
        {
            _languagePickerUpdating = false;
        }
    }

    /// <summary>
    /// Fills a language picker with the localized choices and selects the stored
    /// setting. The item tags carry the stable setting value so the selection
    /// handlers never depend on the display text.
    /// </summary>
    private static void PopulateLanguageCombo(ComboBox combo)
    {
        combo.Items.Clear();
        IReadOnlyList<LanguageChoice> choices = Loc.Choices();
        int selectedIndex = 0;
        for (int index = 0; index < choices.Count; index++)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = choices[index].DisplayName,
                Tag = choices[index].Setting,
            });
            if (choices[index].Setting.Equals(Loc.Setting, StringComparison.OrdinalIgnoreCase))
            {
                selectedIndex = index;
            }
        }
        combo.SelectedIndex = selectedIndex;
    }

    /// <summary>
    /// The synthetic "All entries" row is the only navigation label owned by the
    /// shell (the other group names come from the database); refresh it in place.
    /// </summary>
    private void RelocalizeGroupNavigation()
    {
        foreach (object? menuItem in NavView.MenuItems)
        {
            if (menuItem is NavigationViewItem item && (item.Tag as string) == "all")
            {
                item.Content = Loc.T("All entries");
            }
        }
    }

    private void RelocalizeCurrentGroupTitle()
    {
        string title;
        if (!string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            title = Loc.T("Search results");
        }
        else if (_selectedGroupUuid is null)
        {
            title = Loc.T("All entries");
        }
        else
        {
            title = _groupNames.GetValueOrDefault(_selectedGroupUuid, Loc.T("Entries"));
        }
        CurrentGroupTitle.Text = title;
        RefreshEntryListFingerprint(title);
    }

    /// <summary>
    /// Closes the settings panel (which was built in the previous language) and
    /// opens it again with the new one. The close animation takes ~140 ms, so the
    /// reopen keeps retrying until <see cref="_rightPanelOpen"/> is false instead
    /// of firing once at a fixed delay.
    /// </summary>
    private void ReopenSettingsAfterLanguageChange()
    {
        CloseRightPanel();
        _settingsReopenTimer?.Stop();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(200);
        timer.IsRepeating = true;
        int attempts = 0;
        timer.Tick += (_, _) =>
        {
            attempts++;
            if (!_rightPanelOpen && MainRoot.Visibility == Visibility.Visible)
            {
                timer.Stop();
                _settingsReopenTimer = null;
                SettingsButton_Click(this, new RoutedEventArgs());
            }
            else if (attempts >= 10)
            {
                // The panel stayed open (or the vault locked meanwhile): leave it
                // closed rather than fighting the animation loop.
                timer.Stop();
                _settingsReopenTimer = null;
            }
        };
        _settingsReopenTimer = timer;
        timer.Start();
    }
}
