// SPDX-License-Identifier: GPL-3.0-or-later
// The four headless drills. See Program.cs for the command surface and
// tools/drills/README.md for what each drill proves and what stays manual.

using System.Net;
using System.Text;
using System.Text.Json;
using Keepassity.Mock;

namespace Keepassity.Drills.Runner;

internal static class Drills
{
    // ---------------------------------------------------------------- restore

    /// <summary>
    /// (a) Backup restore. Uses the real <see cref="BackupService"/> used by the
    /// app: CreateBackup -> edit through the FFI -> Restore -> byte equality and
    /// reopen checks.
    /// </summary>
    public static int Restore(string[] args)
    {
        string fixture = Drill.Arg(args, "--fixture");
        string workdir = Drill.Arg(args, "--workdir");
        string databasePath = Path.Combine(workdir, "restore-drill.kdbx");

        string backupDirectory = BackupService.BackupDirectory(databasePath);
        if (Directory.Exists(backupDirectory))
        {
            Directory.Delete(backupDirectory, recursive: true);
        }
        foreach (string leftover in Directory.EnumerateFiles(workdir,
                     "restore-drill.kdbx.keepassity-restore-bak", SearchOption.TopDirectoryOnly))
        {
            File.Delete(leftover);
        }
        File.Copy(fixture, databasePath, overwrite: true);
        byte[] original = File.ReadAllBytes(databasePath);

        BackupEntry? backup = BackupService.CreateBackup(databasePath, retention: 20,
            reason: "drill-start");
        if (backup is null || !File.Exists(backup.Path))
        {
            return Fail("BackupService.CreateBackup did not create a backup");
        }
        Drill.Pass("BackupService.CreateBackup", backup.Path);

        string editedTitle = "Restore drill edit " + Guid.NewGuid().ToString("N")[..8];
        byte[] edited;
        using (CoreApi api = CoreApi.Open(original, Drill.MasterPassword))
        {
            List<(string Uuid, string Title)> entries = api.ListEntries();
            if (entries.Count != 500)
            {
                return Fail($"fixture has {entries.Count} entries, expected 500");
            }
            api.UpdateTitle(entries[0].Uuid, editedTitle);
            if (!api.SearchFinds(editedTitle))
            {
                return Fail("edited title is not visible through the FFI search before save");
            }
            edited = api.Save();
        }
        File.WriteAllBytes(databasePath, edited);
        Drill.Pass("edited and saved through the FFI (kp_call update_entry + kp_save)",
            $"{original.Length} -> {edited.Length} bytes");

        BackupService.Restore(databasePath, backup.Path);
        byte[] restored = File.ReadAllBytes(databasePath);
        if (!restored.AsSpan().SequenceEqual(original))
        {
            return Fail("BackupService.Restore did not reproduce the backed-up bytes");
        }
        Drill.Pass("BackupService.Restore reproduced the backed-up bytes",
            $"{restored.Length} bytes");

        List<BackupEntry> backups = BackupService.ListBackups(databasePath);
        if (backups.Count < 2)
        {
            return Fail($"expected the before-restore safety copy, found {backups.Count} backup(s)");
        }
        Drill.Pass("restore wrote a safety backup first", $"backups={backups.Count}");

        using (CoreApi api = CoreApi.Open(restored, Drill.MasterPassword))
        {
            int entries = api.EntryCount();
            if (entries != 500)
            {
                return Fail($"restored database reports {entries} entries, expected 500");
            }
            if (api.SearchFinds(editedTitle))
            {
                return Fail("restored database still contains the drill edit (restore was a no-op)");
            }
        }
        Drill.Pass("restored database reopens with 500 entries; the drill edit is gone");

        Drill.Info("BackupService is the app service; the app's own save flow calls the " +
                   "same CreateBackup/Restore methods (MainWindow.SaveBytes/CoreClient.SaveAsync).");
        return 0;
    }

    // --------------------------------------------------------------- protocol

