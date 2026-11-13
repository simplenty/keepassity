// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the entry editor's normalization: untitled fallback,
/// tag parsing, TOTP defaults/clamping and the delete-on-empty-secret rule.
/// </summary>
internal static class EntryEditorViewModelTests
{
    public static void Run(TestCheck check)
    {
        check("editor: tags trim and de-duplicate",
            EntryEditorViewModel.ParseTags(" work, 2FA,work, , personal ")
                .SequenceEqual(new[] { "work", "2FA", "personal" }),
            string.Join(",", EntryEditorViewModel.ParseTags(" work, 2FA,work, , personal ")));

        var created = EntryEditorViewModel.New("group-1");
        created.Title = "  Bank  ";
        created.Username = " alice ";
        created.Url = " https://example.com ";
        check("editor: create title trims", created.CreateTitle("Untitled") == "Bank",
            created.CreateTitle("Untitled"));
        check("editor: create username trims", created.NormalizedUsername() == "alice");
        check("editor: create URL trims", created.NormalizedUrl() == "https://example.com");
        check("editor: new entry group", created.IsNew && created.GroupUuid == "group-1");
        created.Title = "   ";
        check("editor: blank title falls back", created.CreateTitle("Untitled") == "Untitled",
            created.CreateTitle("Untitled"));

        var edited = EntryEditorViewModel.FromEntry(
            "entry-1",
            "Old title",
            "bob",
            "secret",
            "https://old.example",
            new[] { "a", "b" },
            "notes",
            "JBSWY3DPEHPK3PXP",
            "SHA256",
            8,
            60);
        check("editor: existing entry identity",
            !edited.IsNew && edited.Uuid == "entry-1");
        check("editor: existing tags rendered", edited.TagsText == "a, b", edited.TagsText);
        check("editor: existing TOTP loaded", edited.HasExistingTotp &&
            edited.TotpSecret == "JBSWY3DPEHPK3PXP" && edited.TotpAlgorithm == "SHA256" &&
            edited.TotpDigits == "8" && edited.TotpPeriod == 60,
            edited.TotpAlgorithm);
        check("editor: existing TOTP builds", edited.BuildTotp() is
            { SecretBase32: "JBSWY3DPEHPK3PXP", Algorithm: "SHA256", Digits: 8, Period: 60 });

        edited.TotpSecret = "  ";
        check("editor: empty secret deletes existing TOTP", edited.ShouldDeleteTotp);
        check("editor: empty secret builds no TOTP", edited.BuildTotp() is null);

        var defaults = EntryEditorViewModel.New("g");
        defaults.TotpSecret = " ABC ";
        defaults.TotpAlgorithm = "sha256";
        defaults.TotpDigits = "7";
        defaults.TotpPeriod = 999;
        TotpEditorValue? value = defaults.BuildTotp();
        check("editor: TOTP defaults and clamps",
            value is { SecretBase32: "ABC", Algorithm: "SHA1", Digits: 6, Period: 300 },
            value?.ToString() ?? "null");

        defaults.TotpPeriod = 0;
        check("editor: TOTP period lower clamp", defaults.BuildTotp()?.Period == 1,
            defaults.BuildTotp()?.Period.ToString() ?? "null");
    }
}
