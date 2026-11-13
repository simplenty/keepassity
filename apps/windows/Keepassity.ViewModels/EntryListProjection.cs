// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Keepassity.ViewModels;

/// <summary>
/// The middle column's projection: filter by TOTP, order by title/modified/group,
/// and compute the cheap row identity used to avoid rebuilding the ListView when
/// the visible rows did not change.
/// </summary>
public enum EntrySort
{
    Title,
    Modified,
    Group,
}

public static class EntryListProjection
{
    public static List<EntryRowInput> Visible(IEnumerable<EntryRowInput> rows, EntrySort sort,
        bool onlyTotp)
    {
        IEnumerable<EntryRowInput> visible = rows;
        if (onlyTotp)
        {
            visible = visible.Where(row => row.HasTotp);
        }
        return sort switch
        {
            EntrySort.Modified => visible
                .OrderByDescending(row => row.ModifiedUnix)
                .ToList(),
            EntrySort.Group => visible
                .OrderBy(row => row.GroupName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
            _ => visible
                .OrderBy(row => row.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList(),
        };
    }

    /// <summary>
    /// Cheap identity of the rendered rows. A following reload with the same
    /// identity is not re-rendered, so selection and scroll survive merges and
    /// moves that do not change what the user sees.
    /// </summary>
    public static string Fingerprint(IEnumerable<EntryRowInput> rows, string title)
    {
        var builder = new StringBuilder(title);
        foreach (EntryRowInput row in rows)
        {
            builder.Append('\n')
                .Append(row.Uuid).Append('|')
                .Append(row.GroupUuid).Append('|')
                .Append(row.GroupName).Append('|')
                .Append(row.Title).Append('|')
                .Append(row.Username).Append('|')
                .Append(row.Url).Append('|')
                .Append(row.HasTotp ? '1' : '0');
        }
        return builder.ToString();
    }
}
