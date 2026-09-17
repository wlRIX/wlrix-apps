using System.IO.Enumeration;
using System.Text.Json.Serialization;

namespace Wlrix.Files.Core.Portal;

/// <summary>Which of the three <c>org.freedesktop.impl.portal.FileChooser</c> calls this is.</summary>
public enum FileChooserMode
{
    /// <summary><c>OpenFile</c>: choose one or more existing files, or a folder.</summary>
    [JsonStringEnumMemberName("open")]
    Open,

    /// <summary><c>SaveFile</c>: name a file to write.</summary>
    [JsonStringEnumMemberName("save")]
    Save,

    /// <summary>
    /// <c>SaveFiles</c>: choose a folder for a set of files whose names the application
    /// already knows.
    /// </summary>
    [JsonStringEnumMemberName("save_files")]
    SaveFiles,
}

/// <summary>One line of a file filter.</summary>
/// <param name="IsMimeType">
/// Whether <paramref name="Pattern"/> is a MIME type rather than a glob. The wire carries the
/// interface's own <c>u</c> discriminator: 0 is a glob, 1 is a MIME type.
/// </param>
/// <param name="Pattern">A glob such as <c>*.png</c>, or a type such as <c>image/png</c>.</param>
public readonly record struct FilterRule(
    [property: JsonPropertyName("mime")] bool IsMimeType,
    [property: JsonPropertyName("pattern")] string Pattern);

