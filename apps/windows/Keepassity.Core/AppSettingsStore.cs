// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Keepassity.Core;

public sealed class AppSettings
{
    /// <summary>Schema version of this settings file; bump when fields change meaning.</summary>
    public int SchemaVersion { get; set; } = 1;
    public string Theme { get; set; } = "Dark";
    /// <summary>
    /// UI language: <c>system</c> follows the Windows display language, or an
    /// explicit tag such as <c>en</c> / <c>zh-CN</c>. The Windows shell applies it
    /// through <c>Loc</c> at startup and when Settings changes it.
    /// </summary>
    public string Language { get; set; } = "system";
    public bool ClearClipboardAfter30Seconds { get; set; } = true;
    public bool AutoLockEnabled { get; set; } = true;
    public int AutoLockMinutes { get; set; } = 15;
    public string? LastDatabasePath { get; set; }
    /// <summary>"local" or "webdav": how the last database was opened.</summary>
    public string? LastDatabaseKind { get; set; }
    public string? LastWebDavUrl { get; set; }
    public string? LastWebDavUsername { get; set; }
    public string? WebDavUrl { get; set; }
    public string? WebDavUsername { get; set; }
    public bool SyncOnSave { get; set; } = true;
    public int BackupRetention { get; set; } = 20;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool StartMinimized { get; set; }
    /// <summary>
    /// Explicitly trusted self-signed WebDAV certificates (SY-04), keyed by
    /// <c>host:port</c>; the value is the lowercase hex SHA-256 fingerprint. Only
    /// the exact certificate is accepted, and only after the user confirmed its
    /// fingerprint in the trust dialog.
    /// </summary>
    public Dictionary<string, string> TrustedCertificates { get; set; } = new();
}

public sealed class AppSettingsStore
{
    private const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;
    private readonly bool _readOnly;
    private bool _warnedReadOnly;

    /// <summary>
    /// <paramref name="path"/> is for tests and diagnostics; the app uses the
    /// application data root.
    /// </summary>
    public AppSettingsStore(string? path = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            string directory = ApplicationPaths.Root;
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
        }
        else
        {
            _path = Path.GetFullPath(path);
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }
        Current = Load(out bool readOnly);
        _readOnly = readOnly;
    }

    public AppSettings Current { get; }

    /// <summary>
    /// True when the file was written by a newer schema. Saving in that state
    /// would silently discard fields this build does not understand, so Save()
    /// leaves the file untouched.
    /// </summary>
    public bool IsReadOnly => _readOnly;

    private AppSettings Load(out bool readOnly)
    {
        readOnly = false;
        try
        {
            if (File.Exists(_path))
            {
                string json = File.ReadAllText(_path);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    if (loaded.SchemaVersion <= 0)
                    {
                        loaded.SchemaVersion = CurrentSchema;
                    }
                    else if (loaded.SchemaVersion > CurrentSchema)
                    {
                        readOnly = true;
                        AppLog.Warn($"settings schema {loaded.SchemaVersion} is newer than " +
                                    $"supported ({CurrentSchema}); not overwriting it");
                    }
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings fall back to defaults, but keep the
            // original bytes for diagnosis; vault data is never stored here.
            PreserveCorruptFile();
            return new AppSettings();
        }
        PreserveCorruptFile();
        return new AppSettings();
    }

    public void Save()
    {
        if (_readOnly)
        {
            if (!_warnedReadOnly)
            {
                _warnedReadOnly = true;
                AppLog.Warn("settings save skipped: the on-disk settings have a newer schema");
            }
            return;
        }
        try
        {
            AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(Current, Options));
        }
        catch
        {
            // Preferences are best-effort; never block vault operations on them.
        }
    }

    private void PreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            string backup = _path + ".corrupt-" + DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            File.Copy(_path, backup, overwrite: false);
            AppLog.Warn($"preserved unreadable settings as {Path.GetFileName(backup)}");
        }
        catch
        {
            // Diagnostics must not prevent the app from starting.
        }
    }
}
