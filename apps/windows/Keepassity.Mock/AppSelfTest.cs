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
