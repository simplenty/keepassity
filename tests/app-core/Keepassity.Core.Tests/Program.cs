// SPDX-License-Identifier: GPL-3.0-or-later
// Headless checks for the platform-neutral application services. This is a small
// console harness instead of xUnit so it runs without adding test-framework
// dependencies; extend it when a real test project is introduced.
using Keepassity.Mock;

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name +
                      (detail.Length > 0 ? " -> " + detail : string.Empty));
    if (!ok)
    {
        failures++;
    }
}

string baseDirectory = Environment.GetEnvironmentVariable("KEEPASSITY_CORE_TESTS_DIR")
    ?? Path.GetTempPath();
Directory.CreateDirectory(baseDirectory);
string dataDirectory = Path.Combine(
    baseDirectory, "keepassity-core-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDirectory);
Environment.SetEnvironmentVariable("KEEPASSITY_DATA_DIR", dataDirectory);
AppLog.Initialize();

try
{
    // settings round trip and schema version
    var settings = new AppSettingsStore();
    settings.Current.Theme = "Light";
    settings.Current.AutoLockMinutes = 42;
    settings.Save();
    var reloaded = new AppSettingsStore();
    Check("settings persist theme",
        reloaded.Current.Theme == "Light", reloaded.Current.Theme);
    Check("settings persist auto-lock",
        reloaded.Current.AutoLockMinutes == 42,
        reloaded.Current.AutoLockMinutes.ToString());
    Check("settings schema version", reloaded.Current.SchemaVersion == 1,
        reloaded.Current.SchemaVersion.ToString());

    // sync state round trip
    var syncStates = new SyncStateStore();
    if (syncStates.Available)
    {
        var state = new SyncState
        {
            Etag = "\"abc\"",
            Sha256 = new string('a', 64),
            Size = 1234,
            RemoteModifiedUnix = 1_700_000_000,
            LocalDirty = true,
            LastSyncUnix = 1_700_000_100,
        };
        syncStates.Save("https://example.test/db.kdbx", "user", "/tmp/db.kdbx", state);
        SyncState loaded = syncStates.Load(
            "https://example.test/db.kdbx", "user", "/tmp/db.kdbx");
        Check("sync state round trip",
            loaded.Etag == state.Etag && loaded.Size == state.Size &&
            loaded.LocalDirty && loaded.LastSyncUnix == state.LastSyncUnix);
        SyncState otherUser = syncStates.Load(
            "https://example.test/db.kdbx", "other", "/tmp/db.kdbx");
        Check("sync state is keyed by user", otherUser.Etag is null);
    }
    else
    {
        Console.WriteLine("SKIP  sync state store unavailable");
    }

    // remote working-copy paths are deterministic and distinct
    string a = RemoteDatabaseStore.PathFor(
        "https://example.test/db.kdbx", "user");
    string b = RemoteDatabaseStore.PathFor(
        "https://example.test/db.kdbx", "user");
    string c = RemoteDatabaseStore.PathFor(
        "https://example.test/db.kdbx", "other");
    Check("working copy path deterministic", a == b, a);
    Check("working copy path keyed by user", a != c);
    Check("working copy path has kdbx suffix",
        a.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase));

    // DPAPI credential round trip (Windows only)
    var credentials = new CredentialStore();
    if (credentials.Available)
    {
        credentials.SavePassword("https://example.test/db.kdbx", "user", "secret-value");
        string? password = credentials.LoadPassword(
            "https://example.test/db.kdbx", "user");
        Check("credential round trip", password == "secret-value");
        Check("missing credential returns null",
            credentials.LoadPassword("https://example.test/other.kdbx", "user") is null);
    }
    else
    {
        Console.WriteLine("SKIP  credential store unavailable");
    }

    // backups
    string database = Path.Combine(dataDirectory, "sample.kdbx");
    File.WriteAllBytes(database, new byte[] { 1, 2, 3, 4 });
    BackupEntry? backup = BackupService.CreateBackup(database, retention: 3);
    Check("backup created", backup is not null && File.Exists(backup.Path));
    Check("backup appears in list",
        BackupService.ListBackups(database).Count >= 1);

    // diagnostics archive contains a summary and no credential blobs
    string archive = DiagnosticsService.ExportAsync(reloaded.Current)
        .GetAwaiter().GetResult();
    Check("diagnostics archive exists", File.Exists(archive));
    using (var zip = System.IO.Compression.ZipFile.OpenRead(archive))
    {
        Check("diagnostics includes summary",
            zip.GetEntry("summary.txt") is not null);
        Check("diagnostics includes settings snapshot",
            zip.GetEntry("settings.json") is not null);
    }

    // logging
    AppLog.Info("test log marker");
    string? logFile = Directory.EnumerateFiles(AppLog.DirectoryPath, "app-*.log")
        .OrderByDescending(File.GetLastWriteTime)
        .FirstOrDefault();
    Check("app log written", logFile is not null &&
        File.ReadAllText(logFile).Contains("test log marker"));
}
catch (Exception exception)
{
    Console.WriteLine("FAIL  unexpected exception: " + exception);
    failures++;
}
finally
{
    try { Directory.Delete(dataDirectory, recursive: true); } catch { }
}

Console.WriteLine(failures == 0 ? "CORE TESTS PASS" : $"CORE TESTS FAIL ({failures})");
return failures == 0 ? 0 : 1;
