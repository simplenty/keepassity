// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Keepassity.Mock;

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

    public SyncStateStore()
    {
        _directory = Path.Combine(AppDataPaths.Root, "state");
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

    private SyncState LoadFrom(string path)
    {
        if (!Available)
        {
            return new SyncState();
        }
        try
        {
            if (File.Exists(path))
            {
                SyncState? state = JsonSerializer.Deserialize<SyncState>(
                    File.ReadAllText(path), Options);
                if (state is not null)
                {
                    return state;
                }
            }
        }
        catch
        {
            // Corrupt sidecar: start with a clean baseline.
        }
        return new SyncState();
    }

    private void SaveTo(string path, SyncState state)
    {
        if (!Available)
        {
            return;
        }
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Options));
        File.Move(temp, path, true);
    }

    private string PathFor(string remoteUrl, string username, string localPath)
    {
        string key = $"{remoteUrl.Trim()}\n{username.Trim()}\n{localPath.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".json");
    }
}
