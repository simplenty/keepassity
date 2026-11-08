// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// Resolves the database path passed on the command line. Besides the explicit
/// <c>--db &lt;path&gt;</c> switch, the shell passes a bare path when a .kdbx file
/// is opened through a file association ("Open with keepassity"). Switches and
/// the executable path must not be mistaken for a database.
/// </summary>
internal static class LaunchArguments
{
    public static string? FindDatabasePath(IReadOnlyList<string> arguments,
        Func<string, bool> fileExists)
    {
        for (int index = 0; index + 1 < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], "--db", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            string candidate = arguments[index + 1];
            if (!string.IsNullOrWhiteSpace(candidate) && fileExists(candidate))
            {
                return candidate;
            }
        }
        // File association hand-off: "<exe>" "<path>.kdbx [other switches]".
        for (int index = 1; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.IsNullOrWhiteSpace(argument) ||
                argument.StartsWith("-", StringComparison.Ordinal) ||
                !argument.EndsWith(".kdbx", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (fileExists(argument))
            {
                return argument;
            }
        }
        return null;
    }
}
