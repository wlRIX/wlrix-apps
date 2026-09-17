using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Files.Core.Operations;
using Wlrix.Files.Localization;
using Wlrix.Files.ViewModels;

namespace Wlrix.Files.Views;

/// <summary>Asks what to do about a name that is already taken.</summary>
public partial class ConflictDialog : Window
{
    public ConflictDialog()
    {
        InitializeComponent();
        OverwriteButton.Click += (_, _) => Answer(ConflictAction.Overwrite);
        RenameButton.Click += (_, _) => Answer(ConflictAction.Rename);
        SkipButton.Click += (_, _) => Answer(ConflictAction.Skip);
        // Cancelling abandons the whole operation, so Apply to all does not apply to it.
        CancelButton.Click += (_, _) => Close(ConflictDecision.Cancel());
    }

    private void Answer(ConflictAction action) =>
        Close(new ConflictDecision(action, ApplyToAll: ApplyToAll.IsChecked == true));

    public static async Task<ConflictDecision> ShowAsync(Window owner, ConflictContext context)
    {
        var dialog = new ConflictDialog { Title = Strings.ConflictTitle };
        dialog.Summary.Text = Strings.ConflictSummary(context.Target.Name);
        dialog.Existing.Text = Describe(context.TargetSize, context.TargetModified, context.IsDirectory);
        dialog.Replacement.Text = Describe(context.SourceSize, context.SourceModified, context.IsDirectory);

        var answer = await dialog.ShowDialog<ConflictDecision?>(owner);
        // A dialog dismissed by the window manager is not an instruction to overwrite
        // anything, so the safe reading is to skip.
        return answer ?? ConflictDecision.Skip();
    }

    private static string Describe(long size, DateTimeOffset? modified, bool isDirectory)
    {
        var when = modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "-";
        return isDirectory ? when : $"{FileEntryViewModel.FormatSize(size)}, {when}";
    }
}
