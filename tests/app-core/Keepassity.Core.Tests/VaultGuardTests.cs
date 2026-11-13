// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Cross-process lock and external-change detection: a second process must not
/// be able to open the same vault, and a save must notice that another program
/// replaced the file before it overwrites it.
/// </summary>
internal static class VaultGuardTests
{
    public static void Run(TestCheck check, string rootDirectory)
    {
        string directory = Path.Combine(rootDirectory, "vault-guard");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "vault.kdbx");
        File.WriteAllBytes(path, "first-version"u8.ToArray());

        VaultFileLock? first = VaultFileLock.TryAcquire(path, out string? firstError);
        check("vault lock acquired", first is not null, firstError ?? string.Empty);
        if (first is null)
        {
            return;
        }
        VaultFileLock? second = VaultFileLock.TryAcquire(path, out string? secondError);
        check("second vault lock is refused", second is null,
            secondError ?? string.Empty);
        second?.Dispose();
        first.Dispose();

        using (VaultFileLock? third = VaultFileLock.TryAcquire(path, out string? thirdError))
        {
            check("vault lock is reusable after release", third is not null,
                thirdError ?? string.Empty);
        }

        VaultFingerprint original = VaultFingerprint.Capture(path);
        check("fingerprint matches unchanged bytes",
            original.Matches(VaultFingerprint.Capture(path)));
        File.WriteAllBytes(path, "second-version"u8.ToArray());
        VaultFingerprint changed = VaultFingerprint.Capture(path);
        check("fingerprint detects changed bytes", !original.Matches(changed));

        // A metadata-only touch must not count as a content change.
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-1));
        check("fingerprint ignores metadata-only touches",
            changed.Matches(VaultFingerprint.Capture(path)));
    }
}
