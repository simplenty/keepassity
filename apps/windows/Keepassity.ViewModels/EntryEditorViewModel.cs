// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>Validated TOTP value produced by the entry editor.</summary>
public readonly record struct TotpEditorValue(
    string SecretBase32,
    string Algorithm,
    int Digits,
    int Period);

/// <summary>
/// The entry editor's data and normalization rules. The dialog controls stay in
/// the shell (they are platform objects), but the values they produce are
/// normalized and validated here so create/edit behave the same and are
/// testable without WinUI.
/// </summary>
public sealed class EntryEditorViewModel
{
    public string? Uuid { get; private init; }
    public string? GroupUuid { get; private init; }
    public bool IsNew => Uuid is null;

    public string Title { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string TagsText { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;

    public bool HasExistingTotp { get; private init; }
    public string TotpSecret { get; set; } = string.Empty;
    public string TotpAlgorithm { get; set; } = "SHA1";
    public string TotpDigits { get; set; } = "6";
    public int TotpPeriod { get; set; } = 30;

    public static EntryEditorViewModel New(string groupUuid) => new()
    {
        GroupUuid = groupUuid,
    };

    public static EntryEditorViewModel FromEntry(
        string uuid,
        string title,
        string username,
        string password,
        string url,
        IReadOnlyList<string> tags,
        string notes,
        string? totpSecret,
        string? totpAlgorithm,
        int? totpDigits,
        int? totpPeriod) => new()
    {
        Uuid = uuid,
        Title = title,
        Username = username,
        Password = password,
        Url = url,
        TagsText = string.Join(", ", tags),
        Notes = notes,
        HasExistingTotp = !string.IsNullOrEmpty(totpSecret),
        TotpSecret = totpSecret ?? string.Empty,
        TotpAlgorithm = NormalizeAlgorithm(totpAlgorithm),
        TotpDigits = NormalizeDigits(totpDigits).ToString(),
        TotpPeriod = NormalizePeriod(totpPeriod),
    };

    /// <summary>
    /// Title used when creating: a blank title becomes the localized placeholder
    /// so an entry is never created with an empty, unclickable row.
    /// </summary>
    public string CreateTitle(string untitledTitle)
    {
        string trimmed = Title.Trim();
        return trimmed.Length > 0 ? trimmed : untitledTitle;
    }

    public string UpdateTitle() => Title.Trim();
    public string NormalizedUsername() => Username.Trim();
    public string NormalizedUrl() => Url.Trim();

    /// <summary>Comma-separated tags, trimmed, de-duplicated case-insensitively.</summary>
    public List<string> TagList() => ParseTags(TagsText);

    public static List<string> ParseTags(string text) => text
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>True when an existing TOTP is cleared by leaving the secret empty.</summary>
    public bool ShouldDeleteTotp =>
        HasExistingTotp && string.IsNullOrWhiteSpace(TotpSecret);

    /// <summary>Non-empty TOTP value with safe defaults, or null when none.</summary>
    public TotpEditorValue? BuildTotp()
    {
        string secret = TotpSecret.Trim();
        if (secret.Length == 0)
        {
            return null;
        }
        return new TotpEditorValue(
            secret,
            NormalizeAlgorithm(TotpAlgorithm),
            NormalizeDigits(int.TryParse(TotpDigits, out int digits) ? digits : null),
            NormalizePeriod(TotpPeriod));
    }

    private static string NormalizeAlgorithm(string? algorithm) => algorithm switch
    {
        "SHA256" => "SHA256",
        "SHA512" => "SHA512",
        _ => "SHA1",
    };

    private static int NormalizeDigits(int? digits) => digits == 8 ? 8 : 6;

    private static int NormalizePeriod(int? period) =>
        Math.Clamp(period ?? 30, 1, 300);
}
