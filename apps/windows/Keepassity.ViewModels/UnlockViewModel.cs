// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using Keepassity.Core;

namespace Keepassity.ViewModels;

/// <summary>Visual severity of the unlock status banner.</summary>
public enum UnlockStatusSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// State and behaviour of the unlock surface. The view owns the password box and
/// passes its current text to <see cref="UnlockAsync"/>; the view model owns
/// validation, busy state, the status banner and the error classification, so
/// the whole unlock decision path is testable without WinUI.
/// </summary>
public sealed partial class UnlockViewModel : ObservableObject
{
    private readonly ICoreClient _core;
    private readonly IAppText _text;
    private string? _databasePath;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isStatusOpen;

    [ObservableProperty]
    private string _statusTitle = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private UnlockStatusSeverity _statusSeverity;

    /// <summary>Whether the recovery actions (diagnostics / backups) should show.</summary>
    [ObservableProperty]
    private bool _recoveryVisible;

    public UnlockViewModel(ICoreClient core, IAppText text)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _text = text ?? throw new ArgumentNullException(nameof(text));
    }

    /// <summary>Absolute path of the database selected on the unlock surface.</summary>
    public string? DatabasePath
    {
        get => _databasePath;
        private set
        {
            if (SetProperty(ref _databasePath, value))
            {
                OnPropertyChanged(nameof(HasDatabase));
                OnPropertyChanged(nameof(CanUnlock));
            }
        }
    }

    public bool HasDatabase => !string.IsNullOrWhiteSpace(_databasePath);

    /// <summary>Whether the unlock action may run right now.</summary>
    public bool CanUnlock => HasDatabase && !IsBusy;

    /// <summary>Whether the password box and source buttons accept input.</summary>
    public bool IsInputEnabled => !IsBusy;

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUnlock));
        OnPropertyChanged(nameof(IsInputEnabled));
    }

    /// <summary>
    /// Select a database path without unlocking. A different path clears any
    /// status left over from the previous attempt, so a stale error can never be
    /// mistaken for the new selection.
    /// </summary>
    public void SetDatabasePath(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : path;
        if (string.Equals(_databasePath, normalized, StringComparison.Ordinal))
        {
            return;
        }
        DatabasePath = normalized;
        ClearStatus();
    }

    /// <summary>Whether a database-open operation (not necessarily an unlock) is running.</summary>
    public void SetBusy(bool busy) => IsBusy = busy;

    /// <summary>Show the recovery actions below the status banner.</summary>
    public void ShowRecovery() => RecoveryVisible = true;

    /// <summary>Hide the recovery actions.</summary>
    public void HideRecovery() => RecoveryVisible = false;

    /// <summary>
    /// Publish a status banner. The unlock surface is also used by the local-file,
    /// WebDAV and new-database flows, so they all route their status through this
    /// single state instead of writing the InfoBar directly.
    /// </summary>
    public void ShowStatus(string title, string message,
        UnlockStatusSeverity severity, bool recovery = false)
    {
        StatusSeverity = severity;
        StatusTitle = title;
        StatusMessage = message;
        RecoveryVisible = recovery;
        IsStatusOpen = true;
    }

    /// <summary>
    /// Validate the input and open the selected database. Returns true only when
    /// the native handle is successfully unlocked; failures leave a localized
    /// status banner and never throw (except caller cancellation).
    /// </summary>
    public async Task<bool> UnlockAsync(string password, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return false;
        }
        if (!HasDatabase)
        {
            ShowError(_text.T("Cannot open database"),
                _text.T("Choose a .kdbx database first."), recovery: false);
            return false;
        }
        if (string.IsNullOrEmpty(password))
        {
            ShowError(_text.T("Cannot open database"),
                _text.T("Enter the master password."), recovery: false);
            return false;
        }

        IsBusy = true;
        ClearStatus();
        try
        {
            await _core.OpenAsync(_databasePath!, password).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (CoreException ex)
        {
            ShowError(TitleFor(ex.Code), ex.Message, recovery: true);
            return false;
        }
        catch (Exception ex)
        {
            ShowError(_text.T("Cannot open database"), ex.Message, recovery: true);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private string TitleFor(string code) => code switch
    {
        "BadPassword" => _text.T("Wrong master password"),
        "CorruptDatabase" => _text.T("Database is damaged"),
        "UnsupportedFormat" => _text.T("Unsupported database"),
        _ => _text.T("Cannot open database"),
    };

    private void ShowError(string title, string message, bool recovery)
    {
        ShowStatus(title, message, UnlockStatusSeverity.Error, recovery);
    }

    /// <summary>Dismiss the status banner and its recovery actions.</summary>
    public void ClearStatus()
    {
        IsStatusOpen = false;
        StatusTitle = string.Empty;
        StatusMessage = string.Empty;
        RecoveryVisible = false;
    }
}
