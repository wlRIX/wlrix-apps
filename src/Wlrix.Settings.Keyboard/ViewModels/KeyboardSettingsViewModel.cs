using Avalonia.Threading;
using ReactiveUI;
using Wlrix.Settings.Keyboard.Models;
using Wlrix.Settings.Keyboard.Services;

namespace Wlrix.Settings.Keyboard.ViewModels;

/// <summary>
/// Backs the keyboard settings window. Each change is committed — written to
/// <c>compositor.toml</c> and applied to the running compositor with <c>SIGHUP</c> — shortly
/// after the user stops adjusting, so the Test box reflects it live. Slider drags are
/// debounced so a drag writes once, on release, rather than on every tick. <see cref="Reset"/>
/// puts everything back to how it was when the window opened and commits that.
/// </summary>
public sealed class KeyboardSettingsViewModel : ViewModelBase, IDisposable
{
    private const int DefaultRate = 25;

    private readonly DispatcherTimer _commitTimer;

    // The state captured when the window opened, for Reset.
    private readonly bool _openEnabled;
    private readonly double _openRate;
    private readonly double _openDelay;
    private readonly string _openLayout;
    private readonly string _openModel;

    // True while the constructor or Reset sets values in bulk, so those do not each commit.
    private bool _loading;

    private bool _repeatEnabled;
    private double _repeatRate;
    private double _repeatDelay;
    private XkbEntry _selectedLayout;
    private XkbEntry _selectedModel;

    public KeyboardSettingsViewModel()
    {
        var initial = CompositorConfig.Read();
        var (layouts, models) = XkbCatalog.Load();
        Layouts = EnsureContains(layouts, initial.Layout);
        Models = EnsureContains(models, initial.Model);

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _commitTimer.Tick += OnCommitTick;

        _loading = true;
        _repeatEnabled = initial.RepeatRate != 0;
        _repeatRate = initial.RepeatRate != 0 ? initial.RepeatRate : DefaultRate;
        _repeatDelay = initial.RepeatDelay;
        _selectedLayout = FindByCode(Layouts, initial.Layout);
        _selectedModel = FindByCode(Models, initial.Model);
        _loading = false;

        _openEnabled = _repeatEnabled;
        _openRate = _repeatRate;
        _openDelay = _repeatDelay;
        _openLayout = _selectedLayout.Code;
        _openModel = _selectedModel.Code;
    }

    public IReadOnlyList<XkbEntry> Layouts { get; }

    public IReadOnlyList<XkbEntry> Models { get; }

    /// <summary>Whether keys repeat when held. Off writes a repeat rate of 0.</summary>
    public bool RepeatEnabled
    {
        get => _repeatEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatEnabled, value);
            ScheduleCommit();
        }
    }

    /// <summary>Repeats per second, 1–100.</summary>
    public double RepeatRate
    {
        get => _repeatRate;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatRate, value);
            this.RaisePropertyChanged(nameof(RepeatRateText));
            ScheduleCommit();
        }
    }

    /// <summary>Milliseconds before a held key starts repeating, 100–1000.</summary>
    public double RepeatDelay
    {
        get => _repeatDelay;
        set
        {
            this.RaiseAndSetIfChanged(ref _repeatDelay, value);
            this.RaisePropertyChanged(nameof(RepeatDelayText));
            ScheduleCommit();
        }
    }

    public XkbEntry SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLayout, value);
            ScheduleCommit();
        }
    }

    public XkbEntry SelectedModel
    {
        get => _selectedModel;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedModel, value);
            ScheduleCommit();
        }
    }

    /// <summary>The current repeat speed, for the slider's tooltip.</summary>
    public string RepeatRateText => ((int)Math.Round(RepeatRate)).ToString();

    /// <summary>The current repeat delay, for the slider's tooltip.</summary>
    public string RepeatDelayText => $"{(int)Math.Round(RepeatDelay)} ms";

    /// <summary>Restore the settings to how they were when the window opened, and apply them.</summary>
    public void Reset()
    {
        _loading = true;
        RepeatEnabled = _openEnabled;
        RepeatRate = _openRate;
        RepeatDelay = _openDelay;
        SelectedLayout = FindByCode(Layouts, _openLayout);
        SelectedModel = FindByCode(Models, _openModel);
        _loading = false;
        Commit();
    }

    public void Dispose()
    {
        // Flush a change still waiting out its debounce, so closing right after an adjustment
        // does not drop it.
        var pending = _commitTimer.IsEnabled;
        _commitTimer.Stop();
        _commitTimer.Tick -= OnCommitTick;
        if (pending)
            Commit();
    }

    private void OnCommitTick(object? sender, EventArgs e)
    {
        _commitTimer.Stop();
        Commit();
    }

    // Coalesce a burst of changes (a slider drag) into one write shortly after it settles.
    private void ScheduleCommit()
    {
        if (_loading)
            return;
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private void Commit()
    {
        var settings = new KeyboardSettings
        {
            Layout = _selectedLayout.Code,
            Model = _selectedModel.Code,
            RepeatDelay = (int)Math.Round(_repeatDelay),
            RepeatRate = _repeatEnabled ? (int)Math.Round(_repeatRate) : 0,
        };

        try
        {
            CompositorConfig.Write(settings);
            CompositorControl.Reload();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort: if the config cannot be written, the UI still reflects the choice;
            // there is nothing useful to do here but leave it unapplied.
        }
    }

    // Guarantee the current code is selectable even if the catalog does not list it (a hand-set
    // or comma-separated layout like "jp,us"), so opening never silently drops it.
    private static IReadOnlyList<XkbEntry> EnsureContains(IReadOnlyList<XkbEntry> entries, string code)
    {
        if (entries.Any(entry => entry.Code == code))
            return entries;
        return [new XkbEntry(code, code), .. entries];
    }

    private static XkbEntry FindByCode(IReadOnlyList<XkbEntry> entries, string code) =>
        entries.FirstOrDefault(entry => entry.Code == code) ?? entries[0];
}
