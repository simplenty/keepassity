// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>
/// Pure TOTP display timing: countdown, code formatting, the next timer tick
/// and the window-boundary decisions. Keeping this out of the shell makes the
/// off-by-one boundary behavior testable.
/// </summary>
public static class TotpTiming
{
    /// <summary>Seconds left in the current window, clamped to [0, period].</summary>
    public static int Remaining(long windowEndUnix, long nowUnix, int period)
    {
        int safePeriod = Math.Max(1, period);
        long remaining = windowEndUnix - nowUnix;
        return (int)Math.Clamp(remaining, 0, safePeriod);
    }

    /// <summary>Group digits for readability: 123 456 or 1234 5678.</summary>
    public static string FormatCode(string code, int digits)
    {
        if (digits == 8 && code.Length == 8)
        {
            return $"{code[..4]} {code[4..]}";
        }
        if (code.Length == 6)
        {
            return $"{code[..3]} {code[3..]}";
        }
        return code;
    }

    /// <summary>
    /// True when the displayed code is missing, belongs to another entry, or its
    /// window has ended and a fresh code must be requested.
    /// </summary>
    public static bool ShouldRequestCode(long nowUnix, bool hasDisplayedCode,
        string? displayedUuid, string currentUuid, long windowEndUnix) =>
        !hasDisplayedCode || displayedUuid != currentUuid || nowUnix >= windowEndUnix;

    /// <summary>
    /// Pre-fetch the next window when fewer than three seconds remain and no
    /// prefetch is already available or in flight.
    /// </summary>
    public static bool ShouldPrefetch(int remaining, long windowEndUnix,
        bool hasPrefetched, bool prefetchInFlight) =>
        remaining < 3 && windowEndUnix != long.MaxValue && !hasPrefetched &&
        !prefetchInFlight;

    /// <summary>
    /// Delay until the next timer tick. Near a window boundary the tick lands
    /// just after it (15 ms) so the code never displays one tick too long.
    /// </summary>
    public static TimeSpan NextTick(TimeSpan untilBoundary)
    {
        return untilBoundary > TimeSpan.Zero && untilBoundary < TimeSpan.FromSeconds(1)
            ? untilBoundary + TimeSpan.FromMilliseconds(15)
            : TimeSpan.FromSeconds(1);
    }

    /// <summary>
    /// A prefetched code for <paramref name="targetUnix"/> is usable once the
    /// clock reaches its window and before that window ends.
    /// </summary>
    public static bool IsPrefetchUsable(long targetUnix, int period, long nowUnix) =>
        nowUnix >= targetUnix && nowUnix < targetUnix + Math.Max(1, period);
}
