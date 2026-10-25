// SPDX-License-Identifier: GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Settings.Utilities.Controls;
using Wlrix.Settings.Utilities.Localization;
using Wlrix.Settings.Utilities.ViewModels;

namespace Wlrix.Settings.Utilities.Views;

public partial class MainWindow : Window
{
    /// <summary>Set once the user has answered the unapplied-changes question, so Close closes.</summary>
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        Hint.Changed += OnHint;
        Closing += OnClosing;
        DataContextChanged += (_, _) =>
        {
            if (Model is { } model)
                model.ChooseApplication = () => ChooseAsync(model);
        };
    }

    private UtilitiesViewModel? Model => DataContext as UtilitiesViewModel;

    private TypeDetailsViewModel? Details => Model?.FileTypes?.Details;

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Hint.Changed -= OnHint;
    }

    /// <summary>
    /// Closing with changes not applied asks first. Apply writes them and closes; Discard
    /// closes; shutting the question's window stays, which is the safe reading of no answer.
    /// </summary>
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed || Model is not { HasChanges: true } model)
            return;

        e.Cancel = true;
        var answer = await MessageDialog.ShowAsync(this, DialogType.Question, Strings.ClosePrompt,
            title: Strings.CloseTitle, okText: Strings.Apply, cancelText: Strings.Discard);
        if (answer is null)
            return;
        if (answer == DialogResult.Ok)
            await model.ApplyAsync();
        _closeConfirmed = true;
        Close();
    }

    private void OnHint(string? text) =>
        this.FindControl<TextBlock>("HintLine")!.Text = text ?? string.Empty;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private async void OnApply(object? sender, RoutedEventArgs e)
    {
        if (Model is { } model)
            await model.ApplyAsync();
    }

    private void OnReset(object? sender, RoutedEventArgs e) => Model?.Reset();

    private async void OnAddType(object? sender, RoutedEventArgs e)
    {
        if (Model?.FileTypes is not { } types)
            return;
        var name = await PromptDialog.ShowAsync(this, Strings.AddTypeTitle, Strings.AddTypePrompt, "application/x-");
        if (name is not null && types.AddType(name) is { } error)
            await MessageDialog.ShowAsync(this, DialogType.Warning, error, buttons: DialogButtons.Ok);
    }

    private void OnRemoveType(object? sender, RoutedEventArgs e) => Model?.FileTypes?.RemoveType();

    private async void OnAddPattern(object? sender, RoutedEventArgs e)
    {
        if (Details is not { } details)
            return;
        if (await PromptDialog.ShowAsync(this, Strings.AddPatternTitle, Strings.AddPatternPrompt, "*.") is { } pattern
            && pattern != "*.")
        {
            details.AddPattern(pattern);
        }
    }

    private void OnRemovePattern(object? sender, RoutedEventArgs e) => Details?.RemovePattern();

    private void OnMoveUp(object? sender, RoutedEventArgs e) => Details?.MoveUp();

    private void OnMoveDown(object? sender, RoutedEventArgs e) => Details?.MoveDown();

    private async void OnAddApp(object? sender, RoutedEventArgs e)
    {
        if (Details is not { } details || Model is not { } model)
            return;
        if (await ChooseAsync(model) is { Entry: { } entry })
            details.AddApp(entry);
    }

    private void OnRemoveApp(object? sender, RoutedEventArgs e) => Details?.RemoveApp();

    private void OnRevert(object? sender, RoutedEventArgs e) => Details?.Revert();

    private Task<AppChoice?> ChooseAsync(UtilitiesViewModel model) =>
        new ApplicationChooser(model.AllApplications).ShowDialog<AppChoice?>(this);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
