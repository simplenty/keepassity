// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;

namespace Keepassity.Core;

/// <summary>
/// Content identity of a database file at a point in time. Before a save the
/// shell compares this with the file on disk; if another program (or user)
/// changed it, the save is refused instead of silently overwriting their work.
/// SHA-256 is authoritative; length and mtime are diagnostics only.
/// </summary>
public readonly record struct VaultFingerprint(long Length, DateTime LastWriteTimeUtc,
    string Sha256)
{
    public static VaultFingerprint Capture(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return new VaultFingerprint(
            bytes.LongLength,
            File.GetLastWriteTimeUtc(path),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    /// <summary>True when both snapshots describe the same bytes.</summary>
    public bool Matches(VaultFingerprint other) =>
        string.Equals(Sha256, other.Sha256, StringComparison.Ordinal);
}
