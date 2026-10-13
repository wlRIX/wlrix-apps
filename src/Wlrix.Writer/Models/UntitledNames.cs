// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Models;

/// <summary>Numbering for documents that have never been saved.</summary>
/// <remarks>
/// The first is "Untitled" and the next "Untitled 2", and a number is reused once its document is
/// gone, so a session of New and Close does not count up forever.
/// </remarks>
public static class UntitledNames
{
    /// <summary>The lowest number not in <paramref name="inUse"/>, starting from one.</summary>
    public static int Next(IEnumerable<int> inUse)
    {
        var taken = inUse.ToHashSet();
        var number = 1;
        while (taken.Contains(number))
            number++;
        return number;
    }

    /// <summary>The display name for untitled document <paramref name="number"/>.</summary>
    public static string Format(string untitled, int number) =>
        number <= 1 ? untitled : $"{untitled} {number}";
}
