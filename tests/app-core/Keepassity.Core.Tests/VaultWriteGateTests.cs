// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.Core;

namespace Keepassity.CoreTests;

/// <summary>
/// Headless checks for the in-process vault write gate: overlapping async
/// writes must never run at the same time, and release must let the next waiter
/// proceed.
/// </summary>
internal static class VaultWriteGateTests
{
    public static void Run(TestCheck check)
    {
        using var gate = new VaultWriteGate();
        int active = 0;
        int maxActive = 0;
        var order = new List<int>();
        Task Write(int id) => Task.Run(async () =>
        {
            using (await gate.EnterAsync())
            {
                int current = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, current);
                order.Add(id);
                await Task.Delay(25);
                Interlocked.Decrement(ref active);
            }
        });

        Task[] tasks = { Write(1), Write(2), Write(3) };
        Task.WaitAll(tasks);
        check("vault write gate serializes writers", maxActive == 1, maxActive.ToString());
        check("vault write gate runs every writer once",
            order.Count == 3 && order.Distinct().Count() == 3,
            string.Join(",", order));

        bool released = false;
        using (gate.EnterAsync().GetAwaiter().GetResult())
        {
            released = true;
        }
        using (gate.EnterAsync().GetAwaiter().GetResult())
        {
            check("vault write gate releases after disposal", released);
        }
    }
}
