using Avalonia.Controls;
using Avalonia.Interactivity;
using Wlrix.Settings.International.Localization;
using Wlrix.Settings.International.Services;
using Wlrix.Settings.International.ViewModels;

namespace Wlrix.Settings.International.Views;

/// <summary>What the Language dialog closes with: the language, and the formats when they differ.</summary>
public sealed record LanguageChoice(LocaleEntry Language, LocaleEntry? Formats);

/// <summary>One row of a locale list; a null entry is "Same as language".</summary>
public sealed record LocaleRow(LocaleEntry? Entry, string Text)
{
    public static LocaleRow Of(LocaleEntry entry) =>
        new(entry, entry.IsGenerated ? entry.Display : $"{entry.Display}  {Strings.NotGenerated}");
}

/// <summary>Choose the language and the formats. Closes with a <see cref="LanguageChoice"/>, or null.</summary>
public partial class LanguageDialog : Window
{
    private readonly InternationalViewModel? _model;

    public LanguageDialog() => InitializeComponent();

    public LanguageDialog(InternationalViewModel model) : this()
    {
        _model = model;
        Scope.ItemsSource = ScopeChoices.For(model.Country);
        // Narrowed to the country when there is one, as IRIX's lists were.
        Scope.SelectedIndex = 0;
        Fill();
    }

    private void OnScope(object? sender, SelectionChangedEventArgs e) => Fill();

    private void Fill()
    {
        if (_model is null)
            return;

        var country = Scope.SelectedItem is ScopeChoice { Country: { } code } ? code : null;
        var entries = _model.Locales
            .Where(locale => country is null || locale.Territory == country)
            .ToList();
        // What is chosen stays listed whatever the filter says, so it can stay selected.
        foreach (var chosen in new[] { _model.Language, _model.Formats }.OfType<LocaleEntry>())
        {
            if (!entries.Contains(chosen))
                entries.Insert(0, chosen);
        }

        var language = (Languages.SelectedItem as LocaleRow)?.Entry ?? _model.Language;
        var formats = Formats.SelectedItem is LocaleRow row ? row.Entry : _model.Formats;

        var rows = entries.Select(LocaleRow.Of).ToList();
        Languages.ItemsSource = rows;
        Languages.SelectedItem = rows.FirstOrDefault(candidate => candidate.Entry == language);

        var same = new LocaleRow(null, Strings.SameAsLanguage);
        var formatRows = rows.Prepend(same).ToList();
        Formats.ItemsSource = formatRows;
        Formats.SelectedItem = formats is null ? same : formatRows.FirstOrDefault(candidate => candidate.Entry == formats) ?? same;

        if (Languages.SelectedItem is { } selected)
            Languages.ScrollIntoView(selected);
        Update();
    }

    private void OnSelection(object? sender, SelectionChangedEventArgs e) => Update();

    /// <summary>The preview, and the note that an ungenerated locale will ask for a password.</summary>
    private void Update()
    {
        var language = (Languages.SelectedItem as LocaleRow)?.Entry;
        var formats = (Formats.SelectedItem as LocaleRow)?.Entry ?? language;
        Preview.Text = InternationalViewModel.Preview(formats);
        GenerateNote.IsVisible = new[] { language, formats }.Any(entry => entry is { IsGenerated: false });
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if ((Languages.SelectedItem as LocaleRow)?.Entry is { } language)
            Close(new LanguageChoice(language, (Formats.SelectedItem as LocaleRow)?.Entry));
        else
            Close(null);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}

/// <summary>An entry of the "Show" option menu: one country's entries, or all of them.</summary>
public sealed record ScopeChoice(string? Country, string Text)
{
    public override string ToString() => Text;
}

internal static class ScopeChoices
{
    /// <summary>"For Japan" first when there is a country, then "All".</summary>
    public static IReadOnlyList<ScopeChoice> For(Country? country) =>
        country is null
            ? [new ScopeChoice(null, Strings.AllEntries)]
            : [new ScopeChoice(country.Code, Strings.ForCountry(country.Name)), new ScopeChoice(null, Strings.AllEntries)];
}
