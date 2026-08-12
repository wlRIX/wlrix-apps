using System.Runtime.CompilerServices;
using ReactiveUI;
using Wlrix.SoftwareManager.Services;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The Panes menu: one checkable item per region of the window. Every change is written
/// straight back to the <see cref="IPaneLayoutStore"/>, so the layout survives a restart
/// without anyone having to remember to save it.
/// </summary>
public sealed class PanesViewModel : ViewModelBase
{
    private readonly IPaneLayoutStore _store;
    private bool _availableSoftware;
    private bool _softwareInventory;
    private bool _statusDiskSpace;
    private bool _command;
    private bool _log;
    private bool _loading;

    public PanesViewModel(IPaneLayoutStore store)
    {
        _store = store;

        // Straight to the fields: going through the properties would have each one save the
        // half-restored layout on its way past.
        _loading = true;
        var layout = store.Load();
        _availableSoftware = layout.AvailableSoftware;
        _softwareInventory = layout.SoftwareInventory;
        _statusDiskSpace = layout.StatusDiskSpace;
        _command = layout.Command;
        _log = layout.Log;
        _loading = false;
    }

    /// <summary>Design-time constructor: everything showing, nothing persisted.</summary>
    public PanesViewModel() : this(new NullPaneLayoutStore())
    {
    }

    public bool AvailableSoftware
    {
        get => _availableSoftware;
        set => Set(ref _availableSoftware, value);
    }

    public bool SoftwareInventory
    {
        get => _softwareInventory;
        set => Set(ref _softwareInventory, value);
    }

    public bool StatusDiskSpace
    {
        get => _statusDiskSpace;
        set => Set(ref _statusDiskSpace, value);
    }

    public bool Command
    {
        get => _command;
        set => Set(ref _command, value);
    }

    public bool Log
    {
        get => _log;
        set => Set(ref _log, value);
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        this.RaiseAndSetIfChanged(ref field, value, name);
        if (!_loading)
            _store.Save(Snapshot());
    }

    private PaneLayout Snapshot() => new()
    {
        AvailableSoftware = _availableSoftware,
        SoftwareInventory = _softwareInventory,
        StatusDiskSpace = _statusDiskSpace,
        Command = _command,
        Log = _log,
    };

    /// <summary>For the XAML previewer, which has no business writing to the user's home directory.</summary>
    private sealed class NullPaneLayoutStore : IPaneLayoutStore
    {
        public PaneLayout Load() => new();

        public void Save(PaneLayout layout)
        {
        }
    }
}
