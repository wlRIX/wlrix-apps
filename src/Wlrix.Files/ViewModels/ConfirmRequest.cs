namespace Wlrix.Files.ViewModels;

/// <summary>A yes-or-no question that needs its own wording, and possibly its own weight.</summary>
/// <param name="Title">The dialog's title.</param>
/// <param name="Message">The question itself.</param>
/// <param name="OkText">
/// The verb on the accepting button. A button that says what it will do is the difference
/// between reading the dialog and dismissing it.
/// </param>
/// <param name="Severe">
/// Whether this is a warning rather than a question. Set for the confirmation that also changes
/// the file on disk.
/// </param>
public sealed record ConfirmRequest(string Title, string Message, string OkText, bool Severe);
