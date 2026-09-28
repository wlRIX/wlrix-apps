using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Wlrix.Archiver.Views;

/// <summary>Asks for an archive's password.</summary>
/// <remarks>
/// A masked cousin of wlRIX Files' PromptDialog, and the second application to want an input
/// dialog — which by that file's own note is the moment the shape earns promotion into
/// <c>Wlrix.Avalonia.Dialogs</c>, where only <c>MessageDialog</c> lives today. Left here for now
/// because moving it is a theme release rather than an edit.
///
/// <para>
/// Nothing remembers what is typed beyond the window that asked: no keyring entry, no file. An
/// archive password is a thing somebody has written down elsewhere, not an account credential,
/// and storing it would be a promise this application has no way to keep well.
/// </para>
/// </remarks>
public partial class PasswordDialog : Window
{
    public PasswordDialog()
    {
        InitializeComponent();
        // Coalesced, and it matters: an empty TextBox has a *null* Text, so closing with it
        // straight would make "OK with nothing typed" indistinguishable from Cancel. Creating an
        // archive needs those to be different answers -- empty means "do not encrypt it".
        Accept.Click += (_, _) => Close(Input.Text ?? string.Empty);
        Reject.Click += (_, _) => Close(null);
    }

    /// <summary>
    /// Asks for a password. Null if the user canceled, empty if they accepted without typing one.
    /// </summary>
    /// <remarks>
    /// Both answers exist because both are meaningful when creating an archive: canceling calls
    /// the whole thing off, while accepting an empty box asks for an unencrypted archive. A
    /// caller that requires a password treats the two alike, which is why this does not have to
    /// know which kind of caller it has.
    /// </remarks>
    public static async Task<string?> ShowAsync(Window owner, string title, string prompt)
    {
        var dialog = new PasswordDialog { Title = title };
        dialog.Prompt.Text = prompt;
        dialog.Opened += (_, _) => dialog.Input.Focus();

        // Not trimmed, unlike a filename: a space is a legal character in a password and is
        // exactly the sort of thing somebody would not notice being eaten.
        return await dialog.ShowDialog<string?>(owner);
    }
}
