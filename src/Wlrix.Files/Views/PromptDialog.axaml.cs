using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Wlrix.Files.Views;

/// <summary>Asks for a single line of text.</summary>
/// <remarks>
/// Rename and New Folder both need one and <c>Wlrix.Avalonia.Dialogs</c> has only
/// <c>MessageDialog</c>, so this lives in the app. It is a candidate for promotion into the
/// dialogs package the moment a second application wants one.
/// </remarks>
public partial class PromptDialog : Window
{
    public PromptDialog()
    {
        InitializeComponent();
        Accept.Click += (_, _) => Close(Input.Text);
        Reject.Click += (_, _) => Close(null);
    }

    /// <summary>Asks for a name, returning null if the user canceled.</summary>
    /// <param name="selectExtension">
    /// Whether the extension is part of the initial selection. False when renaming, so typing
    /// replaces the stem and leaves <c>.txt</c> alone — which is what a rename almost always
    /// means.
    /// </param>
    public static async Task<string?> ShowAsync(
        Window owner, string title, string prompt, string initial = "", bool selectExtension = true)
    {
        var dialog = new PromptDialog { Title = title };
        dialog.Prompt.Text = prompt;
        dialog.Input.Text = initial;

        dialog.Opened += (_, _) =>
        {
            dialog.Input.Focus();
            var dot = initial.LastIndexOf('.');
            var end = !selectExtension && dot > 0 ? dot : initial.Length;
            dialog.Input.SelectionStart = 0;
            dialog.Input.SelectionEnd = end;
        };

        var answer = await dialog.ShowDialog<string?>(owner);
        return string.IsNullOrWhiteSpace(answer) ? null : answer.Trim();
    }
}
