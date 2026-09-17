using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Wlrix.Avalonia.Controls;
using Wlrix.Avalonia.Dialogs;
using Wlrix.Files.Core.Portal;
using Wlrix.FilePicker.Localization;
using Wlrix.FilePicker.ViewModels;
// Avalonia.Controls has a Location of its own, and Wlrix.Avalonia.Controls is in scope here.
using Location = Wlrix.Files.Core.Location;

namespace Wlrix.FilePicker.Views;

/// <summary>The chooser itself.</summary>
/// <remarks>
/// The view holds the plumbing and nothing decidable: which files the accept button answers
/// with is <see cref="Services.AcceptPolicy"/>'s, and what a row shows is the view model's.
/// That division is what lets any of this be tested, since nothing on this machine can
/// synthesize the gestures that reach it.
/// </remarks>
public partial class PickerWindow : Window
{
    /// <summary>The size the listing draws icons at, which real themes ship artwork for.</summary>
    private const int IconSize = 16;

    public PickerWindow()
    {
        // No hand-written parameterless InitializeComponent: the generated
        // InitializeComponent(bool) both loads the XAML and assigns the x:Name fields, and a
        // hand-written stub that only loads leaves every one of them null.
        InitializeComponent();

        Listing.ContainerPrepared += (_, e) =>
        {
            if (Model is { } model && e.Container.DataContext is EntryViewModel row)
                model.EnsureIcon(row);
        };
        Listing.SelectionChanged += OnSelectionChanged;
        Listing.DoubleTapped += OnListingDoubleTapped;
        // On the **tunnel**, and that is not a style choice. The listing marks Return handled
        // on its way up -- verified by logging both routes under the nested compositor: the
        // bubble arrives with `handled=True`, so an ordinary `Listing.KeyDown += ...` is never
        // called and Return in the listing did nothing at all. Taking it on the way down also
        // means one handler rather than two firing on one keystroke.
        Listing.AddHandler(KeyDownEvent, OnListingKeyDown, RoutingStrategies.Tunnel);
    }

    private PickerViewModel? Model => DataContext as PickerViewModel;

    /// <summary>
    /// What the window answered with, or null if it was canceled.
    /// </summary>
    /// <remarks>
    /// Read by <see cref="App"/> when the window closes, which is the one path every way out
    /// goes through. Null until something sets it, so every unexpected end is a cancel.
    /// </remarks>
    public FileChooserResult? Result { get; private set; }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (Model is not { } model)
            return;

        model.CloseRequested += OnCloseRequested;
        model.ErrorRaised += OnError;
        model.ConfirmOverwriteRequested += OnConfirmOverwrite;
        model.Refiltered += OnRefiltered;

        // From the request rather than the markup. An application that asked for one file and
        // is handed three will use the first, and the user will believe all three went.
        Listing.SelectionMode = model.Multiple ? SelectionMode.Multiple : SelectionMode.Single;

        BuildChoices(model);
        DescribeSaveFiles(model);

