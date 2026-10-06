using Wlrix.Settings.Displays.Models;

namespace Wlrix.Settings.Displays.Services;

/// <summary>
/// Displays that are not there: a portrait 1920×1200 and two 2560×1440 panels side by side,
/// one of them HDR- and VRR-capable. For <c>--demo</c>, which runs the panel without a
/// compositor that speaks wlr-output-management, and for the tests.
///
/// Applying behaves as the compositor does: the change lands, and a new snapshot follows.
/// </summary>
public sealed class SampleOutputFeed : IOutputFeed
{
    private OutputSnapshot _snapshot;

    public SampleOutputFeed() : this(Default()) { }

    public SampleOutputFeed(OutputSnapshot snapshot) => _snapshot = snapshot;

    public event Action<OutputSnapshot>? SnapshotReceived;
    public event Action? Unavailable;

    /// <summary>Every configuration applied, oldest first.</summary>
    public List<IReadOnlyList<OutputChange>> Applied { get; } = [];

    /// <summary>What the next <see cref="ApplyAsync"/> answers, for the tests.</summary>
    public ApplyResult NextResult { get; set; } = ApplyResult.Succeeded;

    public OutputSnapshot Current => _snapshot;

    public void Start() => SnapshotReceived?.Invoke(_snapshot);

    public Task<ApplyResult> ApplyAsync(IReadOnlyList<OutputChange> changes)
    {
        var result = NextResult;
        NextResult = ApplyResult.Succeeded;
        if (result != ApplyResult.Succeeded)
            return Task.FromResult(result);

        Applied.Add(changes);
        var heads = _snapshot.Heads.Select(head =>
        {
            if (changes.FirstOrDefault(c => c.Name == head.Name) is not { } change)
                return head;
            return head with
            {
                Enabled = change.Enabled,
                CurrentMode = change.Enabled
                    ? head.Modes.FirstOrDefault(m => m.SameAs(change.Mode)) ?? head.CurrentMode
                    : null,
                X = change.X,
                Y = change.Y,
                Transform = change.Transform,
                Scale = change.Scale,
                AdaptiveSync = change.AdaptiveSync && head.AdaptiveSyncSupported,
                HdrEnabled = change.Hdr && head.HdrSupported,
                SdrWhiteNits = change.SdrWhiteNits,
            };
        }).ToList();
        Replace(new OutputSnapshot(heads));
        return Task.FromResult(ApplyResult.Succeeded);
    }

    /// <summary>Change the displays as a hotplug would.</summary>
    public void Replace(OutputSnapshot snapshot)
    {
        _snapshot = snapshot;
        SnapshotReceived?.Invoke(snapshot);
    }

    /// <summary>Behave as if the compositor had gone away.</summary>
    public void Disconnect() => Unavailable?.Invoke();

    public void Dispose() { }

    public static OutputSnapshot Default() => new([
        Head("DP-1", "NEC Corporation", "EA244WMi", "3X100106NB", 518, 324,
            [Mode(1920, 1200, 59_950, preferred: true), Mode(1920, 1080, 60_000), Mode(1600, 1200, 60_000), Mode(1280, 1024, 75_025), Mode(1280, 1024, 60_020)],
            x: 0, y: 0, transform: OutputTransform.Rotate90),
        Head("DP-2", "NEC Corporation", "EA274WMi", "3X100514NB", 597, 336,
            [Mode(2560, 1440, 59_951, preferred: true), Mode(1920, 1080, 60_000), Mode(1920, 1080, 50_000), Mode(1280, 720, 60_000)],
            x: 1200, y: 0),
        Head("DP-3", "NEC Corporation", "EA274WMi", "3X100335NB", 597, 336,
            [Mode(2560, 1440, 59_951, preferred: true), Mode(2560, 1440, 143_912), Mode(2560, 1440, 200_010), Mode(1920, 1080, 60_000)],
            x: 3760, y: 0, vrr: true, hdr: true),
    ]);

    private static OutputMode Mode(int width, int height, int refresh, bool preferred = false) =>
        new(width, height, refresh, preferred);

    private static OutputHead Head(
        string name, string make, string model, string serial, int widthMm, int heightMm,
        IReadOnlyList<OutputMode> modes, int x, int y,
        OutputTransform transform = OutputTransform.Normal, bool vrr = false, bool hdr = false) =>
        new(name, make, model, serial, $"{make} {model} ({name})", widthMm, heightMm, modes, modes[0],
            Enabled: true, x, y, transform, Scale: 1.0, AdaptiveSync: false, AdaptiveSyncSupported: vrr,
            HdrSupported: hdr, HdrEnabled: false, SdrWhiteNits: 203);
}
