// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Keepassity.ViewModels;

/// <summary>
/// One entry as the detail panel needs it. Pure data; the shell maps the FFI
/// DTO to this record and the view model never sees WinUI.
/// </summary>
public readonly record struct EntryDetailInput(
    string Title,
    string GroupName,
    string Username,
    string PasswordSecret,
    string Url,
    string Notes,
    IReadOnlyList<string> Tags,
    string? CustomFieldName,
    string? CustomFieldValue,
    bool HasTotp);

/// <summary>
/// State and display strings for the detail panel. The view binds these values;
/// visibility remains a view concern (the view maps the booleans to Visibility).
/// </summary>
public sealed class EntryDetailViewModel : ObservableObject
{
    private static readonly string PasswordMask = new('\u2022', 12);

    private string _title = string.Empty;
    private string _subtitle = string.Empty;
    private string _username = string.Empty;
    private string _passwordSecret = string.Empty;
    private string _url = string.Empty;
    private string _notes = string.Empty;
    private string _tags = string.Empty;
    private string _customFieldName = string.Empty;
    private string _customFieldValue = string.Empty;
    private bool _hasUsername;
    private bool _hasPassword;
    private bool _hasUrl;
    private bool _hasTags;
    private bool _hasNotes;
    private bool _hasCustomField;
    private bool _hasTotp;
    private bool _passwordVisible;

    public string Title => _title;
    public string Subtitle => _subtitle;
    public string Username => _username;
    public string Url => _url;
    public string Notes => _notes;
    public string Tags => _tags;
    public string CustomFieldName => _customFieldName;
    public string CustomFieldValue => _customFieldValue;
    public bool HasUsername => _hasUsername;
    public bool HasPassword => _hasPassword;
    public bool HasUrl => _hasUrl;
    public bool HasTags => _hasTags;
    public bool HasNotes => _hasNotes;
    public bool HasCustomField => _hasCustomField;
    public bool HasTotp => _hasTotp;

    /// <summary>The password shown in the panel: mask unless revealed.</summary>
    public string PasswordText =>
        !_hasPassword ? string.Empty : _passwordVisible ? _passwordSecret : PasswordMask;

    // Dividers sit between fields that are actually shown.
    public bool ShowUsernameDivider => _hasUsername && (_hasPassword || _hasUrl || _hasTags);
    public bool ShowPasswordDivider => _hasPassword && (_hasUrl || _hasTags);
    public bool ShowUrlDivider => _hasUrl && _hasTags;

    public void Load(EntryDetailInput input)
    {
        _title = input.Title;
        _subtitle = input.GroupName;
        _username = input.Username;
        _passwordSecret = input.PasswordSecret;
        _url = input.Url;
        _notes = input.Notes;
        _tags = input.Tags.Count > 0 ? string.Join("  \u00b7  ", input.Tags) : string.Empty;
        _customFieldName = input.CustomFieldName ?? string.Empty;
        _customFieldValue = input.CustomFieldValue ?? string.Empty;
        _hasUsername = !string.IsNullOrWhiteSpace(input.Username);
        _hasPassword = !string.IsNullOrEmpty(input.PasswordSecret);
        _hasUrl = !string.IsNullOrWhiteSpace(input.Url);
        _hasTags = input.Tags.Count > 0;
        _hasNotes = !string.IsNullOrWhiteSpace(input.Notes);
        _hasCustomField = !string.IsNullOrEmpty(input.CustomFieldName);
        _hasTotp = input.HasTotp;
        _passwordVisible = false;
        RaiseAll();
    }

    public void Clear(string emptyTitle)
    {
        _title = emptyTitle;
        _subtitle = string.Empty;
        _username = string.Empty;
        _passwordSecret = string.Empty;
        _url = string.Empty;
        _notes = string.Empty;
        _tags = string.Empty;
        _customFieldName = string.Empty;
        _customFieldValue = string.Empty;
        _hasUsername = false;
        _hasPassword = false;
        _hasUrl = false;
        _hasTags = false;
        _hasNotes = false;
        _hasCustomField = false;
        _hasTotp = false;
        _passwordVisible = false;
        RaiseAll();
    }

    public void SetPasswordVisible(bool visible)
    {
        if (_passwordVisible == visible)
        {
            return;
        }
        _passwordVisible = visible;
        OnPropertyChanged(nameof(PasswordText));
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Username));
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(CustomFieldName));
        OnPropertyChanged(nameof(CustomFieldValue));
        OnPropertyChanged(nameof(HasUsername));
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(HasUrl));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasCustomField));
        OnPropertyChanged(nameof(HasTotp));
        OnPropertyChanged(nameof(PasswordText));
        OnPropertyChanged(nameof(ShowUsernameDivider));
        OnPropertyChanged(nameof(ShowPasswordDivider));
        OnPropertyChanged(nameof(ShowUrlDivider));
    }
}
