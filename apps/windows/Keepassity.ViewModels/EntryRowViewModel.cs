// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Keepassity.ViewModels;

/// <summary>
/// Presentation state for one row in the entry list. Deliberately free of
/// WinUI types: the avatar is a hex color and the template converts it to a
/// brush, while TOTP visibility is a boolean.
/// </summary>
public class EntryRowViewModel : INotifyPropertyChanged
{
    private static readonly string[] AvatarPalette =
    {
        "#0078D4",
        "#8764B8",
        "#107C10",
        "#CA5010",
        "#0099BC",
        "#C50F1F",
    };

    private string _groupUuid;
    private string _groupName;
    private string _secondary;

    public EntryRowViewModel(EntryRowInput input)
    {
        Uuid = input.Uuid;
        _groupUuid = input.GroupUuid;
        _groupName = input.GroupName;
        Title = input.Title;
        Username = input.Username;
        Url = input.Url;
        HasTotp = input.HasTotp;
        _secondary = SecondaryFor(input.Username, input.Url, input.GroupName);
        Initial = input.Title.Length > 0 ? input.Title[..1].ToUpperInvariant() : "?";
        int hash = 0;
        foreach (char character in input.Uuid)
        {
            hash = unchecked((hash * 31) + character);
        }
        AvatarColor = AvatarPalette[(hash & int.MaxValue) % AvatarPalette.Length];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Uuid { get; }
    public string GroupUuid => _groupUuid;
    public string GroupName => _groupName;
    public string Title { get; }
    public string Username { get; }
    public string Url { get; }
    public string Secondary => _secondary;
    public bool HasTotp { get; }
    public string Initial { get; }

    /// <summary>Hex color such as <c>#0078D4</c>; the view converts it.</summary>
    public string AvatarColor { get; }

    /// <summary>
    /// Updates the group shown for an entry that stays visible after a move (All
    /// entries / search view). Mutating the existing item keeps the ListView's
    /// selection and scroll position instead of rebuilding the middle column.
    /// </summary>
    public void UpdateGroup(string groupUuid, string groupName)
    {
        if (_groupUuid == groupUuid && _groupName == groupName)
        {
            return;
        }
        _groupUuid = groupUuid;
        _groupName = groupName;
        _secondary = SecondaryFor(Username, Url, groupName);
        OnPropertyChanged(nameof(GroupUuid));
        OnPropertyChanged(nameof(GroupName));
        OnPropertyChanged(nameof(Secondary));
    }

    private static string SecondaryFor(string username, string url, string groupName)
        => !string.IsNullOrWhiteSpace(username)
            ? username
            : Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
              !string.IsNullOrWhiteSpace(uri.Host)
                ? uri.Host
                : groupName;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
