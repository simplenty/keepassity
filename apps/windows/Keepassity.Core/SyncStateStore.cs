// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keepassity.Core;

public sealed class SyncState
{
    public string? Etag { get; set; }
    public string? Sha256 { get; set; }
    public long Size { get; set; }
    public long? RemoteModifiedUnix { get; set; }
    public bool LocalDirty { get; set; }
    public long? LastSyncUnix { get; set; }
}

/// <summary>Persisted ETag/hash/dirty baseline, keyed by remote URL, user and local path.</summary>
public sealed class SyncStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;

    /// <summary>
    /// <paramref name="directory"/> is for tests and diagnostics; the app uses
    /// the application data root.
    /// </summary>
    public SyncStateStore(string? directory = null)
    {
        _directory = string.IsNullOrWhiteSpace(directory)
            ? Path.Combine(ApplicationPaths.Root, "state")
            : Path.GetFullPath(directory);
        try
        {
            Directory.CreateDirectory(_directory);
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public bool Available { get; }

    public SyncState Load(string remoteUrl, string username, string localPath)
        => LoadFrom(PathFor(remoteUrl, username, localPath));

    public void Save(string remoteUrl, string username, string localPath, SyncState state)
        => SaveTo(PathFor(remoteUrl, username, localPath), state);

    /// <summary>Sidecar path for one remote source/local path pair.</summary>
    public string StatePathFor(string remoteUrl, string username, string localPath)
        => PathFor(remoteUrl, username, localPath);

    /// <summary>Remove the sidecar (used when a cached working copy is deleted).</summary>
    public void Remove(string remoteUrl, string username, string localPath)
    {
        try
        {
            string path = PathFor(remoteUrl, username, localPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn($"sync state could not be removed: {ex.Message}");
        }
    }

    private SyncState LoadFrom(string path)
    {
        if (!Available)
        {
            // Without a state directory the baseline is unknown. Do not report
            // "clean": a clean baseline is what lets a sync overwrite local data.
            return new SyncState { LocalDirty = true };
        }
        SyncState? state = null;
        bool existed = File.Exists(path);
        if (existed)
        {
            try
            {
                state = JsonSerializer.Deserialize<SyncState>(File.ReadAllText(path), Options);
            }
            catch (Exception ex)
            {
                PreserveCorruptFile(path);
                AppLog.Warn($"sync state is unreadable; treating the vault as dirty: {ex.Message}");
            }
        }
        if (state is null)
        {
            if (existed)
            {
                PreserveCorruptFile(path);
            }
            // Missing on first sync is normal and clean. An existing but
            // unreadable sidecar is not: fail closed so the next sync uploads
            // or merges instead of overwriting the local database.
            return new SyncState { LocalDirty = existed };
        }
        if (!IsSha256(state.Sha256))
        {
            // A half-written baseline from an older build cannot prove the
            // local and remote copies match; keep the local edits.
            state.LocalDirty = true;
        }
        return state;
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f'
               or >= 'A' and <= 'F');

    private void SaveTo(string path, SyncState state)
    {
        if (!Available)
        {
            return;
        }
        AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(state, Options));
    }

    private static void PreserveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }
            string backup = path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Copy(path, backup, overwrite: false);
            AppLog.Warn($"preserved unreadable sync state as {Path.GetFileName(backup)}");
        }
        catch
        {
            // Diagnostics must not prevent a sync from proceeding dirty.
        }
    }

    private string PathFor(string remoteUrl, string username, string localPath)
    {
        string key = $"{remoteUrl.Trim()}\n{username.Trim()}\n{localPath.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".json");
    }
}
