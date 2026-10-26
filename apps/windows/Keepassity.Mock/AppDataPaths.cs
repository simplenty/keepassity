// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// Local data root. Defaults to %LOCALAPPDATA%\keepassity; KEEPASSITY_DATA_DIR
/// allows a portable/test data directory without touching the real profile.
/// </summary>
public static class AppDataPaths
{
    public static string Root
    {
        get
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "--data-dir");
            if (index >= 0 && index + 1 < args.Length &&
                !string.IsNullOrWhiteSpace(args[index + 1]))
            {
                return Path.GetFullPath(args[index + 1]);
            }
            string? custom = Environment.GetEnvironmentVariable("KEEPASSITY_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(custom))
            {
                return Path.GetFullPath(custom);
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "keepassity");
        }
    }
}
