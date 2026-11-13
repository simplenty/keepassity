// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Keepassity.ViewModels;

/// <summary>A TOTP code plus the window information the display needs.</summary>
public readonly record struct TotpCodeSnapshot(string Code, int Remaining, int Period, int Digits);

/// <summary>The core call the TOTP display needs, injectable for tests.</summary>
public interface ITotpCodeSource
{
    Task<TotpCodeSnapshot?> GetAsync(string uuid, long? atUnix);
}

/// <summary>
/// The TOTP display state machine: which code is shown, the window countdown,
/// the near-boundary pre-fetch and the generation guard for entry switches. The
/// shell owns the timer and passes the current time; no WinUI type appears here.
/// </summary>
public sealed class TotpDisplayViewModel : ObservableObject
{
    private readonly ITotpCodeSource _source;
    private readonly Func<long> _nowUnix;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _entryUuid;
    private int _generation;
    private TotpCodeSnapshot? _displayed;
    private string? _displayedUuid;
    private long _windowEndUnix = long.MaxValue;
    private TotpCodeSnapshot? _prefetched;
    private string? _prefetchedUuid;
    private long _prefetchTargetUnix = long.MinValue;
    private bool _prefetchInFlight;
    private bool _hasCode;
    private string _codeText = string.Empty;
    private int _remainingSeconds;
    private int _period;
    private int _progressValue;

    public TotpDisplayViewModel(ITotpCodeSource source, Func<long> nowUnix)
    {
        _source = source;
        _nowUnix = nowUnix;
    }

    public bool HasCode => _hasCode;
    public string CodeText => _codeText;
    public int RemainingSeconds => _remainingSeconds;
    public int Period => _period;
    public int ProgressValue => _progressValue;

    /// <summary>The code currently displayed, for the copy command.</summary>
    public string? CurrentCode => _displayed?.Code;

    /// <summary>End of the current TOTP window, used to schedule the next tick.</summary>
    public long WindowEndUnix => _windowEndUnix;

    /// <summary>Switch entries; an in-flight request for the old entry is ignored.</summary>
    public void SelectEntry(string? uuid)
    {
        if (string.Equals(_entryUuid, uuid, StringComparison.Ordinal))
        {
            return;
        }
        _entryUuid = uuid;
        _generation++;
        ResetState();
    }

    public void Reset()
    {
        _entryUuid = null;
        _generation++;
        ResetState();
    }

    /// <summary>
    /// Advance the display. The shell calls this once per timer tick and once
    /// after selecting an entry; overlapping calls are serialized.
    /// </summary>
    public async Task TickAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            string? uuid = _entryUuid;
            if (uuid is null)
            {
                ResetState();
                return;
            }
            long now = _nowUnix();
            if (TotpTiming.ShouldRequestCode(now, _displayed is not null, _displayedUuid,
                    uuid, _windowEndUnix))
            {
                TotpCodeSnapshot? prefetched = _prefetched;
                if (prefetched is { } candidate && _prefetchedUuid == uuid &&
                    TotpTiming.IsPrefetchUsable(_prefetchTargetUnix, candidate.Period, now))
                {
                    Activate(candidate,
                        _prefetchTargetUnix + Math.Max(1, candidate.Period), uuid);
                    ClearPrefetch();
                }
                else
                {
                    ClearPrefetch();
                    int generation = _generation;
                    TotpCodeSnapshot? code = await _source.GetAsync(uuid, null)
                        .ConfigureAwait(false);
                    if (generation != _generation || _entryUuid != uuid)
                    {
                        return;
                    }
                    if (code is null)
                    {
                        SetUnavailable();
                        return;
                    }
                    Activate(code.Value, now + Math.Max(1, code.Value.Remaining), uuid);
                }
            }

            now = _nowUnix();
            int period = Math.Max(1, _displayed?.Period ?? 30);
            int remaining = TotpTiming.Remaining(_windowEndUnix, now, period);
            SetCountdown(period, remaining);
            if (TotpTiming.ShouldPrefetch(remaining, _windowEndUnix, _prefetched is not null,
                    _prefetchInFlight))
            {
                _prefetchInFlight = true;
                _ = PrefetchAsync(uuid, _windowEndUnix, period, _generation);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PrefetchAsync(string uuid, long targetUnix, int period, int generation)
    {
        try
        {
            TotpCodeSnapshot? next = await _source.GetAsync(uuid, targetUnix)
                .ConfigureAwait(false);
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (next is null || generation != _generation || _entryUuid != uuid)
                {
                    return;
                }
                long now = _nowUnix();
                if (now < targetUnix)
                {
                    _prefetched = next;
                    _prefetchedUuid = uuid;
                    _prefetchTargetUnix = targetUnix;
                }
                else if (TotpTiming.IsPrefetchUsable(targetUnix, period, now) &&
                         _windowEndUnix <= now)
                {
                    Activate(next.Value, targetUnix + Math.Max(1, period), uuid);
                }
            }
            finally
            {
                _gate.Release();
            }
        }
        catch
        {
            // A prefetch failure is not user-visible; the next tick requests the
            // code normally.
        }
        finally
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (generation == _generation)
                {
                    _prefetchInFlight = false;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private void Activate(TotpCodeSnapshot code, long windowEndUnix, string uuid)
    {
        _displayed = code;
        _displayedUuid = uuid;
        _windowEndUnix = windowEndUnix;
        SetCode(code.Code, code.Digits);
    }

    private void SetCode(string code, int digits)
    {
        _hasCode = true;
        _codeText = TotpTiming.FormatCode(code, digits);
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(CodeText));
        OnPropertyChanged(nameof(CurrentCode));
    }

    private void SetCountdown(int period, int remaining)
    {
        _period = period;
        _remainingSeconds = remaining;
        _progressValue = period - remaining;
        OnPropertyChanged(nameof(Period));
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private void SetUnavailable()
    {
        _windowEndUnix = long.MaxValue;
        _hasCode = false;
        _codeText = string.Empty;
        _remainingSeconds = 0;
        _period = 0;
        _progressValue = 0;
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(CodeText));
        OnPropertyChanged(nameof(CurrentCode));
        OnPropertyChanged(nameof(Period));
        OnPropertyChanged(nameof(RemainingSeconds));
        OnPropertyChanged(nameof(ProgressValue));
    }

    private void ResetState()
    {
        _displayed = null;
        _displayedUuid = null;
        _windowEndUnix = long.MaxValue;
        ClearPrefetch();
        SetUnavailable();
        OnPropertyChanged(nameof(WindowEndUnix));
    }

    private void ClearPrefetch()
    {
        _prefetched = null;
        _prefetchedUuid = null;
        _prefetchTargetUnix = long.MinValue;
        _prefetchInFlight = false;
    }
}
