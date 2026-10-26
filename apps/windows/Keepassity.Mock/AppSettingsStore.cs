// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Keepassity.Mock;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public bool ClearClipboardAfter30Seconds { get; set; } = true;
    public bool AutoLockEnabled { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 15;
    public string? LastDatabasePath { get; set; }
    public string? WebDavUrl { get; set; }
    public string? WebDavUsername { get; set; }
    public bool SyncOnSave { get; set; } = true;
    public int BackupRetention { get; set; } = 20;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public AppSettingsStore()
    {
        string directory = AppDataPaths.Root;
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // Read-only fallback: settings still load if the file exists, and
            // Save() below is best-effort.
        }
        _path = Path.Combine(directory, "settings.json");
        Current = Load();
    }

    public AppSettings Current { get; }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults; no vault data
            // is stored in this file.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Current, Options));
            File.Move(temp, _path, true);
        }
        catch
        {
            // Preferences are best-effort; never block vault operations on them.
        }
    }
}
