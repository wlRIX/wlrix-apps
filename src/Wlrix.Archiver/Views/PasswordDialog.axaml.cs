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
        Accept.Click += (_, _) => Close(Input.Text);
        Reject.Click += (_, _) => Close(null);
    }

    /// <summary>Asks for a password, returning null if the user declined.</summary>
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
