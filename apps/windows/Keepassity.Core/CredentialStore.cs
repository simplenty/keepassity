// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Keepassity.Mock;

/// <summary>Stores WebDAV passwords encrypted with DPAPI for the current user.</summary>
public sealed class CredentialStore
{
    private readonly string _directory;

    public CredentialStore()
    {
        _directory = Path.Combine(AppDataPaths.Root, "credentials");
        try
        {
            Directory.CreateDirectory(_directory);
            Available = true;
        }
        catch
        {
            Available = false;
        }
    }

    public bool Available { get; }

    public void SavePassword(string url, string username, string password)
    {
        if (!Available)
        {
            throw new InvalidOperationException("Credential storage is not available.");
        }
        byte[] plain = Encoding.UTF8.GetBytes(password);
        byte[] encrypted = Protect(plain);
        File.WriteAllBytes(PathFor(url, username), encrypted);
    }

    public string? LoadPassword(string url, string username)
    {
        if (!Available)
        {
            return null;
        }
        string path = PathFor(url, username);
        if (!File.Exists(path))
        {
            return null;
        }
        byte[] encrypted = File.ReadAllBytes(path);
        byte[] plain = Unprotect(encrypted);
        return Encoding.UTF8.GetString(plain);
    }

    private string PathFor(string url, string username)
    {
        string key = $"{url.Trim()}\n{username.Trim()}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Path.Combine(_directory, Convert.ToHexString(hash)[..32].ToLowerInvariant() + ".bin");
    }

    private static byte[] Protect(byte[] data)
    {
        var input = new DataBlob { cbData = data.Length };
        input.pbData = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0x1,
                    out DataBlob output))
            {
                throw new InvalidOperationException("DPAPI could not protect the credential.");
            }
            try
            {
                return CopyBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    private static byte[] Unprotect(byte[] data)
    {
        var input = new DataBlob { cbData = data.Length };
        input.pbData = Marshal.AllocHGlobal(data.Length);
        try
        {
            Marshal.Copy(data, 0, input.pbData, data.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, 0x1, out DataBlob output))
            {
                throw new InvalidOperationException("DPAPI could not unprotect the credential.");
            }
            try
            {
                return CopyBlob(output);
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.pbData);
        }
    }

    private static byte[] CopyBlob(DataBlob blob)
    {
        var result = new byte[blob.cbData];
        Marshal.Copy(blob.pbData, result, 0, blob.cbData);
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
