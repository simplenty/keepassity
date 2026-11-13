// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the TOTP display state machine: code activation,
/// countdown, window rollover, unavailable state, entry switch and prefetch.
/// </summary>
internal static class TotpDisplayViewModelTests
{
    private sealed class FakeSource : ITotpCodeSource
    {
        public Func<string, long?, TotpCodeSnapshot?> Handler { get; set; } =
            (_, _) => new TotpCodeSnapshot("123456", 30, 30, 6);
        public List<(string Uuid, long? At)> Calls { get; } = new();

        public Task<TotpCodeSnapshot?> GetAsync(string uuid, long? atUnix)
        {
            Calls.Add((uuid, atUnix));
            return Task.FromResult(Handler(uuid, atUnix));
        }
    }

    public static void Run(TestCheck check)
    {
        RunBasicWindow(check);
        RunPrefetch(check);
        RunUnavailableAndReset(check);
    }

    private static void RunBasicWindow(TestCheck check)
    {
        long now = 1_000;
        var source = new FakeSource();
        var totp = new TotpDisplayViewModel(source, () => now);
        totp.SelectEntry("entry-a");
        totp.TickAsync().GetAwaiter().GetResult();
        check("totp display: code activated", totp.HasCode && totp.CodeText == "123 456" &&
            totp.CurrentCode == "123456", totp.CodeText);
        check("totp display: initial countdown", totp.RemainingSeconds == 30 &&
            totp.Period == 30 && totp.ProgressValue == 0,
            $"{totp.RemainingSeconds}/{totp.ProgressValue}");
        check("totp display: one source request", source.Calls.Count == 1,
            source.Calls.Count.ToString());

        now += 10;
        totp.TickAsync().GetAwaiter().GetResult();
        check("totp display: countdown advances", totp.RemainingSeconds == 20 &&
            totp.ProgressValue == 10, totp.RemainingSeconds.ToString());
        check("totp display: displayed code is not re-requested",
            source.Calls.Count == 1, source.Calls.Count.ToString());

        now += 20;
        totp.TickAsync().GetAwaiter().GetResult();
        check("totp display: window rollover requests a new code",
            source.Calls.Count >= 2, source.Calls.Count.ToString());
    }

    private static void RunPrefetch(TestCheck check)
    {
        long now = 2_000;
        var source = new FakeSource
        {
            Handler = (_, at) => at is null
                ? new TotpCodeSnapshot("111111", 5, 5, 6)
                : new TotpCodeSnapshot("222222", 5, 5, 6),
        };
        var totp = new TotpDisplayViewModel(source, () => now);
        totp.SelectEntry("entry-prefetch");
        totp.TickAsync().GetAwaiter().GetResult();
        now += 3; // two seconds remain, prefetch threshold
        totp.TickAsync().GetAwaiter().GetResult();
        // Let the fire-and-forget prefetch finish.
        Task.Delay(50).GetAwaiter().GetResult();
        int callsBeforeBoundary = source.Calls.Count;
        now += 2;
        totp.TickAsync().GetAwaiter().GetResult();
        check("totp display: prefetched code is adopted at the boundary",
            totp.CodeText == "222 222", totp.CodeText);
        check("totp display: boundary did not need another request",
            source.Calls.Count == callsBeforeBoundary, source.Calls.Count.ToString());
    }

    private static void RunUnavailableAndReset(TestCheck check)
    {
        long now = 3_000;
        var source = new FakeSource { Handler = (_, _) => null };
        var totp = new TotpDisplayViewModel(source, () => now);
        totp.SelectEntry("entry-none");
        totp.TickAsync().GetAwaiter().GetResult();
        check("totp display: missing code is unavailable",
            !totp.HasCode && totp.CodeText.Length == 0);
        totp.Reset();
        check("totp display: reset clears state", !totp.HasCode &&
            totp.RemainingSeconds == 0 && totp.CurrentCode is null);

        source.Handler = (_, _) => new TotpCodeSnapshot("654321", 30, 30, 6);
        totp.SelectEntry("entry-a");
        totp.TickAsync().GetAwaiter().GetResult();
        totp.SelectEntry("entry-b");
        check("totp display: entry switch hides the old code", !totp.HasCode);
    }
}
