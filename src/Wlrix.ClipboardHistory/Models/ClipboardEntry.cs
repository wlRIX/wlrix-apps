// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.ClipboardHistory.Models;

/// <summary>What an entry holds.</summary>
public enum EntryKind
{
    Text,
    Image,
}

/// <summary>
/// One entry in the history, as <c>wlrix-clipboard</c> sends it.
/// </summary>
/// <param name="Id">The daemon's id for it. Never reused, so a stale list cannot act on the wrong entry.</param>
/// <param name="Kind">Text or an image.</param>
/// <param name="Text">The whole text, for a text entry; empty for an image.</param>
/// <param name="ImagePath">Where the image's bytes are, for an image entry; empty for text.</param>
/// <param name="Starred">Whether it is kept for good.</param>
/// <param name="Created">When it was last copied.</param>
public sealed record ClipboardEntry(
    ulong Id,
    EntryKind Kind,
    string Text,
    string ImagePath,
    bool Starred,
    DateTimeOffset Created);
