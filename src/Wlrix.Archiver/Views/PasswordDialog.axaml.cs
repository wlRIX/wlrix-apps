using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Archiver.ViewModels;

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
        Accept.Click += (_, _) => Commit();
        Reject.Click += (_, _) => Close(null);

        // Enter on the tunnel, not left to the OK button's IsDefault, because IsDefault does not
        // survive the checkbox having focus. CheckBox descends from Button, whose OnKeyDown
        // handles Enter unconditionally as a click on itself -- so tabbing to "encrypt the file
        // names", ticking it and pressing Enter *unticked it again* and left the dialog open.
        // Silently undoing the choice somebody just made is worse than not committing.
        AddHandler(KeyDownEvent, OnKeyDownTunnel, RoutingStrategies.Tunnel);
    }

    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return))
            return;

        e.Handled = true;
        Commit();
    }

    /// <summary>Closes with what was chosen.</summary>
    /// <remarks>
    /// The text is coalesced, and it matters: an empty TextBox has a <c>null</c> Text, so closing
    /// with it straight would make "OK with nothing typed" indistinguishable from Cancel.
    /// Creating an archive needs those to be different answers -- empty means "do not encrypt it".
    /// </remarks>
    private void Commit() =>
        Close(new NewArchiveEncryption(Input.Text ?? string.Empty, EncryptNames.IsChecked == true));

    /// <summary>
    /// Asks for a password. Null if the user canceled, empty if they accepted without typing one.
    /// </summary>
    /// <remarks>
    /// Both answers exist because both are meaningful when creating an archive: canceling calls
    /// the whole thing off, while accepting an empty box asks for an unencrypted archive. A
    /// caller that requires a password treats the two alike, which is why this does not have to
    /// know which kind of caller it has.
    /// </remarks>
    public static async Task<string?> ShowAsync(Window owner, string title, string prompt) =>
        (await ShowCore(owner, title, prompt, offerNameOption: false))?.Password;

    /// <summary>
    /// Asks what encryption a new archive should have. Null if the user canceled.
    /// </summary>
    /// <remarks>
    /// The same dialog with one more question, because it is one decision: whether to protect
    /// this archive, and how much of it. Splitting it in two would ask somebody who wants no
    /// password at all about encrypting names they have already declined to hide.
    /// </remarks>
    public static Task<NewArchiveEncryption?> ShowForNewAsync(
        Window owner, string title, string prompt) =>
        ShowCore(owner, title, prompt, offerNameOption: true);

    private static async Task<NewArchiveEncryption?> ShowCore(
        Window owner, string title, string prompt, bool offerNameOption)
    {
        var dialog = new PasswordDialog { Title = title };
        dialog.Prompt.Text = prompt;
        dialog.NameOption.IsVisible = offerNameOption;
        dialog.Opened += (_, _) => dialog.Input.Focus();

        // Not trimmed, unlike a filename: a space is a legal character in a password and is
        // exactly the sort of thing somebody would not notice being eaten.
        return await dialog.ShowDialog<NewArchiveEncryption?>(owner);
    }
}
