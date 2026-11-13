// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>
/// Localized user-facing text for view models. The shell supplies the real
/// catalog-backed implementation; tests can supply a deterministic one, which
/// keeps view-model tests independent of the WinUI localization machinery.
/// </summary>
public interface IAppText
{
    /// <summary>Translate a plain English source string.</summary>
    string T(string english);

    /// <summary>Translate a composite format string and apply the arguments.</summary>
    string T(string format, params object?[] arguments);

    /// <summary>Translate a long string by symbolic key, with an English fallback.</summary>
    string TLong(string key, string english);
}
