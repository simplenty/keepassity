// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the detail panel's view model: field visibility, divider
/// rules, tag formatting, password masking/reveal and the empty state.
/// </summary>
internal static class EntryDetailViewModelTests
{
    public static void Run(TestCheck check)
    {
        var detail = new EntryDetailViewModel();
        detail.Clear("Select an entry");
        check("detail: empty title", detail.Title == "Select an entry", detail.Title);
        check("detail: empty hides every field",
            !detail.HasUsername && !detail.HasPassword && !detail.HasUrl &&
            !detail.HasTags && !detail.HasNotes && !detail.HasCustomField && !detail.HasTotp,
            detail.PasswordText);

        detail.Load(new EntryDetailInput(
            "GitHub",
            "Work",
            "alice",
            "s3cret",
            "https://github.com",
            "Recovery codes are stored offline.",
            new[] { "work", "2FA" },
            "Recovery email",
            "alice@example.com",
            true));
        check("detail: title and group", detail.Title == "GitHub" && detail.Subtitle == "Work",
            detail.Subtitle);
        check("detail: fields", detail.Username == "alice" &&
            detail.Url == "https://github.com" &&
            detail.Notes.StartsWith("Recovery"), detail.Url);
        check("detail: tags joined", detail.Tags == "work  \u00b7  2FA", detail.Tags);
        check("detail: custom field", detail.HasCustomField &&
            detail.CustomFieldName == "Recovery email" &&
            detail.CustomFieldValue == "alice@example.com", detail.CustomFieldName);
        check("detail: all flags", detail.HasUsername && detail.HasPassword &&
            detail.HasUrl && detail.HasTags && detail.HasNotes && detail.HasTotp);
        check("detail: dividers", detail.ShowUsernameDivider &&
            detail.ShowPasswordDivider && detail.ShowUrlDivider);
        check("detail: password starts masked",
            detail.PasswordText == new string('\u2022', 12), detail.PasswordText);
        int changes = 0;
        detail.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(EntryDetailViewModel.PasswordText))
            {
                changes++;
            }
        };
        detail.SetPasswordVisible(true);
        check("detail: reveal shows the secret", detail.PasswordText == "s3cret",
            detail.PasswordText);
        detail.SetPasswordVisible(false);
        check("detail: hide restores the mask", detail.PasswordText == new string('\u2022', 12));
        check("detail: reveal/hide notify twice", changes == 2, changes.ToString());

        detail.Load(new EntryDetailInput(
            "Minimal",
            "Vault",
            string.Empty,
            "only-password",
            string.Empty,
            string.Empty,
            Array.Empty<string>(),
            null,
            null,
            false));
        check("detail: password-only entry", detail.HasPassword && !detail.HasUsername &&
            !detail.HasUrl && !detail.HasTags && !detail.HasNotes && !detail.HasCustomField);
        check("detail: password-only divider", !detail.ShowUsernameDivider &&
            !detail.ShowPasswordDivider && !detail.ShowUrlDivider);
        check("detail: password-only mask", detail.PasswordText == new string('\u2022', 12));
    }
}
