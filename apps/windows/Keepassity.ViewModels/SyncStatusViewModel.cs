// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;

namespace Keepassity.ViewModels;

/// <summary>
/// Localized sync status shown in the navigation pane. The shell stores the
/// source string and arguments (some are numeric), and the view model renders
/// them through <see cref="IAppText"/> so a language change can refresh the text
/// without rebuilding the navigation item.
/// </summary>
public sealed partial class SyncStatusViewModel : ObservableObject
{
    private readonly IAppText _appText;
    private string _source = "Local only";
    private object?[] _arguments = Array.Empty<object?>();

    [ObservableProperty]
    private string _text = string.Empty;

    public SyncStatusViewModel(IAppText appText)
    {
        _appText = appText ?? throw new ArgumentNullException(nameof(appText));
        Refresh();
    }

    /// <summary>The untranslated source string (diagnostics and tests).</summary>
    public string Source => _source;

    /// <summary>Set the status source and its format arguments.</summary>
    public void Set(string source, params object?[] arguments)
    {
        _source = source;
        _arguments = arguments;
        Refresh();
    }

    /// <summary>Re-render the current status, for example after a language change.</summary>
    public void Refresh()
    {
        Text = _arguments.Length == 0
            ? _appText.T(_source)
            : _appText.T(_source, _arguments);
    }
}
