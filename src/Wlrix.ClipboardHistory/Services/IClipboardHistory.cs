// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.ClipboardHistory.Models;

namespace Wlrix.ClipboardHistory.Services;

/// <summary>
/// The history, as kept by <c>wlrix-clipboard</c>.
/// </summary>
/// <remarks>
/// An interface because the implementation talks to the session bus and the tests have none.
/// Every change is made by the daemon; <see cref="Changed"/> is how this side hears about it,
/// including the changes it asked for itself.
/// </remarks>
public interface IClipboardHistory : IDisposable
{
    /// <summary>The history, newest first.</summary>
    Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync();

    /// <summary>The entry on the clipboard right now, or null when the clipboard holds something else.</summary>
    Task<ulong?> GetActiveAsync();

    /// <summary>Put an entry on the clipboard, so it is what pastes next.</summary>
    Task ActivateAsync(ulong id);

    /// <summary>Replace a text entry's text.</summary>
    Task SetTextAsync(ulong id, string text);

    Task RemoveAsync(ulong id);

    Task SetStarredAsync(ulong id, bool starred);

    /// <summary>Delete every unstarred entry.</summary>
    Task ClearAsync();

    /// <summary>The history changed. Raised on a bus thread.</summary>
    event Action? Changed;
}
