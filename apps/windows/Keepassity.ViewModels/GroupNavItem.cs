// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>
/// One entry in the group navigation. Pure presentation data; the shell builds
/// the NavigationView items from these.
/// </summary>
public sealed class GroupNavItem
{
    public GroupNavItem(string? uuid, string name, int count, string glyph)
    {
        Uuid = uuid;
        Name = name;
        Count = count.ToString();
        Glyph = glyph;
    }

    public string? Uuid { get; }
    public string Name { get; }
    public string Count { get; }
    public string Glyph { get; }
    public int CountValue => int.TryParse(Count, out int value) ? value : 0;
}
