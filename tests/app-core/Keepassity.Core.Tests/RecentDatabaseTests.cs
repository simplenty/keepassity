// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the recent-database list: normalization, newest-first
/// order, de-duplication, limit, removal and corrupt-file preservation.
/// </summary>
internal static class RecentDatabaseTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory, "recent-databases");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "recent.json");
        string vaultA = Path.Combine(directory, "a.kdbx");
        string vaultB = Path.Combine(directory, "b.kdbx");
        string vaultC = Path.Combine(directory, "c.kdbx");

        var store = new RecentDatabaseStore(path);
        store.Remember(vaultA, "local");
        store.Remember(vaultB, "webdav", "https://dav.example.test/b.kdbx", "user");
        store.Remember(vaultA, "local");
        check("recent: newest-first and de-duplicated",
            store.Entries.Count == 2 && store.Entries[0].Path == vaultA &&
            store.Entries[0].Kind == "local" && store.Entries[1].Path == vaultB,
            string.Join(", ", store.Entries.Select(entry => entry.DisplayName)));
        check("recent: WebDAV facts retained", store.Entries[1].WebDavUrl ==
            "https://dav.example.test/b.kdbx" && store.Entries[1].WebDavUsername == "user");

        var reloaded = new RecentDatabaseStore(path);
        check("recent: round trips through disk",
            reloaded.Entries.Count == 2 && reloaded.Entries[0].Path == vaultA,
            reloaded.Entries.Count.ToString());

        for (int index = 0; index < RecentDatabaseStore.MaxEntries + 2; index++)
        {
            store.Remember(Path.Combine(directory, $"v{index}.kdbx"));
        }
        check("recent: list is capped", store.Entries.Count == RecentDatabaseStore.MaxEntries,
            store.Entries.Count.ToString());
        check("recent: oldest entries dropped", store.Entries.All(entry =>
            !string.Equals(entry.Path, Path.GetFullPath(vaultB),
                StringComparison.OrdinalIgnoreCase)));

        string toRemove = store.Entries[0].Path;
        check("recent: remove reports a hit", store.Remove(toRemove));
        check("recent: remove reports a miss", !store.Remove(toRemove));

        string corrupt = Path.Combine(directory, "corrupt.json");
        File.WriteAllText(corrupt, "{ not a list");
        var corruptStore = new RecentDatabaseStore(corrupt);
        check("recent: corrupt file starts empty", corruptStore.Entries.Count == 0);
        check("recent: corrupt file is preserved",
            Directory.GetFiles(directory, "corrupt.json.corrupt-*").Length == 1);
    }
}