    /// <summary>
    /// Mock WebDAV protocol check: PUT create, PROPFIND depth 0, GET, HEAD,
    /// conditional PUT (If-None-Match / If-Match / stale If-Match -> 412),
    /// DELETE, and a non-ASCII (percent-encoded) file path.
    /// </summary>
    public static int Protocol(string[] args)
    {
        string url = Drill.Arg(args, "--url");
        using var http = new HttpClient();
        using var dav = new WebDavClient(url, "drill", "drill");

        byte[] first = Encoding.UTF8.GetBytes("mock-webdav protocol drill payload 1");
        string? etag1 = dav.UploadAsync(first, ifMatch: null).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(etag1))
        {
            return Fail("PUT create returned no ETag");
        }
        Drill.Pass("PUT create (If-None-Match: *)", "etag=" + etag1);

        WebDavFileInfo? metadata = dav.GetMetadataAsync().GetAwaiter().GetResult();
        if (metadata is null || metadata.ETag != etag1 || metadata.Size != first.Length)
        {
            return Fail($"PROPFIND metadata mismatch: {JsonSerializer.Serialize(metadata)}");
        }
        Drill.Pass("PROPFIND depth 0 reports ETag/size",
            $"etag={metadata.ETag} size={metadata.Size}");

        byte[] downloaded = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!downloaded.AsSpan().SequenceEqual(first))
        {
            return Fail("GET body differs from the uploaded body");
        }
        Drill.Pass("GET returns the uploaded bytes", $"{downloaded.Length} bytes");

        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, url))
        using (HttpResponseMessage headResponse =
               http.SendAsync(headRequest).GetAwaiter().GetResult())
        {
            if (headResponse.StatusCode != HttpStatusCode.OK ||
                headResponse.Headers.ETag?.Tag != etag1 ||
                headResponse.Content.Headers.ContentLength != first.Length)
            {
                return Fail($"HEAD failed: status={headResponse.StatusCode} " +
                            $"etag={headResponse.Headers.ETag?.Tag}");
            }
        }
        Drill.Pass("HEAD reports status/ETag/content-length");

        byte[] stale = Encoding.UTF8.GetBytes("mock-webdav stale write");
        try
        {
            dav.UploadAsync(stale, ifMatch: "\"stale-etag\"").GetAwaiter().GetResult();
            return Fail("stale If-Match PUT was accepted (must be 412)");
        }
        catch (WebDavException exception) when (exception.Conflict)
        {
            Drill.Pass("stale If-Match PUT rejected with 412", exception.Message);
        }

        try
        {
            dav.UploadAsync(stale, ifMatch: null).GetAwaiter().GetResult();
            return Fail("If-None-Match: * PUT over an existing file was accepted");
        }
        catch (WebDavException exception) when (exception.Conflict)
        {
            Drill.Pass("If-None-Match: * PUT over an existing file rejected with 412",
                exception.Message);
        }

        byte[] second = Encoding.UTF8.GetBytes("mock-webdav protocol drill payload 2");
        string? etag2 = dav.UploadAsync(second, ifMatch: etag1).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(etag2) || etag2 == etag1)
        {
            return Fail("conditional PUT with the current ETag did not return a new ETag");
        }
        Drill.Pass("If-Match PUT with the current ETag succeeded", $"etag1={etag1} etag2={etag2}");

        byte[] finalBody = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!finalBody.AsSpan().SequenceEqual(second))
        {
            return Fail("GET after conditional PUT returns stale bytes");
        }
        Drill.Pass("conditional PUT content is what the server serves");

        using (HttpResponseMessage deleteResponse =
               http.DeleteAsync(url).GetAwaiter().GetResult())
        {
            if (deleteResponse.StatusCode != HttpStatusCode.NoContent)
            {
                return Fail($"DELETE returned {(int)deleteResponse.StatusCode} instead of 204");
            }
        }
        WebDavFileInfo? afterDelete;
        try
        {
            afterDelete = dav.GetMetadataAsync().GetAwaiter().GetResult();
        }
        catch (WebDavException exception) when (exception.StatusCode == 404)
        {
            afterDelete = null;
        }
        if (afterDelete is not null)
        {
            return Fail("PROPFIND after DELETE still finds the file");
        }
        Drill.Pass("DELETE removes the file (PROPFIND -> 404/null)");
        return 0;
    }

    // -------------------------------------------------------- offline replay

    /// <summary>
    /// (b) Offline edit then replay. Phase 1 seeds the mock remote over WebDAV,
    /// downloads the app's remote working copy and applies an offline edit
    /// through the FFI; the script then stops the server.
    /// </summary>
    public static int OfflinePrepare(string[] args)
    {
        string url = Drill.Arg(args, "--url");
        string fixture = Drill.Arg(args, "--fixture");
        string workdir = Drill.Arg(args, "--workdir");
        byte[] original = File.ReadAllBytes(fixture);

        using var dav = new WebDavClient(url, "drill", "drill");
        string? etag = dav.UploadAsync(original, ifMatch: null).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(etag))
        {
            return Fail("mock server returned no ETag for the seeded database");
        }
        byte[] remote = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!remote.AsSpan().SequenceEqual(original))
        {
            return Fail("downloaded remote bytes differ from the seeded fixture");
        }

        // Real app service: remote databases get a deterministic working copy.
        string localPath = RemoteDatabaseStore.PathFor(url, "drill");
        File.WriteAllBytes(localPath, remote);
        EnsureWorkingCopyExists(localPath);

        var store = new SyncStateStore();
        if (!store.Available)
        {
            return Fail("SyncStateStore is unavailable in this environment");
        }
        var state = new SyncState
        {
            Etag = etag,
            Sha256 = Drill.Sha256Hex(remote),
            Size = remote.Length,
            RemoteModifiedUnix = null,
            LocalDirty = false,
        };
        store.Save(url, "drill", localPath, state);
        Drill.Pass("seeded remote and downloaded working copy",
            $"etag={etag} local={localPath}");

        string editedTitle = "Offline edit (drill)";
        using (CoreApi api = CoreApi.Open(File.ReadAllBytes(localPath), Drill.MasterPassword))
        {
            List<(string Uuid, string Title)> entries = api.ListEntries();
            if (entries.Count != 500)
            {
                return Fail($"working copy has {entries.Count} entries, expected 500");
            }
            api.UpdateTitle(entries[0].Uuid, editedTitle);
            byte[] edited = api.Save();
            File.WriteAllBytes(localPath, edited);
            Drill.Pass("offline edit applied and saved through the FFI",
                $"{edited.Length} bytes (remote still {remote.Length} bytes)");
        }

        SyncState dirty = store.Load(url, "drill", localPath);
        dirty.LocalDirty = true;
        store.Save(url, "drill", localPath, dirty);
        Drill.Pass("SyncStateStore persists local dirty state");

        Drill.WriteJson(Drill.StatePath(workdir), new OfflineState
        {
            RemoteUrl = url,
            LocalPath = localPath,
            Etag = etag,
            Sha256 = Drill.Sha256Hex(remote),
            Size = remote.Length,
        });
        Drill.Info("stopping the mock server is the script's next step; the offline edit " +
                   "must not be uploaded yet");
        return 0;
    }

    /// <summary>
    /// (b) Phase 2 (server stopped): the remote is unreachable; the client must
    /// fail without losing the dirty local state.
    /// </summary>
    public static int OfflineUnreachable(string[] args)
    {
        string workdir = Drill.Arg(args, "--workdir");
        OfflineState state = Drill.ReadJson<OfflineState>(Drill.StatePath(workdir));

        using (var dav = new WebDavClient(state.RemoteUrl, "drill", "drill"))
        {
            try
            {
                dav.GetMetadataAsync().GetAwaiter().GetResult();
                return Fail("expected an unreachable remote, but PROPFIND succeeded");
            }
            catch (HttpRequestException exception)
            {
                Drill.Pass("remote unreachable (transport failure, no server)",
                    exception.Message);
            }

            try
            {
                dav.UploadAsync(File.ReadAllBytes(state.LocalPath), state.Etag)
                    .GetAwaiter().GetResult();
                return Fail("expected the offline upload to fail");
            }
            catch (HttpRequestException exception)
            {
                Drill.Pass("upload during outage fails without touching the remote",
                    exception.Message);
            }
        }

        var store = new SyncStateStore();
        SyncState syncState = store.Load(state.RemoteUrl, "drill", state.LocalPath);
        if (!syncState.LocalDirty)
        {
            return Fail("local dirty flag was lost during the outage");
        }
        Drill.Pass("SyncStateStore still reports local dirty after the failed sync");

        using (CoreApi localApi = CoreApi.Open(
                   File.ReadAllBytes(state.LocalPath), Drill.MasterPassword))
        {
            if (!localApi.SearchFinds("Offline edit (drill)"))
            {
                return Fail("local working copy does not contain the offline edit");
            }
        }
        Drill.Pass("offline-edited working copy is intact and still readable");

        Drill.Info("the app maps this transport failure to the core Offline decision " +
                   "(SyncState::on_offline, covered by cargo test sync_matrix scenario 14); " +
                   "the dirty flag above is what makes the replay possible.");
        return 0;
    }

    /// <summary>
    /// (b) Phase 3 (server back): verify the remote did not change during the
    /// outage, replay the upload, and commit the new baseline.
    /// </summary>
    public static int OfflineReplay(string[] args)
    {
        string workdir = Drill.Arg(args, "--workdir");
        OfflineState state = Drill.ReadJson<OfflineState>(Drill.StatePath(workdir));
        string url = Drill.ArgOrNull(args, "--url") ?? state.RemoteUrl;
        byte[] localBytes = File.ReadAllBytes(state.LocalPath);

        using var dav = new WebDavClient(url, "drill", "drill");
        WebDavFileInfo? metadata = dav.GetMetadataAsync().GetAwaiter().GetResult();
        if (metadata is null)
        {
            return Fail("remote file disappeared while the client was offline");
        }
        if (metadata.ETag != state.Etag)
        {
            return Fail($"remote changed during the outage (etag {state.Etag} -> {metadata.ETag}); " +
                        "the drill script expects an unchanged remote");
        }
        Drill.Pass("remote reachable again and unchanged (ETag matches the baseline)",
            metadata.ETag);

        byte[] remoteBytes = dav.DownloadAsync().GetAwaiter().GetResult();
        if (Drill.Sha256Hex(remoteBytes) != state.Sha256)
        {
            return Fail("remote content hash differs from the recorded baseline");
        }
        Drill.Pass("remote content is still the pre-outage baseline",
            Drill.Sha256Hex(remoteBytes));

        using CoreApi api = CoreApi.Open(localBytes, Drill.MasterPassword);
        api.SyncCommit(state.Etag, state.Sha256);
        string decisionJson = api.SyncDecide(Drill.RemoteMetaJson(metadata), localDirty: true);
        string action = Drill.ReadAction(decisionJson);
        if (action != "upload")
        {
            return Fail($"FFI sync decision is \"{action}\", expected \"upload\" (remote unchanged, local dirty)");
        }
        Drill.Pass("FFI kp_sync_decide returned upload", decisionJson);

        string? newEtag = dav.UploadAsync(localBytes, state.Etag).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(newEtag))
        {
            return Fail("replayed upload returned no ETag");
        }
        Drill.Pass("replayed local changes when the remote became available",
            $"{state.Etag} -> {newEtag}");

        byte[] uploaded = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!uploaded.AsSpan().SequenceEqual(localBytes))
        {
            return Fail("remote bytes differ from the replayed local bytes");
        }
        using (CoreApi remoteApi = CoreApi.Open(uploaded, Drill.MasterPassword))
        {
            int entries = remoteApi.EntryCount();
            if (entries != 500)
            {
                return Fail($"uploaded remote has {entries} entries, expected 500");
            }
            if (!remoteApi.SearchFinds("Offline edit (drill)"))
            {
                return Fail("uploaded remote is missing the offline edit");
            }
        }
        Drill.Pass("uploaded remote reopens with 500 entries and contains the offline edit",
            $"{uploaded.Length} bytes");

        api.SyncCommit(newEtag, Drill.Sha256Hex(localBytes));
        var store = new SyncStateStore();
        SyncState syncState = store.Load(state.RemoteUrl, "drill", state.LocalPath);
        syncState.Etag = newEtag;
        syncState.Sha256 = Drill.Sha256Hex(localBytes);
        syncState.Size = localBytes.Length;
        syncState.LocalDirty = false;
        store.Save(state.RemoteUrl, "drill", state.LocalPath, syncState);
        Drill.Pass("sync baseline committed and sidecar marked clean");
        return 0;
    }

    // --------------------------------------------------------------- conflict

    /// <summary>
    /// (c) Conflict: remote changed by another client + local dirty. Proves the
    /// core decision, the 412 precondition (no silent overwrite), and the FFI
    /// merge keeping both sides. The WinUI conflict dialog remains manual.
    /// </summary>
    public static int Conflict(string[] args)
    {
        string url = Drill.Arg(args, "--url");
        string fixture = Drill.Arg(args, "--fixture");
        string workdir = Drill.Arg(args, "--workdir");
        byte[] original = File.ReadAllBytes(fixture);

        using var dav = new WebDavClient(url, "drill", "drill");
        string? etagA = dav.UploadAsync(original, ifMatch: null).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(etagA))
        {
            return Fail("mock server returned no ETag for the seeded database");
        }
        byte[] baseBytes = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!baseBytes.AsSpan().SequenceEqual(original))
        {
            return Fail("downloaded base bytes differ from the fixture");
        }
        string localPath = RemoteDatabaseStore.PathFor(url, "drill");
        File.WriteAllBytes(localPath, baseBytes);
        var store = new SyncStateStore();
        var state = new SyncState
        {
            Etag = etagA,
            Sha256 = Drill.Sha256Hex(baseBytes),
            Size = baseBytes.Length,
            LocalDirty = false,
        };
        store.Save(url, "drill", localPath, state);
        Drill.Pass("remote seeded", "etag=" + etagA);

        // "Another device" changes one entry and uploads it with the old ETag.
        string remoteTitle = "Remote edit (drill)";
        byte[] remoteEdited;
        using (CoreApi remoteApi = CoreApi.Open(baseBytes, Drill.MasterPassword))
        {
            List<(string Uuid, string Title)> entries = remoteApi.ListEntries();
            remoteApi.UpdateTitle(entries[1].Uuid, remoteTitle);
            remoteEdited = remoteApi.Save();
        }
        string? etagB;
        using (var otherDevice = new WebDavClient(url, "drill", "drill"))
        {
            etagB = otherDevice.UploadAsync(remoteEdited, etagA).GetAwaiter().GetResult();
        }
        if (string.IsNullOrEmpty(etagB) || etagB == etagA)
        {
            return Fail("remote change was not accepted (expected a new ETag)");
        }
        Drill.Pass("remote changed by another client and uploaded", $"etagA -> {etagB}");

        // Local device changes a different entry while offline/unsynced.
        string localTitle = "Local edit (drill)";
        byte[] localEdited;
        using (CoreApi localApi = CoreApi.Open(baseBytes, Drill.MasterPassword))
        {
            List<(string Uuid, string Title)> entries = localApi.ListEntries();
            localApi.UpdateTitle(entries[0].Uuid, localTitle);
            localEdited = localApi.Save();
        }
        File.WriteAllBytes(localPath, localEdited);
        SyncState dirty = store.Load(url, "drill", localPath);
        dirty.LocalDirty = true;
        store.Save(url, "drill", localPath, dirty);
        Drill.Pass("local database changed on a disjoint entry; sidecar marked dirty");

        WebDavFileInfo? metadata = dav.GetMetadataAsync().GetAwaiter().GetResult();
        if (metadata is null || metadata.ETag != etagB)
        {
            return Fail("PROPFIND does not report the remote change");
        }

        using CoreApi api = CoreApi.Open(localEdited, Drill.MasterPassword);
        api.SyncCommit(etagA, Drill.Sha256Hex(baseBytes));
        string decisionJson = api.SyncDecide(Drill.RemoteMetaJson(metadata), localDirty: true);
        string action = Drill.ReadAction(decisionJson);
        if (action != "download_and_merge")
        {
            return Fail($"FFI sync decision is \"{action}\", expected \"download_and_merge\" " +
                        "(remote changed + local dirty)");
        }
        Drill.Pass("FFI kp_sync_decide returned download_and_merge", decisionJson);

        // A naive upload with the stale baseline must be refused, and both sides
        // must stay untouched (this is the "no silent overwrite" evidence).
        try
        {
            new WebDavClient(url, "drill", "drill")
                .UploadAsync(localEdited, etagA).GetAwaiter().GetResult();
            return Fail("stale If-Match upload was accepted: silent overwrite risk");
        }
        catch (WebDavException exception) when (exception.Conflict)
        {
            Drill.Pass("stale If-Match upload rejected with HTTP 412", exception.Message);
        }

        byte[] afterReject = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!afterReject.AsSpan().SequenceEqual(remoteEdited))
        {
            return Fail("remote bytes changed after the rejected upload");
        }
        Drill.Pass("remote bytes unchanged after the rejected upload");
        if (!File.ReadAllBytes(localPath).AsSpan().SequenceEqual(localEdited))
        {
            return Fail("local dirty working copy was modified before merge");
        }
        Drill.Pass("local dirty working copy preserved");

        // Merge through the FFI, exactly like MainWindow.MergeRemoteAsync does
        // before it looks at conflicts.
        (byte[] mergedBytes, string conflictsJson) = api.SyncMerge(afterReject);
        File.WriteAllBytes(Path.Combine(workdir, "merged.kdbx"), mergedBytes);
        using (JsonDocument conflicts = JsonDocument.Parse(conflictsJson))
        {
            int count = conflicts.RootElement.GetArrayLength();
            Drill.Info("FFI merge conflicts JSON: " + conflictsJson);
            if (count != 0)
            {
                return Fail($"disjoint edits produced {count} conflict(s); the conflict " +
                            "dialog path is manual, so the scripted drill cannot continue");
            }
        }
        Drill.Pass("FFI kp_sync_merge reported 0 conflicts for disjoint edits",
            $"merged={mergedBytes.Length} bytes");

        using (CoreApi mergedApi = CoreApi.Open(mergedBytes, Drill.MasterPassword))
        {
            int entries = mergedApi.EntryCount();
            if (entries != 500)
            {
                return Fail($"merged database has {entries} entries, expected 500");
            }
            if (!mergedApi.SearchFinds(remoteTitle))
            {
                return Fail("merged database lost the remote edit");
            }
            if (!mergedApi.SearchFinds(localTitle))
            {
                return Fail("merged database lost the local edit");
            }
        }
        Drill.Pass("merged database contains both the remote and the local edit");

        string? etagC = dav.UploadAsync(mergedBytes, metadata.ETag).GetAwaiter().GetResult();
        if (string.IsNullOrEmpty(etagC))
        {
            return Fail("merged upload returned no ETag");
        }
        byte[] finalRemote = dav.DownloadAsync().GetAwaiter().GetResult();
        if (!finalRemote.AsSpan().SequenceEqual(mergedBytes))
        {
            return Fail("remote bytes differ from the uploaded merge result");
        }
        using (CoreApi finalApi = CoreApi.Open(finalRemote, Drill.MasterPassword))
        {
            if (!finalApi.SearchFinds(remoteTitle) || !finalApi.SearchFinds(localTitle))
            {
                return Fail("remote merge result lost one of the edits");
            }
        }
        Drill.Pass("merged result uploaded; remote reopens with both edits", "etag=" + etagC);

        api.SyncCommit(etagC, Drill.Sha256Hex(mergedBytes));
        SyncState synced = store.Load(url, "drill", localPath);
        synced.Etag = etagC;
        synced.Sha256 = Drill.Sha256Hex(mergedBytes);
        synced.Size = mergedBytes.Length;
        synced.LocalDirty = false;
        store.Save(url, "drill", localPath, synced);
        Drill.Pass("sync baseline committed and sidecar marked clean");

        Drill.Info("still manual/UI: the same-entry same-mtime conflict dialog and the " +
                   "conflict-copy files are created in MainWindow.MergeRemoteAsync " +
                   "(WinUI); the core conflict plan/copy naming is covered by " +
                   "cargo test -p keepassity-core --test sync_matrix (scenario 07).");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine("FAIL  " + message);
        return 1;
    }

    private static void EnsureWorkingCopyExists(string localPath)
    {
        if (!File.Exists(localPath))
        {
            throw new InvalidOperationException("remote working copy was not written");
        }
    }
}
