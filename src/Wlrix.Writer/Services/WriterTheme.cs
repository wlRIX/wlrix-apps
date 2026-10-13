// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Media;
using TextMateSharp.Grammars;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Themes.Reader;
using TextMateSharp.Internal.Types;
using TextMateSharp.Registry;
using TextMateSharp.Themes;
using Wlrix.Writer.Models;

namespace Wlrix.Writer.Services;

/// <summary>
/// The grammars, the language list and the highlighting themes, shared by every editor.
/// </summary>
/// <remarks>
/// <para>
/// One per process. Reading the bundled grammar package takes a noticeable moment, and every
/// editor would otherwise do it again. <c>App</c> warms it on a worker thread at startup, so the
/// first file's highlighting does not wait for it.
/// </para>
/// <para>
/// The themes are templates rather than finished themes. Each holds only token colors: comments,
/// strings, keywords and so on, in NEdit's default styles. The default foreground and background
/// come from the session's color scheme when the theme is built, so plain text is always exactly
/// the scheme's text color on the scheme's text area, and a scheme change rebuilds the theme to
/// match. Which template is used is the scheme's own dark/light flag.
/// </para>
/// </remarks>
public sealed class WriterTheme
{
    private static readonly Lazy<WriterTheme> Instance = new(() => new WriterTheme(), isThreadSafe: true);

    private readonly JsonObject _light;
    private readonly JsonObject _dark;

    private WriterTheme()
    {
        // The theme named here is never used: every editor is installed with a WriterRegistryOptions
        // whose default theme is one built below.
        Registry = new RegistryOptions(ThemeName.Light);
        Languages = new LanguageCatalog(Registry);
        _light = LoadTemplate("Themes.nedit-light.json");
        _dark = LoadTemplate("Themes.nedit-dark.json");
    }

    public static WriterTheme Shared => Instance.Value;

    /// <summary>The bundled grammars.</summary>
    public RegistryOptions Registry { get; }

    /// <summary>The languages those grammars cover.</summary>
    public LanguageCatalog Languages { get; }

    /// <summary>A theme for a scheme with the given darkness and text colors.</summary>
    public IRawTheme Build(bool dark, Color foreground, Color background)
    {
        var theme = (JsonObject)(dark ? _dark : _light).DeepClone();
        var fg = Hex(foreground);
        var bg = Hex(background);

        // An entry with no scope is the theme's default, which is what every token the palette
        // does not name is drawn in. It goes first, as TextMate themes put it.
        var tokens = theme["tokenColors"] as JsonArray ?? [];
        tokens.Insert(0, new JsonObject
        {
            ["settings"] = new JsonObject { ["foreground"] = fg, ["background"] = bg },
        });
        theme["tokenColors"] = tokens;
        theme["colors"] = new JsonObject { ["editor.foreground"] = fg, ["editor.background"] = bg };

        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(theme.ToJsonString())));
        return ThemeReader.ReadThemeSync(reader);
    }

    /// <summary>Registry options for one editor, whose default theme is <paramref name="theme"/>.</summary>
    public IRegistryOptions OptionsFor(IRawTheme theme) => new WriterRegistryOptions(Registry, theme);

    private static JsonObject LoadTemplate(string name)
    {
        using var stream = typeof(WriterTheme).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"missing embedded theme {name}");
        return JsonNode.Parse(stream) as JsonObject
               ?? throw new InvalidOperationException($"embedded theme {name} is not a JSON object");
    }

    private static string Hex(Color color) => $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    /// <summary>The bundled grammars, with a theme of this program's in place of the package's.</summary>
    private sealed class WriterRegistryOptions(RegistryOptions inner, IRawTheme theme) : IRegistryOptions
    {
        public IRawTheme GetTheme(string scopeName) => inner.GetTheme(scopeName);

        public IRawGrammar GetGrammar(string scopeName) => inner.GetGrammar(scopeName);

        public ICollection<string> GetInjections(string scopeName) => inner.GetInjections(scopeName);

        public IRawTheme GetDefaultTheme() => theme;
    }
}
