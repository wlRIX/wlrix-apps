// SPDX-License-Identifier: GPL-3.0-or-later

namespace Wlrix.Writer.Models;

/// <summary>What a document can be previewed as, if anything.</summary>
public enum PreviewKind
{
    None,
    Markdown,
    Svg,
}

/// <summary>How a previewable document is shown in its tab.</summary>
public enum ViewMode
{
    /// <summary>The text alone, as any other file.</summary>
    Editor,

    /// <summary>The text and the preview side by side, the text on the left.</summary>
    Split,

    /// <summary>The preview alone.</summary>
    Preview,
}

/// <summary>Deciding which documents have a preview.</summary>
public static class PreviewKinds
{
    /// <summary>The language id the grammar package gives Markdown.</summary>
    public const string MarkdownLanguage = "markdown";

    /// <summary>The preview a document gets, from its language mode and its file name.</summary>
    /// <remarks>
    /// Markdown goes by the language mode, so an untitled buffer switched to Markdown gets the
    /// preview as well, and a <c>.md</c> file switched to Plain loses it. SVG has no language of
    /// its own (the grammars read it as XML), so it goes by the file's extension.
    /// </remarks>
    public static PreviewKind For(string? path, string? languageId)
    {
        if (string.Equals(languageId, MarkdownLanguage, StringComparison.Ordinal))
            return PreviewKind.Markdown;
        if (path is not null && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return PreviewKind.Svg;
        return PreviewKind.None;
    }
}

/// <summary>The mode each kind of previewable file opens in: the last one chosen for that kind.</summary>
public sealed record PreviewDefaults
{
    public static PreviewDefaults Initial { get; } = new();

    public ViewMode Markdown { get; init; } = ViewMode.Split;
    public ViewMode Svg { get; init; } = ViewMode.Split;

    public ViewMode For(PreviewKind kind) => kind switch
    {
        PreviewKind.Markdown => Markdown,
        PreviewKind.Svg => Svg,
        _ => ViewMode.Editor,
    };

    public PreviewDefaults With(PreviewKind kind, ViewMode mode) => kind switch
    {
        PreviewKind.Markdown => this with { Markdown = mode },
        PreviewKind.Svg => this with { Svg = mode },
        _ => this,
    };
}
