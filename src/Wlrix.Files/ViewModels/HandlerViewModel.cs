using System.Globalization;
using Wlrix.Common.Desktop;

namespace Wlrix.Files.ViewModels;

/// <summary>One entry in the Open With menu.</summary>
/// <param name="Entry">The application.</param>
/// <param name="MimeType">
/// What it is being offered for. Carried along because making it the default is a statement
/// about a <i>type</i>, not about the file that happened to be selected — and by the time the
/// menu item is clicked the selection may be something else.
/// </param>
public sealed record HandlerViewModel(DesktopEntry Entry, string MimeType)
{
    /// <summary>What the menu shows, in the user's language where the entry has one.</summary>
    public string Label =>
        DesktopEntryParser.ResolveLocalized(Entry.Name, CultureInfo.CurrentCulture) is { Length: > 0 } name
            ? name
            : Entry.Id;

    /// <summary>The label, not the record's field dump.</summary>
    /// <remarks>
    /// A menu item whose Header is an object with no matching template renders whatever this
    /// answers, and a record's generated version is a list of its properties. That is what a
    /// dropped template looked like on screen once already.
    /// </remarks>
    public override string ToString() => Label;
}