/// <summary>A named set of rules, as one entry of the filter list a person chooses between.</summary>
/// <remarks>
/// A filter matches a file when <b>any</b> of its rules does — that is what lets an application
/// offer "Images" as <c>*.png</c>, <c>*.jpg</c> and <c>image/svg+xml</c> together.
/// </remarks>
public sealed class FileFilter
{
    /// <summary>What the combo box shows. The application's wording, not ours.</summary>
    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("rules")]
    public IReadOnlyList<FilterRule> Rules { get; init; } = [];

    /// <summary>Whether this filter admits a file.</summary>
    /// <param name="fileName">The file's own name, without a path.</param>
    /// <param name="mimeType">
    /// Resolves the file's type, and is called only if a MIME rule is reached. Resolution costs
    /// a glob lookup at least and a read of the file's head at worst, and most filters are
    /// answered by their extension rules alone.
    /// </param>
    /// <remarks>
    /// <para>
    /// A filter with no rules admits everything, which is how an application spells "All Files".
    /// </para>
    /// <para>
    /// Globs are matched <b>ignoring case</b>, which GTK's own chooser does not do. A filter is
    /// a way of showing somebody less, not a rule about what a file is: a camera that writes
    /// <c>IMG_0001.JPG</c> would otherwise have its pictures hidden by the <c>*.jpg</c> filter
    /// an image editor asked for, with the file plainly there in another application. Being
    /// looser here can only ever offer a file the user can see and reject; being stricter hides
    /// one they came for.
    /// </para>
    /// </remarks>
    public bool Matches(string fileName, Func<string> mimeType)
    {
        if (Rules.Count == 0)
            return true;

        string? resolved = null;
        foreach (var rule in Rules)
        {
            if (!rule.IsMimeType)
            {
                if (FileSystemName.MatchesSimpleExpression(rule.Pattern, fileName, ignoreCase: true))
                    return true;
                continue;
            }

            resolved ??= mimeType();
            // `text/*` is not in the interface's vocabulary, but applications send it anyway
            // and GTK honors it. Cheaper to accept than to explain in a bug report.
            if (rule.Pattern.EndsWith("/*", StringComparison.Ordinal))
            {
                var media = rule.Pattern[..^1];
                if (resolved.StartsWith(media, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (string.Equals(rule.Pattern, resolved, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>One option in a <see cref="FileChooserChoice"/>.</summary>
public readonly record struct ChoiceOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label);

/// <summary>An extra control the application asked to have in the dialog.</summary>
/// <remarks>
/// The interface calls these "serialized combo boxes", but a choice with <b>no options</b> is a
/// checkbox whose value is the string <c>"true"</c> or <c>"false"</c> — that is the spec's own
/// rule, and an application that wanted "Open read-only" spells it exactly that way. A dialog
/// that drew an empty combo box for one would be showing a control nobody can use.
/// </remarks>
public sealed class FileChooserChoice
{
    /// <summary>Opaque to the dialog; it comes back in the answer verbatim.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    [JsonPropertyName("options")]
    public IReadOnlyList<ChoiceOption> Options { get; init; } = [];

    /// <summary>
    /// The option id to start on, or <c>"true"</c>/<c>"false"</c> for a checkbox. Empty means
    /// the application did not say.
    /// </summary>
    [JsonPropertyName("default")]
    public string Default { get; init; } = "";

    /// <summary>Whether this is a checkbox rather than a combo box. See the type's remarks.</summary>
    [JsonIgnore]
    public bool IsBoolean => Options.Count == 0;
}

/// <summary>What the application asked for.</summary>
/// <remarks>
/// <para>
/// This is the wire format between <c>xdg-desktop-portal-wlrix</c> and <c>wlrix-file-picker</c>,
/// as JSON on the picker's stdin — the same contract the source picker and the screenshot tool
/// use. The two programs are released together but built separately, so a disagreement here
/// shows up as a dialog that offers the wrong thing rather than as a build error. The backend's
/// <c>src/filechooser.rs</c> is the other half and must move with it.
/// </para>
/// <para>
/// Every path arrives already decoded. The interface carries <c>current_folder</c> and
/// <c>current_file</c> as null-terminated <c>ay</c> byte arrays, because a filename on Linux is
/// bytes and need not be valid UTF-8; the backend drops the terminator and anything it cannot
/// decode, so what reaches here is a real path or nothing.
/// </para>
/// </remarks>
public sealed class FileChooserRequest
{
    [JsonPropertyName("mode")]
    public FileChooserMode Mode { get; init; } = FileChooserMode.Open;

    /// <summary>Who asked. Empty for an unsandboxed application, which is most of them.</summary>
    [JsonPropertyName("app_id")]
    public string AppId { get; init; } = "";

    /// <summary>The dialog's title, chosen by the application.</summary>
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";

    /// <summary>
    /// The accept button's label, with a GTK mnemonic underscore if the application used one.
    /// Empty when it did not ask, in which case the dialog uses its own wording.
    /// </summary>
    [JsonPropertyName("accept_label")]
    public string AcceptLabel { get; init; } = "";

    [JsonPropertyName("multiple")]
    public bool Multiple { get; init; }

    /// <summary>Whether folders are being chosen rather than files.</summary>
    [JsonPropertyName("directory")]
    public bool Directory { get; init; }

    /// <summary>A name to start the save field with.</summary>
    [JsonPropertyName("current_name")]
    public string CurrentName { get; init; } = "";

    /// <summary>A folder to open in.</summary>
    [JsonPropertyName("current_folder")]
    public string CurrentFolder { get; init; } = "";

    /// <summary>The file being saved over, when an application is saving one it already opened.</summary>
    [JsonPropertyName("current_file")]
    public string CurrentFile { get; init; } = "";

    /// <summary>The names <c>SaveFiles</c> wants written into the chosen folder.</summary>
    [JsonPropertyName("files")]
    public IReadOnlyList<string> Files { get; init; } = [];

    [JsonPropertyName("filters")]
    public IReadOnlyList<FileFilter> Filters { get; init; } = [];

    /// <summary>
    /// Which filter to start on, as an index into <see cref="Filters"/>, or -1 for the first.
    /// </summary>
    /// <remarks>
    /// An index rather than the filter itself, so the answer can name one without the two sides
    /// having to compare structures. The backend puts a <c>current_filter</c> that is not in the
    /// list into the list, which is what keeps this an index in every case.
    /// </remarks>
    [JsonPropertyName("current_filter")]
    public int CurrentFilter { get; init; } = -1;

    [JsonPropertyName("choices")]
    public IReadOnlyList<FileChooserChoice> Choices { get; init; } = [];

    /// <summary>Where the dialog should open, given everything the application said.</summary>
    /// <param name="home">Where to land when nothing else applies.</param>
    /// <param name="isDirectory">Whether a path names a directory that exists.</param>
    /// <remarks>
    /// <c>current_folder</c> first because it is the application saying so outright; then the
    /// folder holding <c>current_file</c>, because saving over a file means starting where that
    /// file is. A folder that no longer exists is ignored rather than shown empty.
    /// </remarks>
    public Location StartingFolder(Location home, Func<string, bool> isDirectory)
    {
        if (CurrentFolder.Length > 0 && isDirectory(CurrentFolder))
            return Location.FromLocalPath(CurrentFolder);

        if (CurrentFile.Length > 0)
        {
            var parent = System.IO.Path.GetDirectoryName(CurrentFile);
            if (!string.IsNullOrEmpty(parent) && isDirectory(parent))
                return Location.FromLocalPath(parent);
        }

        return home;
    }

    /// <summary>The name the save field starts with.</summary>
    /// <remarks>
    /// <c>current_name</c> is what the application suggested; failing that, the name of
    /// <c>current_file</c>, because "Save As" on an open document should offer that document's
    /// own name rather than an empty box.
    /// </remarks>
    public string StartingName() =>
        CurrentName.Length > 0
            ? CurrentName
            : CurrentFile.Length > 0
                ? System.IO.Path.GetFileName(CurrentFile)
                : "";
}

/// <summary>One answered choice.</summary>
public readonly record struct ChoiceAnswer(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] string Value);

/// <summary>What the dialog answers, written to stdout when the user accepts.</summary>
public sealed class FileChooserResult
{
    /// <summary>
    /// The chosen files, as <c>file://</c> URIs. Empty is not an answer: the picker exits with
    /// the canceled code instead, so an empty list can never be mistaken for a selection.
    /// </summary>
    [JsonPropertyName("uris")]
    public required IReadOnlyList<string> Uris { get; init; }

    [JsonPropertyName("choices")]
    public IReadOnlyList<ChoiceAnswer> Choices { get; init; } = [];

    /// <summary>The filter that was showing, as an index into the request's list, or -1.</summary>
    [JsonPropertyName("current_filter")]
    public int CurrentFilter { get; init; } = -1;

    /// <summary>Whether the application may write to what it was given.</summary>
    [JsonPropertyName("writable")]
    public bool Writable { get; init; }
}

/// <summary>The source-generated serializer for the picker's wire format.</summary>
/// <remarks>
/// Source-generated rather than reflection-based, so this keeps working under trimming — where
/// a manifest silently reading back empty would be a dialog offering nothing, with no error
/// anywhere to say why.
/// </remarks>
[JsonSourceGenerationOptions(UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(FileChooserRequest))]
[JsonSerializable(typeof(FileChooserResult))]
public sealed partial class FileChooserJson : JsonSerializerContext;
