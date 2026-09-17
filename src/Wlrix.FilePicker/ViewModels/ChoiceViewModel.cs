using ReactiveUI;
using Wlrix.Files.Core.Portal;

namespace Wlrix.FilePicker.ViewModels;

/// <summary>One extra control the application asked the dialog to carry.</summary>
/// <remarks>
/// Two shapes behind one type, because the interface has two: a choice with options is a combo
/// box, and a choice with none is a checkbox whose value is the string <c>"true"</c> or
/// <c>"false"</c>. The view binds both and shows whichever applies; putting the distinction in
/// the view model keeps it out of two templates that could disagree.
/// </remarks>
public sealed class ChoiceViewModel : ViewModelBase
{
    private ChoiceOption _selected;
    private bool _checked;

    public ChoiceViewModel(FileChooserChoice choice)
    {
        Choice = choice;
        Options = choice.Options;

        if (choice.IsBoolean)
        {
            _checked = choice.Default == "true";
            return;
        }

        // The application's default if it named one that exists, else the first option. An
        // application that named nothing still gets a combo box showing something, because a
        // combo box showing nothing has no value to answer with.
        var index = Options.ToList().FindIndex(option => option.Id == choice.Default);
        _selected = Options.Count == 0 ? default : Options[index >= 0 ? index : 0];
    }

    public FileChooserChoice Choice { get; }

    public string Label => Choice.Label;

    public IReadOnlyList<ChoiceOption> Options { get; }

    public bool IsBoolean => Choice.IsBoolean;

    public ChoiceOption Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    public bool Checked
    {
        get => _checked;
        set => this.RaiseAndSetIfChanged(ref _checked, value);
    }

    /// <summary>What goes back to the application.</summary>
    public ChoiceAnswer Answer() =>
        new(Choice.Id, IsBoolean ? (Checked ? "true" : "false") : Selected.Id);
}
