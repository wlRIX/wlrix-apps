namespace Wlrix.Desks.Models;

/// <summary>
/// What a miniature window says when the pointer passes over it.
/// </summary>
/// <remarks>
/// IRIX's <c>ov</c> had one switch here, <c>--noWindowName</c>, which swapped window names for
/// application IDs. The Overview menu offers the third state as well, because a switch you can
/// only reach by relaunching the program is one nobody can undo.
/// </remarks>
public enum WindowLabel
{
    /// <summary>The window's own title, falling back to its application ID.</summary>
    Title,

    /// <summary>The application ID, falling back to the window's title. <c>--noWindowName</c>.</summary>
    AppId,

    /// <summary>Nothing at all: no tooltip.</summary>
    None,
}
