// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the TOTP countdown boundary: clamping, formatting, the
/// request/prefetch decisions and the timer interval near a window end.
/// </summary>
internal static class TotpTimingTests
{
    public static void Run(TestCheck check)
    {
        check("totp: remaining in window", TotpTiming.Remaining(100, 70, 30) == 30);
        check("totp: remaining clamps at zero", TotpTiming.Remaining(100, 130, 30) == 0);
        check("totp: remaining clamps at period", TotpTiming.Remaining(100, 0, 30) == 30);
        check("totp: period is at least one", TotpTiming.Remaining(100, 99, 0) == 1);

        check("totp: format six digits", TotpTiming.FormatCode("123456", 6) == "123 456");
        check("totp: format eight digits", TotpTiming.FormatCode("12345678", 8) == "1234 5678");
        check("totp: format unknown length", TotpTiming.FormatCode("12345", 6) == "12345");

        check("totp: requests when no code", TotpTiming.ShouldRequestCode(
            50, false, null, "uuid-a", long.MaxValue));
        check("totp: requests on entry change", TotpTiming.ShouldRequestCode(
            50, true, "uuid-a", "uuid-b", 100));
        check("totp: requests after window end", TotpTiming.ShouldRequestCode(
            100, true, "uuid-a", "uuid-a", 100));
        check("totp: keeps code inside window", !TotpTiming.ShouldRequestCode(
            99, true, "uuid-a", "uuid-a", 100));

        check("totp: prefetch under three seconds", TotpTiming.ShouldPrefetch(
            2, 100, false, false));
        check("totp: no prefetch at three seconds", !TotpTiming.ShouldPrefetch(
            3, 100, false, false));
        check("totp: no prefetch while one is pending", !TotpTiming.ShouldPrefetch(
            1, 100, false, true));
        check("totp: no prefetch for an unbounded window", !TotpTiming.ShouldPrefetch(
            1, long.MaxValue, false, false));

        check("totp: timer snaps to a near boundary",
            TotpTiming.NextTick(TimeSpan.FromMilliseconds(500)) ==
            TimeSpan.FromMilliseconds(515),
            TotpTiming.NextTick(TimeSpan.FromMilliseconds(500)).ToString());
        check("totp: timer uses one second away from a boundary",
            TotpTiming.NextTick(TimeSpan.FromSeconds(2)) == TimeSpan.FromSeconds(1));
        check("totp: timer uses one second at the boundary",
            TotpTiming.NextTick(TimeSpan.Zero) == TimeSpan.FromSeconds(1));

        check("totp: prefetch usable at window start",
            TotpTiming.IsPrefetchUsable(100, 30, 100));
        check("totp: prefetch unusable before its window",
            !TotpTiming.IsPrefetchUsable(100, 30, 99));
        check("totp: prefetch unusable after its window",
            !TotpTiming.IsPrefetchUsable(100, 30, 130));
    }
}
