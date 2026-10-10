// SPDX-License-Identifier: GPL-3.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;
using Wlrix.Avalonia.Dialogs;
using Wlrix.ClipboardHistory.Localization;
using Wlrix.ClipboardHistory.ViewModels;

namespace Wlrix.ClipboardHistory.Views;

/// <summary>
/// The popup. Opened by <c>Super+V</c> or the Toolchest; closed by choosing an entry, by
/// Escape, by the same key again, or by clicking anywhere else.
/// </summary>
public partial class HistoryWindow : Window
{
    /// <summary>
    /// Whether a dialog of ours is up. It takes the focus, and losing the focus to it must not
    /// close the window it belongs to.
    /// </summary>
    private bool _dialogOpen;

    public HistoryWindow()
    {
        InitializeComponent();
        // Tunnel, so Escape and the arrows are seen before the search box or the list eat them.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        Deactivated += OnDeactivated;
    }

    private HistoryViewModel? Model => DataContext as HistoryViewModel;

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Model is { } model)
        {
            model.CloseRequested += Close;
            model.ShowError += OnShowError;
        }

        // Typing searches straight away, as in KDE.
        this.FindControl<TextBox>("SearchBox")?.Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (Model is { } model)
        {
            model.CloseRequested -= Close;
            model.ShowError -= OnShowError;
        }
    }

    /// <summary>
    /// Clicking elsewhere dismisses the popup, the way a menu is dismissed.
    /// </summary>
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (!_dialogOpen)
            Close();
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Model is not { } model)
            return;

        switch (e.Key)
        {
            case Key.Escape:
                // An open editor first: Escape there means "not this edit", not "go away".
                if (!model.CancelEdits())
                    Close();
                e.Handled = true;
                break;

            // From the search box, the arrows move through the list instead of the text, so
            // search-then-pick needs no mouse and no Tab.
            case Key.Down or Key.Up when e.Source is TextBox { Name: "SearchBox" }:
                if (CurrentList() is { } list)
                {
                    list.Focus();
                    if (list.SelectedIndex < 0 && list.ItemCount > 0)
                        list.SelectedIndex = 0;
                    else if (e.Key == Key.Down && list.SelectedIndex + 1 < list.ItemCount)
                        list.SelectedIndex++;
                    else if (e.Key == Key.Up && list.SelectedIndex > 0)
                        list.SelectedIndex--;
                    list.ScrollIntoView(list.SelectedIndex);
                }

                e.Handled = true;
                break;

            case Key.Enter when e.Source is not TextBox { AcceptsReturn: true }:
                if (model.Selected is { IsEditing: false } entry)
                    entry.ActivateCommand.Execute().Subscribe();
                e.Handled = true;
                break;

            case Key.Delete when e.Source is ListBox or ListBoxItem:
                if (model.Selected is { IsEditing: false } selected)
                    selected.RemoveCommand.Execute().Subscribe();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The list on the tab that is showing.</summary>
    private ListBox? CurrentList() =>
        this.FindControl<ListBox>(Model?.SelectedTab == 1 ? "StarredList" : "AllList");

    /// <summary>
    /// A click on a row puts it on the clipboard — unless it landed on one of the row's own
    /// buttons or in its editor, which do their own thing.
    /// </summary>
    private void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: EntryViewModel entry } || entry.IsEditing)
            return;
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;
        entry.ActivateCommand.Execute().Subscribe();
    }

    /// <summary>The editor takes the focus as it appears, with its text ready to type over.</summary>
    /// <remarks>
    /// Every row has an editor, hidden until Edit is pressed, so this watches for it being shown
    /// rather than created.
    /// </remarks>
    private void OnEditorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox editor)
            editor.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty && editor.IsEffectivelyVisible)
                {
                    editor.Focus();
                    editor.CaretIndex = editor.Text?.Length ?? 0;
                }
            };
    }

    /// <summary>Ctrl+Enter saves; a plain Enter is a line break, since the text may want one.</summary>
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && sender is Control { DataContext: EntryViewModel entry })
        {
            entry.SaveCommand.Execute().Subscribe();
            e.Handled = true;
        }
    }

    private async void OnClear(object? sender, RoutedEventArgs e)
    {
        if (Model is not { } model)
            return;
        _dialogOpen = true;
        try
        {
            var answer = await MessageDialog.ShowAsync(this, DialogType.Question, Strings.ClearConfirm,
                title: Strings.ErrorTitle, okText: Strings.ClearOk);
            if (answer == DialogResult.Ok)
                await model.ClearAsync();
        }
        finally
        {
            _dialogOpen = false;
        }

        Activate();
    }

    private async void OnShowError(string message)
    {
        _dialogOpen = true;
        try
        {
            await MessageDialog.ShowAsync(this, DialogType.Error, message,
                buttons: DialogButtons.Ok, title: Strings.ErrorTitle);
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
