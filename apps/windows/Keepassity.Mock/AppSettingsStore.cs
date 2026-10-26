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
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "keepassity");
        Directory.CreateDirectory(directory);
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
