// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>A local backup plus the bytes that were read from it.</summary>
public sealed record RestorableBackup(BackupEntry Entry, byte[] Bytes);

/// <summary>
/// Finds a local backup that can actually replace a database that failed to
/// open. A damaged file still has its pre-save backups next to it, so recovery
/// must not depend on opening the damaged database first (the normal backup list
/// is only reachable once a vault is open).
/// </summary>
public static class VaultRecovery
{
    /// <summary>
    /// Newest backup whose bytes decrypt with the supplied check. Unreadable or
    /// corrupt candidates are skipped; returns null when no backup works.
    /// </summary>
    public static async Task<RestorableBackup?> FindRestorableBackupAsync(
        string databasePath, Func<byte[], Task<bool>> decrypts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decrypts);
        foreach (BackupEntry entry in BackupService.ListBackups(databasePath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(entry.Path, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A backup removed or locked between listing and reading is not a
                // recovery candidate; the next older one may still work.
                continue;
            }
            bool usable;
            try
            {
                usable = await decrypts(bytes).ConfigureAwait(false);
            }
            catch (CoreException)
            {
                // The candidate does not decrypt with the current password.
                usable = false;
            }
            if (usable)
            {
                return new RestorableBackup(entry, bytes);
            }
        }
        return null;
    }
}