        _ = StartAsync(model);
    }

    /// <summary>Read the first directory, then put the caret somewhere useful.</summary>
    /// <remarks>
    /// The focus has to wait for the read, and that is not a nicety. An empty
    /// <c>TableView</c> has no items to give focus to, so focusing it before the first batch
    /// arrives quietly does nothing and the first focusable control takes it instead — which
    /// here is the Up button, so the dialog opened with the arrow keys walking the button row
    /// and Space navigating to the parent directory.
    /// </remarks>
    private async Task StartAsync(PickerViewModel model)
    {
        // The rows landing raises Refiltered, which is what selects and focuses the first one.
        await model.ReloadAsync();

        // The name field, in a save dialog: it is the one control somebody is certain to want,
        // and the stem is selected rather than the extension, so typing a new name keeps
        // `.odt` without anybody having to retype it. After the read, so it wins over the
        // listing focus.
        if (model.ShowNameField)
        {
            NameField.Focus();
            SelectStem();
        }
        else if (Listing.ItemCount == 0)
        {
            Listing.Focus();
        }
    }

    /// <summary>Puts the selection and the focus back after the rows were rebuilt.</summary>
    private void OnRefiltered(IReadOnlyList<EntryViewModel> keep)
    {
        if (Model is not { } model)
            return;

        foreach (var row in keep)
            Listing.SelectedItems?.Add(row);

        // Nothing survived, so start at the top -- but never in a save dialog, where selecting
        // a row puts its name in the field and would overwrite the name the application asked
        // for before the user had seen it.
        if (Listing.SelectedIndex < 0 && !model.ShowNameField && Listing.ItemCount > 0)
            Listing.SelectedIndex = 0;

        if (Listing.SelectedIndex < 0)
            return;

        // The **container**, not the list. Focusing the list itself leaves the focus outside
        // the items, and `SelectingItemsControl`'s arrow handling moves from whatever is
        // focused -- so the dialog opened with Up and Down walking the button row instead of
        // the listing, and Space pressing the Up button. Posted at Loaded priority because the
        // row does not exist until the panel has laid the new rows out.
        Dispatcher.UIThread.Post(
            () => Listing.ContainerFromIndex(Listing.SelectedIndex)?.Focus(),
            DispatcherPriority.Loaded);
    }

    /// <summary>Selects everything before the extension in the name field.</summary>
    private void SelectStem()
    {
        var name = NameField.Text ?? "";
        var dot = name.LastIndexOf('.');
        NameField.SelectionStart = 0;
        // A leading dot is part of the name rather than an extension, so `.bashrc` selects
        // whole -- the same rule the file manager's rename dialog uses.
        NameField.SelectionEnd = dot > 0 ? dot : name.Length;
    }

    /// <summary>
    /// Builds the application's extra controls.
    /// </summary>
    /// <remarks>
    /// In code rather than through an <c>ItemsControl</c> with a template selector, because
    /// <c>Wlrix.Avalonia</c> themes neither, and an unthemed control here renders as an empty
    /// rectangle with no error and nothing in the log.
    /// </remarks>
    private void BuildChoices(PickerViewModel model)
    {
        foreach (var choice in model.Choices)
        {
            if (choice.IsBoolean)
            {
                var box = new CheckBox { Content = choice.Label };
                box.IsCheckedChanged += (_, _) => choice.Checked = box.IsChecked == true;
                box.IsChecked = choice.Checked;
                ChoicesPanel.Children.Add(box);
                continue;
            }

            var combo = new ComboBox
            {
                ItemsSource = choice.Options.Select(option => option.Label).ToList(),
                SelectedIndex = Math.Max(0, choice.Options.ToList().IndexOf(choice.Selected)),
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0 && combo.SelectedIndex < choice.Options.Count)
                    choice.Selected = choice.Options[combo.SelectedIndex];
            };

            ChoicesPanel.Children.Add(new DockPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = choice.Label,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(0, 0, 6, 0),
                        [DockPanel.DockProperty] = Dock.Left,
                    },
                    combo,
                },
            });
        }
    }

    /// <summary>
    /// Says what a <c>SaveFiles</c> dialog is going to do.
    /// </summary>
    /// <remarks>
    /// It has no name field and its accept button says Save, which without a word of
    /// explanation reads as an open dialog with the wrong button on it.
    /// </remarks>
    private void DescribeSaveFiles(PickerViewModel model)
    {
        if (model.Mode != FileChooserMode.SaveFiles)
            return;
        SaveFilesNote.Text = Strings.Catalog.Format("SaveFilesNote", model.FileCount);
        SaveFilesNote.IsVisible = true;
    }

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is { } model)
            model.SetSelection([.. Listing.SelectedItems?.OfType<EntryViewModel>() ?? []]);
    }

    private void OnListingDoubleTapped(object? sender, TappedEventArgs e) => ActivateSelection();

    /// <summary>
    /// Return on the listing is the accept button, not a row opener.
    /// </summary>
    /// <remarks>
    /// One rule rather than two: the policy already opens a folder and answers with files, so
    /// Return here means exactly what pressing the button means — and with several files
    /// selected that matters, because a row opener would answer with one of them and the user
    /// would believe all of them went.
    ///
    /// <para>
    /// Handled here rather than left to the button's <c>IsDefault</c>, because a list that
    /// swallows Return would otherwise leave the key doing nothing at all in the one control
    /// the dialog opens focused.
    /// </para>
    /// </remarks>
    private void OnListingKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Model is not { } model)
            return;
        e.Handled = true;
        _ = model.AcceptAsync();
    }

    private void ActivateSelection()
    {
        if (Model is { } model && Listing.SelectedItem is EntryViewModel row)
            _ = model.ActivateAsync(row);
    }

    private void OnPathSelected(object? sender, PathSelectedEventArgs e)
    {
        if (Model is { } model && Location.TryParse($"file://{e.Path}", out var location))
            _ = model.GoAsync(location);
    }

    private void OnPlaceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (Model is { } model && PlacesList.SelectedItem is PlaceViewModel place)
            _ = model.GoAsync(place.Location);
    }

    private void OnError(string message) =>
        _ = MessageDialog.ShowAsync(this, DialogType.Error, message,
            title: Strings.Catalog.Get("ErrorTitle"));

    private async Task<bool> OnConfirmOverwrite(string name) =>
        await MessageDialog.ShowAsync(
            this,
            DialogType.Question,
            Strings.ConfirmOverwrite(name),
            buttons: DialogButtons.OkCancel,
            title: Strings.Catalog.Get("ConfirmTitle")) == DialogResult.Ok;

    /// <summary>
    /// The answer, and then the window goes.
    /// </summary>
    /// <remarks>
    /// Closing rather than exiting here: <see cref="App"/> writes the answer from
    /// <c>Closed</c>, so every way out of this dialog -- accept, Cancel, Escape, the frame's
    /// close button -- reports through one path.
    /// </remarks>
    private void OnCloseRequested(FileChooserResult? result)
    {
        Result = result;
        Close();
    }

}
