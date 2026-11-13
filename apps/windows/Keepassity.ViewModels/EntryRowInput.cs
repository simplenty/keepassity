// SPDX-License-Identifier: GPL-3.0-or-later
namespace Keepassity.ViewModels;

/// <summary>
/// One entry as the list needs it. The shell maps the FFI DTO to this record and
/// then works only through the view models, so sorting, filtering and the row's
/// display strings are testable without WinUI.
/// </summary>
public readonly record struct EntryRowInput(
    string Uuid,
    string GroupUuid,
    string GroupName,
    string Title,
    string Username,
    string Url,
    bool HasTotp,
    long ModifiedUnix);
