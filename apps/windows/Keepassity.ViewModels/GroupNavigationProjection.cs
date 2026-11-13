// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>One group row as the navigation projection needs it.</summary>
public readonly record struct GroupNavigationInput(string Uuid, string Name);

/// <summary>
/// Ordering rules for the group navigation: "All entries" first, then normal
/// groups in vault order, then the Templates group, then the Recycle Bin last.
/// Name classifiers are supplied by the view because a KeePass vault may store
/// the special groups under a localized name.
/// </summary>
public static class GroupNavigationProjection
{
    public static List<GroupNavItem> Build(
        IEnumerable<GroupNavigationInput> groups,
        string? rootUuid,
        string allEntriesLabel,
        int allEntriesCount,
        Func<string, int> countForGroup,
        Func<string, bool> isRecycleBin,
        Func<string, bool> isTemplates)
    {
        var items = new List<GroupNavItem>();
        items.Add(new GroupNavItem(null, allEntriesLabel, allEntriesCount, "\uE8FD"));

        GroupNavItem? recycleBin = null;
        GroupNavItem? templates = null;
        var otherGroups = new List<GroupNavItem>();
        foreach (GroupNavigationInput group in groups)
        {
            if (group.Uuid == rootUuid || string.IsNullOrWhiteSpace(group.Name))
            {
                continue;
            }
            var item = new GroupNavItem(group.Uuid, group.Name, countForGroup(group.Uuid),
                "\uE8B7");
            if (recycleBin is null && isRecycleBin(group.Name))
            {
                recycleBin = item;
            }
            else if (templates is null && isTemplates(group.Name))
            {
                templates = item;
            }
            else
            {
                otherGroups.Add(item);
            }
        }
        items.AddRange(otherGroups);
        if (templates is not null)
        {
            items.Add(templates);
        }
        if (recycleBin is not null)
        {
            items.Add(recycleBin);
        }
        return items;
    }
}
