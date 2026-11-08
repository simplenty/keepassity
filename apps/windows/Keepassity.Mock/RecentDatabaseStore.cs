// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Keepassity.Mock;

public sealed class RecentDatabaseEntry
{
    public string Path { get; set; } = "";
    /// <summary>"local" or "webdav".</summary>
    public string Kind { get; set; } = "local";
    public string? WebDavUrl { get; set; }
    public string? WebDavUsername { get; set; }
    public long OpenedUnix { get; set; }

    public string DisplayName
    {
        get
        {
            string name = System.IO.Path.GetFileName(Path);
            return string.IsNullOrWhiteSpace(name) ? Path : name;
        }
    }
}

/// <summary>
/// Remembers the last few opened databases for the unlock screen. Stores only
/// paths plus the WebDAV target facts settings.json already keeps; never vault
/// content, master passwords or WebDAV passwords. Kept out of settings.json so
/// the recent list can grow without changing the settings schema.
/// </summary>
public sealed class RecentDatabaseStore
{
    public const int MaxEntries = 5;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _filePath;
    private readonly List<RecentDatabaseEntry> _entries = new();

    public RecentDatabaseStore()
    {
        _filePath = Path.Combine(AppDataPaths.Root, "recent-databases.json");
        Load();
    }

    public IReadOnlyList<RecentDatabaseEntry> Entries => _entries;

    /// <summary>Moves (or adds) the database to the front of the recent list.</summary>
    public void Remember(string path, string kind = "local", string? webDavUrl = null,
        string? webDavUsername = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        string normalized = Normalize(path);
        _entries.RemoveAll(entry => string.Equals(
            Normalize(entry.Path), normalized, StringComparison.OrdinalIgnoreCase));
        _entries.Insert(0, new RecentDatabaseEntry
        {
            Path = normalized,
            Kind = string.Equals(kind, "webdav", StringComparison.OrdinalIgnoreCase)
                ? "webdav"
                : "local",
            WebDavUrl = webDavUrl,
            WebDavUsername = webDavUsername,
            OpenedUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        });
        while (_entries.Count > MaxEntries)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
        Save();
    }

    public bool Remove(string path)
    {
        string normalized = Normalize(path);
        int removed = _entries.RemoveAll(entry => string.Equals(
            Normalize(entry.Path), normalized, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            Save();
        }
        return removed > 0;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            List<RecentDatabaseEntry>? loaded = JsonSerializer.Deserialize<
                List<RecentDatabaseEntry>>(File.ReadAllText(_filePath), Options);
            if (loaded is null)
            {
                return;
            }
            foreach (RecentDatabaseEntry entry in loaded)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }
                entry.Path = Normalize(entry.Path);
                _entries.Add(entry);
                if (_entries.Count >= MaxEntries)
                {
                    break;
                }
            }
        }
        catch
        {
            // A corrupt recent list must never block unlocking; start empty.
        }
    }

    private void Save()
    {
        try
        {
            string temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Options));
            File.Move(temp, _filePath, true);
        }
        catch
        {
            // Remembering a path is a convenience; never block vault operations.
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }
}
