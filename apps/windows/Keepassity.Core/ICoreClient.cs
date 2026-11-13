// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// The application-facing view of the native core. View models depend on this
/// port, never on the P/Invoke implementation, so their behaviour can be tested
/// without the native library or a window.
/// </summary>
public interface ICoreClient : IDisposable
{
    /// <summary>Whether a database handle is currently held (also true when locked).</summary>
    bool IsOpen { get; }

    /// <summary>Path of the database the handle was opened from, when known.</summary>
    string? DatabasePath { get; }

    /// <summary>
    /// Open and decrypt a database. Throws <see cref="CoreException"/> for a
    /// wrong password, a damaged file or an unsupported format.
    /// </summary>
    Task OpenAsync(string path, string password);

    /// <summary>Lock the vault and drop the decrypted state; the handle stays usable.</summary>
    Task LockAsync();
}
