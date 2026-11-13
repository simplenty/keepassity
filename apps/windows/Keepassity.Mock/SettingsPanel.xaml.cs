// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Keepassity.Mock;

/// <summary>
/// View for <see cref="SettingsViewModel"/>. Labels are view concerns and are
/// assigned here from <see cref="Loc"/>; data and decisions stay in the view
/// model. The WebDAV password box is deliberately not bound: its text is passed
/// to the view model only when the panel's Save is pressed.
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    public SettingsPanel(SettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ThemeChoices.Header = Loc.T("Appearance");
        ThemeChoices.Items.Add(Loc.T("Use system setting"));
        ThemeChoices.Items.Add(Loc.T("Light"));
        ThemeChoices.Items.Add(Loc.T("Dark"));
        ThemeChoices.SelectedIndex = viewModel.ThemeIndex;
        LanguageCombo.Header = Loc.T("Language");
        LanguageCombo.ItemsSource = viewModel.Languages;
        LanguageCombo.DisplayMemberPath = nameof(LanguageOption.DisplayName);
        LanguageCombo.SelectedValuePath = nameof(LanguageOption.Setting);
        LanguageCombo.SelectedValue = viewModel.SelectedLanguage;
        ClipboardToggle.Header = Loc.T("Clear copied secrets after 30 seconds");
        ClipboardToggle.OnContent = Loc.T("On");
        ClipboardToggle.OffContent = Loc.T("Off");
        AutoLockToggle.Header = Loc.T("Lock the vault after inactivity");
        AutoLockToggle.OnContent = Loc.T("On");
        AutoLockToggle.OffContent = Loc.T("Off");
        StartMinimizedToggle.Header = Loc.T("Start minimized to the notification area");
        StartMinimizedToggle.OnContent = Loc.T("On");
        StartMinimizedToggle.OffContent = Loc.T("Off");
        AutoLockMinutesBox.Header = Loc.T("Inactivity minutes before lock");
        RetentionBox.Header = Loc.T("Backup copies to keep");
        WebDavUrlBox.Header = Loc.T("WebDAV file URL");
        WebDavUserBox.Header = Loc.T("WebDAV username");
        WebDavPasswordBox.Header = Loc.T("WebDAV password (leave blank to keep the stored one)");
        ClearStoredPasswordButton.Content = Loc.T("Clear stored password");
        SyncOnSaveToggle.Header = Loc.T("Sync after saving changes");
        SyncOnSaveToggle.OnContent = Loc.T("On");
        SyncOnSaveToggle.OffContent = Loc.T("Off");
        LicenseLink.Content = Loc.T("GPL-3.0 license text");
        SourceLink.Content = Loc.T("Source code");
        DiagnosticsButton.Content = Loc.T("Export diagnostics…");
        // The XAML initial values are read before the constructor's label setup;
        // refresh the credential summary once more so a stored password is shown
        // even when the URL was filled from settings.
        viewModel.RefreshStoredCredential();
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Password currently typed in the panel; never stored by the view.</summary>
    public string WebDavPassword => WebDavPasswordBox.Password;

    internal Image AboutIconElement => AboutIcon;

    private void ThemeChoices_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThemeChoices.SelectedIndex >= 0)
        {
            ViewModel.ThemeIndex = ThemeChoices.SelectedIndex;
        }
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageCombo.SelectedItem is LanguageOption option &&
            !string.Equals(option.Setting, ViewModel.SelectedLanguage,
                StringComparison.OrdinalIgnoreCase))
        {
            ViewModel.SelectedLanguage = option.Setting;
        }
    }

    private void ClearStoredPassword_Click(object sender, RoutedEventArgs e)
        => ViewModel.ClearStoredPassword();

    private async void Diagnostics_Click(object sender, RoutedEventArgs e)
        => await ViewModel.ExportDiagnosticsAsync();
}
