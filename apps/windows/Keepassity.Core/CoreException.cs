// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.Core;

/// <summary>
/// A failure reported by the native core. <see cref="Code"/> is the stable
/// contract code (for example <c>BadPassword</c>); the message is already
/// user-facing text and never contains vault content.
/// </summary>
public sealed class CoreException : Exception
{
    public CoreException(string code, string message) : base($"{code}: {message}")
    {
        Code = code;
    }

    public string Code { get; }
}
