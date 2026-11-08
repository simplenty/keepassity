// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// Opt-in gate for the diagnostic overrides that exist for automated UI checks:
/// <c>--allow-multiple</c>, <c>--no-tray</c>, <c>KEEPASSITY_MOCK_PASSWORD</c>,
/// <c>KEEPASSITY_SETTINGS_DEMO_MS</c> and the tray-menu geometry hooks.
///
/// They are useful when testing a published build, but a normal user (or a
/// malicious environment variable) must not be able to skip the single-instance
/// guard, hide the tray icon or inject a master password into a release build.
/// They therefore require <b>both</b> a debug build or the explicit
/// <c>--enable-test-hooks</c> switch, and the hook itself.
/// <c>--selftest</c> is not affected: it is the CI smoke test and always works.
/// </summary>
internal static class TestHooks
{
    private const string EnableSwitch = "--enable-test-hooks";

#if DEBUG
    private static readonly bool DebugBuild = true;
#else
    private static readonly bool DebugBuild = false;
#endif

    private static readonly Lazy<bool> _enabled = new(() =>
    {
        if (DebugBuild)
        {
            return true;
        }
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.Equals(EnableSwitch, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    });

    /// <summary>True when diagnostic overrides are allowed in this process.</summary>
    public static bool Enabled => _enabled.Value;

    /// <summary>
    /// Reads a diagnostic switch: true when the argument is present and hooks are
    /// enabled. Release builds without the opt-in switch always return false.
    /// </summary>
    public static bool HasSwitch(string name)
    {
        if (!Enabled)
        {
            return false;
        }
        foreach (string argument in Environment.GetCommandLineArgs())
        {
            if (argument.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads a diagnostic environment variable; null when hooks are not enabled.
    /// </summary>
    public static string? EnvironmentValue(string name)
    {
        return Enabled ? Environment.GetEnvironmentVariable(name) : null;
    }
}
