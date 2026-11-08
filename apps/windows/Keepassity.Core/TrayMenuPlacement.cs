// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Mock;

/// <summary>
/// Pure placement decisions for the tray menu flyout. Kept out of the WinUI
/// layer so the edge cases (top/bottom taskbar, right screen edge, icon rect
/// anchoring) can be unit tested without a desktop session.
/// </summary>
public static class TrayMenuPlacement
{
    /// <summary>
    /// A right-aligned flyout looks better when the anchor is this close to the
    /// right edge of the work area.
    /// </summary>
    public const int RightAlignmentMargin = 320;

    public enum VerticalSide
    {
        Below,
        Above,
    }

    public readonly record struct Result(VerticalSide Side, bool RightAligned);

    /// <summary>
    /// Picks the side of the anchor with more room and whether the menu should be
    /// right-aligned so it stays fully on screen. Coordinates are physical pixels.
    /// </summary>
    public static Result Choose(int anchorX, int anchorY, int iconWidth, int iconHeight,
        int workTop, int workRight, int workBottom)
    {
        int spaceBelow = workBottom - (anchorY + Math.Max(iconHeight, 1));
        int spaceAbove = anchorY - workTop;
        bool below = spaceBelow >= spaceAbove;
        bool rightAligned = anchorX + Math.Max(iconWidth, 0) >
            workRight - RightAlignmentMargin;
        return new Result(
            below ? VerticalSide.Below : VerticalSide.Above,
            rightAligned);
    }
}
