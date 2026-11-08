// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// App-level smoke test used by --selftest: open a fixture copy, list, read TOTP,
/// save, reopen and verify. Secrets are read from KEEPASSITY_SELFTEST_PASSWORD
/// and never written to the report.
/// </summary>
internal static class AppSelfTest
{
    public static int Run(string databasePath, string reportPath)
    {
        var lines = new List<string>();
        void Record(string message)
        {
            lines.Add(message);
            try
            {
                File.AppendAllText(reportPath, message + Environment.NewLine);
            }
            catch
            {
                // Report writing is diagnostic.
            }
        }
        string reportDirectory = Path.GetDirectoryName(Path.GetFullPath(reportPath))
            ?? AppDataPaths.Root;
        Directory.CreateDirectory(reportDirectory);
        // The app may run at low integrity (portable folder in a hardened workspace),
        // so keep the temporary copy inside the application data root.
        string tempDirectory = Path.Combine(AppDataPaths.Root, "selftest");
        Directory.CreateDirectory(tempDirectory);
        string temp = Path.Combine(tempDirectory,
            $"keepassity-selftest-{Guid.NewGuid():N}.kdbx");
        int code = 1;
        try
        {
            AppLog.Info($"selftest report: {reportPath}");
            Record("start");
            string? password = Environment.GetEnvironmentVariable("KEEPASSITY_SELFTEST_PASSWORD");
            if (string.IsNullOrEmpty(password))
            {
                throw new InvalidOperationException(
                    "KEEPASSITY_SELFTEST_PASSWORD is required.");
            }
            if (!File.Exists(databasePath))
            {
                throw new FileNotFoundException("Fixture not found.", databasePath);
            }
            // Branding assets must be copied next to the executable; the window
            // icon, tray icon and About logo all load them from there.
            Record("checking icon assets");
            if (!File.Exists(AppAssets.AppIconPath) ||
                !File.Exists(AppAssets.AppIconPreviewPath))
            {
                throw new InvalidOperationException(
                    "Icon assets are missing next to the executable (Assets/app.ico, " +
                    "Assets/app-icon-256.png).");
            }
            string executable = Environment.ProcessPath ?? string.Empty;
            using (System.Drawing.Icon? executableIcon =
                   System.Drawing.Icon.ExtractAssociatedIcon(executable))
            {
                if (executableIcon is null || executableIcon.Width < 16)
                {
                    throw new InvalidOperationException(
                        "The executable has no application icon resource.");
                }
                Record($"icons: assets present, executable icon " +
                       $"{executableIcon.Width}x{executableIcon.Height}");
            }
            using (System.Drawing.Image aboutPreview =
                   System.Drawing.Image.FromFile(AppAssets.AppIconPreviewPath))
            {
                if (aboutPreview.Width < 64 || aboutPreview.Height < 64)
                {
                    throw new InvalidOperationException(
                        "The About icon preview is smaller than 64px.");
                }
                Record($"icons: about preview {aboutPreview.Width}x{aboutPreview.Height}");
            }
            // A bare .kdbx path (file association / Explorer "Open with") must be
            // treated like --db; unrelated switches must be ignored.
            Record("checking launch arguments");
            string? fromSwitch = LaunchArguments.FindDatabasePath(
                new[] { "keepassity.exe", "--db", databasePath }, File.Exists);
            string? fromAssociation = LaunchArguments.FindDatabasePath(
                new[] { "keepassity.exe", databasePath }, File.Exists);
            string? ignoredSwitch = LaunchArguments.FindDatabasePath(
                new[] { "keepassity.exe", "--data-dir", @"C:\keepassity-data" },
                _ => true);
            if (fromSwitch != databasePath || fromAssociation != databasePath ||
                ignoredSwitch is not null)
            {
                throw new InvalidOperationException("Launch argument parsing is wrong.");
            }
            Record("launch arguments: --db and file-association paths recognized");
            // The single-instance hand-off sends the path over WM_COPYDATA; the
            // receiver validates signature, size and the .kdbx path before the UI
            // acts on it. Keep the pure helpers covered by the smoke test.
            Record("checking single-instance payload");
            byte[] ipcPayload = SingleInstanceRelay.EncodePath(databasePath)
                ?? throw new InvalidOperationException(
                    "The database path could not be encoded for the single-instance hand-off.");
            string? ipcDecoded = SingleInstanceRelay.DecodePath(ipcPayload);
            if (ipcDecoded is null ||
                !SingleInstanceRelay.TryNormalizeDatabasePath(ipcDecoded,
                    out string normalizedIpcPath) ||
                normalizedIpcPath != Path.GetFullPath(databasePath))
            {
                throw new InvalidOperationException(
                    "Single-instance payload encoding round trip failed.");
            }
            if (SingleInstanceRelay.DecodePath(
                    new byte[SingleInstanceRelay.MaxPayloadBytes + 2]) is not null ||
                SingleInstanceRelay.DecodePath(new byte[] { 0x41 }) is not null)
            {
                throw new InvalidOperationException(
                    "Malformed single-instance payloads were accepted.");
            }
            if (SingleInstanceRelay.TryNormalizeDatabasePath(executable, out _) ||
                SingleInstanceRelay.TryNormalizeDatabasePath(
                    new string('a', SingleInstanceRelay.MaxPathCharacters + 1), out _))
            {
                throw new InvalidOperationException(
                    "Invalid single-instance paths were accepted.");
            }
            Record("single-instance: payload encode/decode and path validation");
            Record("copying fixture");
            File.Copy(databasePath, temp, true);
            using (var core = new CoreClient())
            {
                Record("opening copy");
                OpenAsync(core, temp, password).GetAwaiter().GetResult();
                Record("listing");
                List<GroupDto> groups = core.ListGroupsAsync().GetAwaiter().GetResult();
                List<EntrySummaryDto> entries = core.ListEntriesAsync().GetAwaiter().GetResult();
                if (groups.Count == 0 || entries.Count == 0)
                {
                    throw new InvalidOperationException("Database lists are empty.");
                }
                Record($"opened: {groups.Count} group(s), {entries.Count} entr(ies)");

                EntrySummaryDto? totpEntry = entries.FirstOrDefault(entry => entry.HasTotp);
                if (totpEntry is not null)
                {
                    Record("reading TOTP");
                    TotpCodeDto? totp = core.TotpCodeAsync(totpEntry.Uuid)
                        .GetAwaiter().GetResult();
                    if (totp is null || totp.Code.Length is not (6 or 8))
                    {
                        throw new InvalidOperationException("TOTP code was not generated.");
                    }
                    Record($"totp: {totp.Code.Length} digits, period {totp.Period}s");
                    // The UI pre-fetches the next window before the boundary; the FFI
                    // accepts an explicit at_unix second for that code.
                    long period = Math.Max(1, totp.Period);
                    long nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    long nextWindow = ((nowUnix / period) + 1) * period;
                    TotpCodeDto? prefetched = core.TotpCodeAsync(totpEntry.Uuid, nextWindow)
                        .GetAwaiter().GetResult();
                    if (prefetched is null ||
                        prefetched.Code.Length != totp.Code.Length ||
                        prefetched.Remaining != prefetched.Period)
                    {
                        throw new InvalidOperationException("TOTP next-window prefetch failed.");
                    }
                    Record($"totp prefetch: next window, {prefetched.Remaining}s remaining");
                }

                Record("generating passwords");
                PasswordGenerateDto digitsOnly = core.GeneratePasswordAsync(
                    length: 32, excludeAmbiguous: false, lowercase: false,
                    uppercase: false, digits: true, symbols: false)
                    .GetAwaiter().GetResult();
                if (digitsOnly.Password.Length != 32 ||
                    digitsOnly.Password.Any(character => character is < '0' or > '9') ||
                    digitsOnly.EntropyBits <= 0)
                {
                    throw new InvalidOperationException(
                        "Password generator character classes were not applied.");
                }
                PasswordGenerateDto generated = core.GeneratePasswordAsync()
                    .GetAwaiter().GetResult();
                if (generated.Password.Length != 20 || generated.EntropyBits <= 0)
                {
                    throw new InvalidOperationException(
                        "Password generator defaults are wrong.");
                }
                try
                {
                    core.GeneratePasswordAsync(8, true, false, false, false, false)
                        .GetAwaiter().GetResult();
                    throw new InvalidOperationException(
                        "An empty character class set was accepted.");
                }
                catch (CoreException ex) when (ex.Code == "InvalidArgument")
                {
                    Record("password: empty class set rejected by core");
                }
                Record($"password: 32 digits {digitsOnly.EntropyBits:F1} bits, " +
                       $"default 20 chars {generated.EntropyBits:F1} bits");

                Record("storing credential");
                var credentials = new CredentialStore();
                if (credentials.Available)
                {
                    const string credentialUrl = "https://selftest.invalid/db.kdbx";
                    const string credentialUser = "selftest";
                    credentials.SavePassword(credentialUrl, credentialUser, "selftest-value");
                    if (!credentials.HasPassword(credentialUrl, credentialUser) ||
                        credentials.LoadPassword(credentialUrl, credentialUser) !=
                        "selftest-value")
                    {
                        throw new InvalidOperationException("Credential round trip failed.");
                    }
                    if (!credentials.DeletePassword(credentialUrl, credentialUser) ||
                        credentials.HasPassword(credentialUrl, credentialUser) ||
                        credentials.DeletePassword(credentialUrl, credentialUser))
                    {
                        throw new InvalidOperationException("Credential delete failed.");
                    }
                    Record("credential: store/load/delete round trip");
                }

                Record("saving copy");
                core.SaveAsync(temp, 20).GetAwaiter().GetResult();
                if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
                {
                    throw new InvalidOperationException("Save produced no file.");
                }
                Record($"saved: {new FileInfo(temp).Length} bytes");
            }

            using (var verify = new CoreClient())
            {
                Record("reopening copy");
                OpenAsync(verify, temp, password).GetAwaiter().GetResult();
                List<EntrySummaryDto> reopened = verify.ListEntriesAsync().GetAwaiter().GetResult();
                if (reopened.Count == 0)
                {
                    throw new InvalidOperationException("Reopened database has no entries.");
                }
                Record($"reopened: {reopened.Count} entr(ies)");
            }

            Record("PASS");
            code = 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("selftest failed", ex);
            Record("FAIL: " + ex.Message);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // Temp cleanup is best-effort.
            }
        }
        return code;
    }

    private static async Task OpenAsync(CoreClient core, string path, string password)
        => await core.OpenAsync(path, password);
}
