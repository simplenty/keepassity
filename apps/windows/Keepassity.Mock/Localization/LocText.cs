// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// Adapts the catalog-backed <see cref="Loc"/> service to the view-model text
/// port. Keeping this adapter in the shell is what allows the view models to be
/// tested without WinUI or the platform language override.
/// </summary>
internal sealed class LocText : IAppText
{
    public static LocText Instance { get; } = new();

    public string T(string english) => Loc.T(english);

    public string T(string format, params object?[] arguments) => Loc.T(format, arguments);

    public string TLong(string key, string english) => Loc.TLong(key, english);
}
