// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Wlrix.Common;

namespace Wlrix.Writer.Views;

/// <summary>A question with up to three answers.</summary>
/// <remarks>
/// Save / Don't Save / Cancel needs three buttons and <c>MessageDialog</c> has two, so this is a
/// copy of its look with a button row built from a list. A candidate for promotion into
/// Wlrix.Avalonia.Dialogs once another application needs it.
/// </remarks>
public partial class ChoiceDialog : Window
{
    public ChoiceDialog()
    {
        InitializeComponent();
    }

    /// <summary>One button: its label, the answer it gives, and its role.</summary>
    public sealed record Choice<T>(string Label, T Answer, bool IsDefault = false, bool IsCancel = false);

    /// <summary>Shows the question and returns the chosen answer, or <paramref name="dismissed"/> if the window was closed.</summary>
    /// <param name="icon">One of the dialog package's icons: Question, Warning, Information, Error.</param>
    public static async Task<T> ShowAsync<T>(Window owner, string title, string icon, string message,
        T dismissed, params Choice<T>[] choices)
    {
        var dialog = new ChoiceDialog { Title = title };
        dialog.Message.Text = message;
        dialog.IconImage.Source = LoadIcon(icon);

        foreach (var choice in choices)
        {
            var button = new Button
            {
                Content = choice.Label,
                MinWidth = 64,
                IsDefault = choice.IsDefault,
                IsCancel = choice.IsCancel,
            };
            button.Click += (_, _) => dialog.Close(choice.Answer);
            dialog.Buttons.Children.Add(button);
        }

        dialog.Opened += (_, _) =>
        {
            dialog.Buttons.Children.OfType<Button>().FirstOrDefault(button => button.IsDefault)?.Focus();
            // Sounds like a MessageDialog with the same icon, which is what it looks like.
            SystemSound.Play(SystemSound.ForDialogIcon(icon));
        };

        var answer = await dialog.ShowDialog<object?>(owner);
        return answer is T chosen ? chosen : dismissed;
    }

    private static Bitmap? LoadIcon(string name)
    {
        try
        {
            return new Bitmap(AssetLoader.Open(new Uri($"avares://Wlrix.Avalonia.Dialogs/Assets/Icons/{name}.png")));
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException)
        {
            return null;
        }
    }
}
