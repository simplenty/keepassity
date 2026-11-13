// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the group navigation ordering: special groups pinned,
/// root/blank groups hidden, counts passed through.
/// </summary>
internal static class GroupNavigationTests
{
    public static void Run(TestCheck check)
    {
        var groups = new[]
        {
            new GroupNavigationInput("root", "Vault"),
            new GroupNavigationInput("g1", "Work"),
            new GroupNavigationInput("g2", "Templates"),
            new GroupNavigationInput("g3", "Personal"),
            new GroupNavigationInput("g4", "Recycle Bin"),
            new GroupNavigationInput("g5", string.Empty),
        };
        List<GroupNavItem> items = GroupNavigationProjection.Build(
            groups,
            "root",
            "All entries",
            9,
            uuid => uuid switch { "g1" => 2, "g3" => 1, _ => 0 },
            name => name == "Recycle Bin",
            name => name == "Templates");
        check("group nav: all entries first", items[0].Uuid is null &&
            items[0].Name == "All entries" && items[0].CountValue == 9,
            items[0].Name);
        check("group nav: normal groups keep vault order",
            items.Skip(1).Select(item => item.Uuid)
                .SequenceEqual(new[] { "g1", "g3", "g2", "g4" }),
            string.Join(",", items.Select(item => item.Name)));
        check("group nav: counts", items[1].CountValue == 2 && items[2].CountValue == 1);
        check("group nav: root and blank names hidden",
            items.All(item => item.Uuid is not ("root" or "g5")));
        check("group nav: templates before recycle bin",
            items[^2].Name == "Templates" && items[^1].Name == "Recycle Bin");

        List<GroupNavItem> empty = GroupNavigationProjection.Build(
            Array.Empty<GroupNavigationInput>(), null, "All entries", 0,
            _ => 0, _ => false, _ => false);
        check("group nav: empty vault has all entries", empty.Count == 1 &&
            empty[0].CountValue == 0);
    }
}
