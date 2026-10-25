// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Localization;
using Wlrix.IconCatalog.Models;

namespace Wlrix.IconCatalog.Views;

/// <summary>Selected ▸ Edit: a launcher's name, command, environment, directory and terminal.</summary>
public partial class EntryDialog : Window
{
    private readonly EntryEditing? _editing;

    // For the XAML loader and the designer.
    public EntryDialog() => AvaloniaXamlLoader.Load(this);

    private EntryDialog(EntryEditing editing) : this()
    {
        _editing = editing;
        var fields = editing.Read();
        Field<TextBox>("NameBox").Text = fields.Name;
        Field<TextBox>("CommandBox").Text = fields.Command;
        Field<TextBox>("DirectoryBox").Text = fields.WorkingDirectory;
        Field<TextBox>("EnvironmentBox").Text = string.Join('\n', fields.Environment.Select(v => $"{v.Key}={v.Value}"));
        Field<CheckBox>("TerminalBox").IsChecked = fields.Terminal;

        Field<TextBlock>("Where").Text = editing switch
        {
            { MakesCopy: true } => Strings.EditSavesCopy(Path.GetDirectoryName(editing.TargetPath)!),
            { CanRevert: true } => Strings.EditSavesOverride(Path.GetDirectoryName(editing.TargetPath)!),
            _ => Strings.EditSavesFile(editing.TargetPath),
        };

        var revert = Field<Button>("Revert");
        revert.IsVisible = editing.CanRevert;
        revert.Click += (_, _) => Finish(() => editing.Revert());
        Field<Button>("Accept").Click += (_, _) => OnAccept();
        Field<Button>("Reject").Click += (_, _) => Close(false);
        Opened += (_, _) => Field<TextBox>("CommandBox").Focus();
    }

    /// <summary>Edits <paramref name="editing"/>'s launcher. True when something was saved or reverted.</summary>
    public static Task<bool> ShowAsync(Window owner, EntryEditing editing) =>
        new EntryDialog(editing).ShowDialog<bool>(owner);

    private T Field<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnAccept()
    {
        if (_editing is not { } editing)
            return;

        var command = Field<TextBox>("CommandBox").Text?.Trim() ?? string.Empty;
        if (command.Length == 0)
        {
            Complain(Strings.EditNoCommand);
            return;
        }

        var environment = new List<KeyValuePair<string, string>>();
        foreach (var line in (Field<TextBox>("EnvironmentBox").Text ?? string.Empty).Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
                continue;
            var equals = text.IndexOf('=');
            if (equals <= 0 || !ExecEnvironment.IsValidName(text[..equals]))
            {
                Complain(Strings.EditBadVariable(text));
                return;
            }
            environment.Add(new(text[..equals], text[(equals + 1)..]));
        }

        var fields = new EntryFields(
            Field<TextBox>("NameBox").Text?.Trim() ?? string.Empty,
            command,
            environment,
            Field<TextBox>("DirectoryBox").Text?.Trim() ?? string.Empty,
            Field<CheckBox>("TerminalBox").IsChecked == true);
        Finish(() => editing.Save(fields));
    }

    // Runs a save or a revert; closes on success, says why on failure and stays open.
    private void Finish(Action change)
    {
        try
        {
            change();
            Close(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Complain(Strings.EditSaveFailed(_editing!.TargetPath, ex.Message));
        }
    }

    private void Complain(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message,
            title: Strings.ErrorTitle, buttons: DialogButtons.Ok, okText: Strings.DialogOk);
}
