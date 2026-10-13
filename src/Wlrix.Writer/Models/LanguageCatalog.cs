// SPDX-License-Identifier: GPL-3.0-or-later

using TextMateSharp.Grammars;

namespace Wlrix.Writer.Models;

/// <summary>One entry in Preferences ▸ Language Mode.</summary>
/// <param name="Id">The grammar package's language id, e.g. <c>cpp</c>.</param>
/// <param name="Name">What the menu shows, e.g. <c>C++</c>.</param>
/// <param name="Scope">The TextMate scope the grammar is registered under.</param>
public sealed record LanguageMode(string Id, string Name, string Scope);

/// <summary>The languages the bundled TextMate grammars can highlight.</summary>
/// <remarks>
/// A null language id means "Plain", which is what NEdit called no language mode and what an
/// unrecognized file opens as.
/// </remarks>
public sealed class LanguageCatalog
{
    /// <summary>
    /// File names that say what they are without an extension. The grammar package only matches
    /// extensions, and these are common enough that opening one as Plain would look broken.
    /// </summary>
    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["Makefile"] = "makefile",
        ["GNUmakefile"] = "makefile",
        ["makefile"] = "makefile",
        ["Dockerfile"] = "dockerfile",
        ["PKGBUILD"] = "shellscript",
        [".bashrc"] = "shellscript",
        [".bash_profile"] = "shellscript",
        [".profile"] = "shellscript",
        [".zshrc"] = "shellscript",
    };

    private readonly RegistryOptions _registry;
    private readonly Dictionary<string, LanguageMode> _byId;

    public LanguageCatalog(RegistryOptions registry)
    {
        _registry = registry;
        _byId = new Dictionary<string, LanguageMode>(StringComparer.Ordinal);

        foreach (var language in registry.GetAvailableLanguages())
        {
            // Some packages contribute a language with no grammar of its own (a configuration
            // dialect, say); there is nothing to highlight it with, so it is not offered.
            if (string.IsNullOrEmpty(language.Id) || registry.GetScopeByLanguageId(language.Id) is not { } scope)
                continue;
            var name = language.Aliases?.FirstOrDefault(alias => !string.IsNullOrWhiteSpace(alias)) ?? language.Id;
            _byId.TryAdd(language.Id, new LanguageMode(language.Id, name, scope));
        }

        All = [.. _byId.Values.OrderBy(mode => mode.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Every language, sorted by name for the menu.</summary>
    public IReadOnlyList<LanguageMode> All { get; }

    /// <summary>The language with this id, or null for Plain or an id this build does not have.</summary>
    public LanguageMode? Find(string? id) =>
        id is not null && _byId.TryGetValue(id, out var mode) ? mode : null;

    /// <summary>The language a file's name suggests, or null for Plain.</summary>
    public string? ForPath(string path)
    {
        var name = Path.GetFileName(path);
        if (KnownNames.TryGetValue(name, out var known) && _byId.ContainsKey(known))
            return known;

        var extension = Path.GetExtension(name);
        if (string.IsNullOrEmpty(extension))
            return null;

        // The grammar package matches extensions case-sensitively; .C and .H are C++ to some and
        // C to others, and trying the lower-case form matches what most people expect.
        var language = _registry.GetLanguageByExtension(extension)
                       ?? _registry.GetLanguageByExtension(extension.ToLowerInvariant());
        return language?.Id is { } id && _byId.ContainsKey(id) ? id : null;
    }
}
