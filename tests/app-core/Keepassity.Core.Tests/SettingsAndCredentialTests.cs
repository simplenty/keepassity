// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Keepassity.Mock;

namespace Keepassity.CoreTests;

/// <summary>
/// M4-04: the WebDAV password must never land in the settings file (or any
/// config file under the data root), and the DPAPI credential store must
/// support save/load/has/delete without storing plaintext. Uses the temporary
/// KEEPASSITY_DATA_DIR that Program.cs set before any store was constructed.
/// </summary>
internal static class SettingsAndCredentialTests
{
    private const string WebDavUrl = "https://dav.example.test/dav/密码库/测试.kdbx";
    private const string WebDavUser = "dav-user";
    private const string WebDavPassword = "dav-test-password";

    public static void Run(TestCheck check)
    {
        RunSettingsProof(check);
        RunCredentialLifecycle(check);
    }

    private static void RunSettingsProof(TestCheck check)
    {
        var settings = new AppSettingsStore();
        settings.Current.WebDavUrl = WebDavUrl;
        settings.Current.WebDavUsername = WebDavUser;
        settings.Current.LastWebDavUrl = WebDavUrl;
        settings.Current.LastWebDavUsername = WebDavUser;
        settings.Save();

        string settingsPath = Path.Combine(AppDataPaths.Root, "settings.json");
        check("settings: file written", File.Exists(settingsPath), settingsPath);
        string json = File.ReadAllText(settingsPath);
        check("settings: WebDAV password is not present",
            !json.Contains(WebDavPassword, StringComparison.Ordinal));
        check("settings: no password-valued field is serialized",
            !json.Contains("password", StringComparison.OrdinalIgnoreCase),
            "settings JSON should carry no password property at all");
        check("settings: model exposes no password property",
            !typeof(AppSettings).GetProperties().Any(property =>
                property.Name.Contains("password", StringComparison.OrdinalIgnoreCase)));
        var reloaded = new AppSettingsStore();
        check("settings: WebDAV URL round trips", reloaded.Current.WebDavUrl == WebDavUrl,
            reloaded.Current.WebDavUrl ?? "null");
        check("settings: WebDAV username round trips",
            reloaded.Current.WebDavUsername == WebDavUser,
            reloaded.Current.WebDavUsername ?? "null");
    }

    private static void RunCredentialLifecycle(TestCheck check)
    {
        var credentials = new CredentialStore();
        if (!credentials.Available)
        {
            Console.WriteLine("SKIP  credential store unavailable (DPAPI is Windows-only)");
            return;
        }

        check("credential: absent before first save",
            !credentials.HasPassword(WebDavUrl, WebDavUser));
        check("credential: delete of a missing credential returns false",
            !credentials.DeletePassword(WebDavUrl, WebDavUser));

        credentials.SavePassword(WebDavUrl, WebDavUser, WebDavPassword);
        check("credential: HasPassword is true after save",
            credentials.HasPassword(WebDavUrl, WebDavUser));
        string? loaded = credentials.LoadPassword(WebDavUrl, WebDavUser);
        check("credential: LoadPassword round trips", loaded == WebDavPassword);
        check("credential: keyed by URL",
            !credentials.HasPassword("https://dav.example.test/other.kdbx", WebDavUser));
        check("credential: keyed by username",
            !credentials.HasPassword(WebDavUrl, "other-user"));

        // The on-disk credential blob must be DPAPI-protected, not plaintext.
        string credentialDirectory = Path.Combine(AppDataPaths.Root, "credentials");
        string[] blobs = Directory.Exists(credentialDirectory)
            ? Directory.GetFiles(credentialDirectory, "*.bin")
            : Array.Empty<string>();
        check("credential: protected blob written", blobs.Length >= 1,
            blobs.Length.ToString());
        byte[] plaintext = Encoding.UTF8.GetBytes(WebDavPassword);
        check("credential: stored blob is not plaintext",
            blobs.All(path => !Contains(File.ReadAllBytes(path), plaintext)));

        // Nothing else under the data root may contain the WebDAV password:
        // the settings/config proof is a real grep, not just a field check.
        byte[] passwordBytes = Encoding.UTF8.GetBytes(WebDavPassword);
        string[] configFiles = Directory.GetFiles(AppDataPaths.Root, "*",
                SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(credentialDirectory + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        check("settings: no config file under the data root contains the password",
            configFiles.All(path => !Contains(File.ReadAllBytes(path), passwordBytes)),
            string.Join(", ", configFiles.Select(Path.GetFileName)));

        check("credential: DeletePassword reports a deletion",
            credentials.DeletePassword(WebDavUrl, WebDavUser));
        check("credential: HasPassword is false after delete",
            !credentials.HasPassword(WebDavUrl, WebDavUser));
        check("credential: LoadPassword returns null after delete",
            credentials.LoadPassword(WebDavUrl, WebDavUser) is null);
        check("credential: deleting twice returns false",
            !credentials.DeletePassword(WebDavUrl, WebDavUser));
    }

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }
        for (int start = 0; start <= haystack.Length - needle.Length; start++)
        {
            bool matched = true;
            for (int offset = 0; offset < needle.Length; offset++)
            {
                if (haystack[start + offset] != needle[offset])
                {
                    matched = false;
                    break;
                }
            }
            if (matched)
            {
                return true;
            }
        }
        return false;
    }
}
