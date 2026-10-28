// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Mock;

/// <summary>
/// Local working copies for databases opened from WebDAV. The KDBX bytes stay
/// encrypted at rest; the folder only keeps a cached database per remote source.
/// </summary>
public static class RemoteDatabaseStore
{
    public static string Root => Path.Combine(AppDataPaths.Root, "remote");

    /// <summary>Working-copy path for a remote file URL + username pair.</summary>
    public static string PathFor(string url, string username)
    {
        string key = $"{url.Trim()}\n{username.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        string name = Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".kdbx";
        Directory.CreateDirectory(Root);
        return Path.Combine(Root, name);
    }
}
