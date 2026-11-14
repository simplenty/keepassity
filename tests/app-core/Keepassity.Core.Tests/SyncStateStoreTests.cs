// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the sync baseline sidecar: round trip, malformed hash
/// fails closed, and an unreadable sidecar is preserved for diagnosis.
/// </summary>
internal static class SyncStateStoreTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory, "sync-state");
        Directory.CreateDirectory(directory);
        var store = new SyncStateStore(directory);
        check("sync state: store available", store.Available);

        var state = new SyncState
        {
            Etag = "\"etag-1\"",
            Sha256 = new string('a', 64),
            Size = 1234,
            RemoteModifiedUnix = 1_700_000_000,
            LocalDirty = false,
        };
        store.Save("https://dav.example.test/db.kdbx", "user", "/tmp/db.kdbx", state);
        SyncState loaded = store.Load("https://dav.example.test/db.kdbx", "user", "/tmp/db.kdbx");
        check("sync state: round trip", loaded.Etag == "\"etag-1\"" &&
            loaded.Sha256 == new string('a', 64) && loaded.Size == 1234 &&
            !loaded.LocalDirty, loaded.Etag ?? "null");

        SyncState missing = store.Load("https://dav.example.test/other.kdbx", "user",
            "/tmp/other.kdbx");
        check("sync state: missing baseline is clean", !missing.LocalDirty);

        state.Sha256 = "not-a-sha";
        store.Save("https://dav.example.test/bad.kdbx", "user", "/tmp/bad.kdbx", state);
        SyncState malformed = store.Load("https://dav.example.test/bad.kdbx", "user",
            "/tmp/bad.kdbx");
        check("sync state: malformed hash fails closed", malformed.LocalDirty);

        string corruptDirectory = Path.Combine(rootDirectory, "sync-state-corrupt");
        Directory.CreateDirectory(corruptDirectory);
        var corruptStore = new SyncStateStore(corruptDirectory);
        corruptStore.Save("https://dav.example.test/corrupt.kdbx", "user",
            "/tmp/corrupt.kdbx", state);
        string? anySidecar = Directory.GetFiles(corruptDirectory, "*.json").FirstOrDefault();
        check("sync state: sidecar written", anySidecar is not null);
        if (anySidecar is not null)
        {
            File.WriteAllText(anySidecar, "{ not json");
            SyncState corrupt = corruptStore.Load("https://dav.example.test/corrupt.kdbx",
                "user", "/tmp/corrupt.kdbx");
            check("sync state: corrupt sidecar is dirty", corrupt.LocalDirty);
            check("sync state: corrupt sidecar is preserved",
                Directory.GetFiles(corruptDirectory, "*.corrupt-*").Length > 0);
        }
    }
}
