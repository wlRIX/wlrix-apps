using Wlrix.Desks.Models;

namespace Wlrix.Desks.Services;

/// <summary>
/// An offline <see cref="IDeskFeed"/> that fabricates a plausible desk layout and cycles the
/// active desk on a timer, so the UI can be run and previewed without a live compositor
/// (selected with <c>--demo</c>, and used for the XAML design-time DataContext). Commands
/// mutate the in-memory model and re-emit, mirroring the real feed's behavior.
/// </summary>
public sealed class SampleDeskFeed : IDeskFeed
{
    private readonly Lock _gate = new();
    private readonly List<DeskInfo> _desks =
    [
        new(0, false, "Global"),
        new(1, true, "Web"),
        new(2, false, "Code"),
        new(3, false, "Mail"),
    ];

    private readonly List<WindowInfo> _windows =
    [
        new(101, 1, 100, 120, 760, 520, false, "firefox", "Mozilla Firefox"),
        new(102, 1, 900, 320, 620, 430, false, "term", "Terminal"),
        new(103, 2, 220, 150, 1040, 720, false, "code", "main.cs — Editor"),
        new(104, 3, 2000, 200, 820, 600, false, "mail", "Inbox"),
        new(105, 0, 1560, 60, 320, 210, false, "clock", "World Clock"),
        new(106, 2, 1500, 520, 520, 300, true, "files", "Files"),
    ];

    private Timer? _timer;

    public event Action<DeskSnapshot>? SnapshotReceived;

    // The sample feed never reports itself unavailable; the event satisfies the interface.
#pragma warning disable CS0067
    public event Action? Unavailable;
#pragma warning restore CS0067

    public void Start()
    {
        Emit();
        _timer = new Timer(_ => CycleActive(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public Task SwitchAsync(int id)
    {
        SetActive(id);
        return Task.CompletedTask;
    }

    public Task CreateAsync(string name)
    {
        lock (_gate)
        {
            var id = _desks.Count == 0 ? 1 : _desks.Max(d => d.Id) + 1;
            _desks.Add(new DeskInfo(id, false, name));
        }

        Emit();
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int id)
    {
        lock (_gate)
        {
            _desks.RemoveAll(d => d.Id == id);
            // Windows on the removed desk migrate to the Global desk, matching the compositor.
            for (var i = 0; i < _windows.Count; i++)
                if (_windows[i].DeskId == id)
                    _windows[i] = _windows[i] with { DeskId = 0 };
        }

        Emit();
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void CycleActive()
    {
        lock (_gate)
        {
            var switchable = _desks.Where(d => d.Id != 0).Select(d => d.Id).ToList();
            if (switchable.Count == 0)
                return;

            var current = _desks.FindIndex(d => d.Active);
            var currentId = current >= 0 ? _desks[current].Id : switchable[0];
            var pos = switchable.IndexOf(currentId);
            var next = switchable[(pos + 1) % switchable.Count];
            SetActiveLocked(next);
        }

        Emit();
    }

    private void SetActive(int id)
    {
        lock (_gate)
            SetActiveLocked(id);
        Emit();
    }

    private void SetActiveLocked(int id)
    {
        for (var i = 0; i < _desks.Count; i++)
            _desks[i] = _desks[i] with { Active = _desks[i].Id == id };
    }

    private void Emit()
    {
        DeskSnapshot snapshot;
        lock (_gate)
            snapshot = new DeskSnapshot(_desks.ToList(), _windows.ToList());
        SnapshotReceived?.Invoke(snapshot);
    }
}
