// SPDX-License-Identifier: GPL-3.0-or-later
using Keepassity.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Keepassity.Mock;

/// <summary>
/// The thin WinUI adapter for one list row. <see cref="EntryRowViewModel"/> owns
/// the display logic and stays testable without WinUI; this type only turns the
/// view model's color string and TOTP flag into XAML types.
/// </summary>
public sealed class EntryRowPresenter : EntryRowViewModel
{
    public EntryRowPresenter(EntryRowInput input) : base(input)
    {
        AvatarBrush = new SolidColorBrush(ParseColor(AvatarColor));
    }

    public Brush AvatarBrush { get; }

    public Visibility TotpVisibility => HasTotp ? Visibility.Visible : Visibility.Collapsed;

    private static Windows.UI.Color ParseColor(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#' &&
            byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber,
                null, out byte red) &&
            byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber,
                null, out byte green) &&
            byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber,
                null, out byte blue))
        {
            return Windows.UI.Color.FromArgb(255, red, green, blue);
        }
        return Microsoft.UI.Colors.Transparent;
    }
}
