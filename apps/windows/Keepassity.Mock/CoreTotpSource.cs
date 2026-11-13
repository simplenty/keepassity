// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;

namespace Keepassity.Mock;

/// <summary>Thin adapter from the FFI DTO to the view model's TOTP snapshot.</summary>
internal sealed class CoreTotpSource : ITotpCodeSource
{
    private readonly CoreClient _core;

    public CoreTotpSource(CoreClient core) => _core = core;

    public async Task<TotpCodeSnapshot?> GetAsync(string uuid, long? atUnix)
    {
        TotpCodeDto? code = await _core.TotpCodeAsync(uuid, atUnix).ConfigureAwait(false);
        return code is null
            ? null
            : new TotpCodeSnapshot(code.Code, code.Remaining, code.Period, code.Digits);
    }
}
