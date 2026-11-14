// SPDX-License-Identifier: GPL-3.0-or-later
// Headless checks for the platform-neutral application services. This is a small
// console harness instead of xUnit so it runs without adding test-framework
// dependencies; extend it when a real test project is introduced.
using Keepassity.CoreTests;
using Keepassity.Core;

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
    settings.Current.Language = "zh-CN";
    settings.Save();
    var reloaded = new AppSettingsStore();
    Check("settings persist theme",
        reloaded.Current.Theme == "Light", reloaded.Current.Theme);
    Check("settings persist auto-lock",
        reloaded.Current.AutoLockMinutes == 42,
        reloaded.Current.AutoLockMinutes.ToString());
    Check("settings persist UI language",
        reloaded.Current.Language == "zh-CN", reloaded.Current.Language);
    Check("settings schema version", reloaded.Current.SchemaVersion == 1,
        reloaded.Current.SchemaVersion.ToString());

    // UI language catalog: resolution, fallback, formatting and catalog hygiene.
    LocalizationTests.Run(Check);

    // MVVM: the unlock decision path is headless and has its own fakes.
    UnlockViewModelTests.Run(Check, dataDirectory);
    SyncStatusViewModelTests.Run(Check);
    SettingsViewModelTests.Run(Check);
    EntryListViewModelTests.Run(Check);
    EntryDetailViewModelTests.Run(Check);
    EntryEditorViewModelTests.Run(Check);
    GroupNavigationTests.Run(Check);
    TotpTimingTests.Run(Check);
    TotpDisplayViewModelTests.Run(Check);

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

        // A corrupt or half-written sidecar must fail closed: treating it as
        // clean lets the next sync download over local edits.
        string corruptUrl = "https://example.test/corrupt.kdbx";
        syncStates.Save(corruptUrl, "user", "/tmp/corrupt.kdbx",
            new SyncState
            {
                Etag = "\"etag\"",
                Sha256 = new string('b', 64),
                LastSyncUnix = 987654321,
            });
        string stateDirectory = Path.Combine(ApplicationPaths.Root, "state");
        string? sidecar = Directory.Exists(stateDirectory)
            ? Directory.EnumerateFiles(stateDirectory, "*.json")
                .FirstOrDefault(file => File.ReadAllText(file).Contains(
                    "987654321", StringComparison.Ordinal))
            : null;
        Check("sync state sidecar exists for corruption test", sidecar is not null);
        if (sidecar is not null)
        {
            File.WriteAllText(sidecar, "{ not valid json");
            SyncState recovered = syncStates.Load(corruptUrl, "user", "/tmp/corrupt.kdbx");
            Check("corrupt sync state is treated as dirty", recovered.LocalDirty);
        }

        syncStates.Save("https://example.test/half.kdbx", "user", "/tmp/half.kdbx",
            new SyncState { Etag = "\"etag\"", Sha256 = null });
        SyncState half = syncStates.Load(
            "https://example.test/half.kdbx", "user", "/tmp/half.kdbx");
        Check("incomplete sync baseline is treated as dirty", half.LocalDirty);
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

    // M4-04: the WebDAV password never reaches the settings file, and the DPAPI
    // credential store round trips, reports presence and deletes credentials.
    SettingsAndCredentialTests.Run(Check);

    // M3-09: tray menu flyout placement (pure geometry; the WinUI host only maps
    // the result to FlyoutPlacementMode).
    TrayMenuPlacement.Result topTaskbar = TrayMenuPlacement.Choose(
        anchorX: 1800, anchorY: 300, iconWidth: 24, iconHeight: 24,
        workTop: 48, workRight: 2560, workBottom: 1600);
    Check("tray menu opens below the icon on a top taskbar",
        topTaskbar.Side == TrayMenuPlacement.VerticalSide.Below,
        topTaskbar.Side.ToString());
    Check("tray menu is left aligned away from the right edge", !topTaskbar.RightAligned);
    TrayMenuPlacement.Result bottomTaskbar = TrayMenuPlacement.Choose(
        anchorX: 1200, anchorY: 1500, iconWidth: 24, iconHeight: 24,
        workTop: 0, workRight: 2560, workBottom: 1550);
    Check("tray menu opens above the icon on a bottom taskbar",
        bottomTaskbar.Side == TrayMenuPlacement.VerticalSide.Above,
        bottomTaskbar.Side.ToString());
    TrayMenuPlacement.Result rightEdge = TrayMenuPlacement.Choose(
        anchorX: 2500, anchorY: 300, iconWidth: 24, iconHeight: 24,
        workTop: 48, workRight: 2560, workBottom: 1600);
    Check("tray menu right aligns near the right screen edge", rightEdge.RightAligned);

    // M4-01: WebDavClient against an in-process WebDAV mock on a CJK path; also
    // proves the client sends the percent-encoded request target.
    WebDavNonAsciiPathTests.Run(Check);
    WebDavSafetyTests.Run(Check);
    WebDavTlsTests.Run(Check);

    // backups
    string database = Path.Combine(dataDirectory, "sample.kdbx");
    File.WriteAllBytes(database, new byte[] { 1, 2, 3, 4 });
    BackupEntry? backup = BackupService.CreateBackup(database, retention: 3);
    Check("backup created", backup is not null && File.Exists(backup.Path));
    Check("backup appears in list",
        BackupService.ListBackups(database).Count >= 1);

    // M4-05: retention boundary (custom and default 20), restore ordering and
    // rejection of a corrupted backup with the live database left untouched.
    BackupServiceTests.Run(Check, dataDirectory);
    AtomicFileTests.Run(Check, dataDirectory);
    VaultGuardTests.Run(Check, dataDirectory);
    VaultWriteGateTests.Run(Check);
    RecentDatabaseTests.Run(Check, dataDirectory);
    SyncStateStoreTests.Run(Check, dataDirectory);
    RemoteVaultOpenerTests.Run(Check, dataDirectory);
    RemoteVaultOpenIntegrationTests.Run(Check, dataDirectory);
    SyncCoordinatorTests.Run(Check, dataDirectory);

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
