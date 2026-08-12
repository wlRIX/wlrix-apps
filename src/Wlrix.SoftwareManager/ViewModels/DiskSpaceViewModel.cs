using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.SoftwareManager.Localization;
using Wlrix.SoftwareManager.Services;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The Disk Space pane: a filesystem selector, the pie, and the legend beside it. Used and Free
/// come from the filesystem; Net change and Overhead come from a pending transaction's plan and
/// are zero until there is one.
/// </summary>
public sealed class DiskSpaceViewModel : ViewModelBase
{
    private readonly IDiskSpaceProbe _probe;
    private DiskUsage _selected;
    private long _netChangeKilobytes;
    private long _overheadKilobytes;

    public DiskSpaceViewModel(IDiskSpaceProbe probe)
    {
        _probe = probe;
        _selected = new DiskUsage("/", 0, 0);
        Refresh();
    }

    /// <summary>Design-time constructor: one plausible-looking filesystem.</summary>
    public DiskSpaceViewModel() : this(new SampleProbe())
    {
    }

    /// <summary>The mounted filesystems, root first.</summary>
    public ObservableCollection<DiskUsage> Filesystems { get; } = [];

    public DiskUsage Selected
    {
        get => _selected;
        set
        {
            // The ComboBox clears its selection while its items are being replaced. Letting
            // that through would blank the pie every refresh.
            if (value is null)
                return;

            this.RaiseAndSetIfChanged(ref _selected, value);
            RaiseTotals();
        }
    }

    /// <summary>
    /// What the pending transaction would add, in kilobytes; negative for a removal. Set from
    /// the plan, and back to zero when the transaction finishes or is abandoned.
    /// </summary>
    public long NetChangeKilobytes
    {
        get => _netChangeKilobytes;
        set
        {
            this.RaiseAndSetIfChanged(ref _netChangeKilobytes, value);
            this.RaisePropertyChanged(nameof(NetChangeText));
            this.RaisePropertyChanged(nameof(HasPendingChange));
        }
    }

    /// <summary>Temporary space the transaction needs while it runs — downloads, unpacking.</summary>
    public long OverheadKilobytes
    {
        get => _overheadKilobytes;
        set
        {
            this.RaiseAndSetIfChanged(ref _overheadKilobytes, value);
            this.RaisePropertyChanged(nameof(OverheadText));
            this.RaisePropertyChanged(nameof(HasPendingChange));
        }
    }

    /// <summary>Whether the Net change and Overhead rows have anything to say yet.</summary>
    public bool HasPendingChange => _netChangeKilobytes != 0 || _overheadKilobytes != 0;

    public long TotalKilobytes => _selected.TotalKilobytes;
    public long UsedKilobytes => _selected.UsedKilobytes;

    public string UsedText => Strings.Kilobytes(_selected.UsedKilobytes);
    public string FreeText => Strings.Kilobytes(_selected.FreeKilobytes);
    public string NetChangeText => Strings.Kilobytes(_netChangeKilobytes);
    public string OverheadText => Strings.Kilobytes(_overheadKilobytes);

    /// <summary>Re-reads the mounted filesystems, keeping the selection if it is still there.</summary>
    public void Refresh()
    {
        var previous = _selected.MountPoint;
        var probed = _probe.Probe();

        Filesystems.Clear();
        foreach (var usage in probed)
            Filesystems.Add(usage);

        Selected = probed.FirstOrDefault(usage => usage.MountPoint == previous)
                   ?? probed.FirstOrDefault()
                   ?? new DiskUsage("/", 0, 0);
    }

    private void RaiseTotals()
    {
        this.RaisePropertyChanged(nameof(TotalKilobytes));
        this.RaisePropertyChanged(nameof(UsedKilobytes));
        this.RaisePropertyChanged(nameof(UsedText));
        this.RaisePropertyChanged(nameof(FreeText));
    }

    private sealed class SampleProbe : IDiskSpaceProbe
    {
        public IReadOnlyList<DiskUsage> Probe() => [new DiskUsage("/", 98_158_900, 4_799_640)];
    }
}
