// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the entry-list view models: sorting/filtering/fingerprint
/// and the row's display strings. This is the part of the middle column that no
/// WinUI test would cover.
/// </summary>
internal static class EntryListViewModelTests
{
    public static void Run(TestCheck check)
    {
        var alpha = new EntryRowInput("uuid-a", "g-work", "Work", "Alpha", "alice",
            string.Empty, false, 10);
        var beta = new EntryRowInput("uuid-b", "g-home", "Home", "beta", string.Empty,
            "https://example.com/path", true, 30);
        var gamma = new EntryRowInput("uuid-c", "g-work", "Work", "Gamma", "carol",
            string.Empty, false, 20);
        EntryRowInput[] rows = { alpha, beta, gamma };

        List<EntryRowInput> byTitle =
            EntryListProjection.Visible(rows, EntrySort.Title, false);
        check("entry list: title order", byTitle.Select(row => row.Uuid)
            .SequenceEqual(new[] { "uuid-a", "uuid-b", "uuid-c" }),
            string.Join(",", byTitle.Select(row => row.Title)));

        List<EntryRowInput> byModified =
            EntryListProjection.Visible(rows, EntrySort.Modified, false);
        check("entry list: modified order", byModified.Select(row => row.Uuid)
            .SequenceEqual(new[] { "uuid-b", "uuid-c", "uuid-a" }),
            string.Join(",", byModified.Select(row => row.Title)));

        List<EntryRowInput> byGroup =
            EntryListProjection.Visible(rows, EntrySort.Group, false);
        check("entry list: group then title order", byGroup.Select(row => row.Uuid)
            .SequenceEqual(new[] { "uuid-b", "uuid-a", "uuid-c" }),
            string.Join(",", byGroup.Select(row => row.GroupName)));

        List<EntryRowInput> totpOnly =
            EntryListProjection.Visible(rows, EntrySort.Title, true);
        check("entry list: TOTP filter", totpOnly.Count == 1 && totpOnly[0].Uuid == "uuid-b",
            totpOnly.Count.ToString());

        string first = EntryListProjection.Fingerprint(byTitle, "All entries");
        string same = EntryListProjection.Fingerprint(byTitle.ToList(), "All entries");
        string renamed = EntryListProjection.Fingerprint(
            new[] { alpha with { Title = "Renamed" }, beta, gamma }, "All entries");
        check("entry list: fingerprint is stable", first == same);
        check("entry list: fingerprint notices a rename", first != renamed);

        var row = new EntryRowViewModel(alpha);
        check("entry row: title", row.Title == "Alpha", row.Title);
        check("entry row: initial", row.Initial == "A", row.Initial);
        check("entry row: secondary prefers username", row.Secondary == "alice",
            row.Secondary);
        check("entry row: avatar color is a hex color",
            row.AvatarColor.StartsWith('#') && row.AvatarColor.Length == 7,
            row.AvatarColor);
        var sameAvatar = new EntryRowViewModel(alpha);
        check("entry row: avatar color is stable", row.AvatarColor == sameAvatar.AvatarColor);

        var urlRow = new EntryRowViewModel(beta);
        check("entry row: secondary falls back to the URL host",
            urlRow.Secondary == "example.com", urlRow.Secondary);
        check("entry row: TOTP flag", urlRow.HasTotp);

        var groupOnly = new EntryRowViewModel(gamma with { Username = string.Empty, Url = string.Empty });
        check("entry row: secondary falls back to the group name",
            groupOnly.Secondary == "Work", groupOnly.Secondary);
        int changes = 0;
        groupOnly.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EntryRowViewModel.Secondary))
            {
                changes++;
            }
        };
        groupOnly.UpdateGroup("g-home", "Home");
        check("entry row: move updates group and secondary",
            groupOnly.GroupUuid == "g-home" && groupOnly.GroupName == "Home" &&
            groupOnly.Secondary == "Home" && changes == 1,
            $"{groupOnly.GroupName}/{groupOnly.Secondary}/{changes}");
        groupOnly.UpdateGroup("g-home", "Home");
        check("entry row: a no-op move does not notify", changes == 1, changes.ToString());
    }
}
