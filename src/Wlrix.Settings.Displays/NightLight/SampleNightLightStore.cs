namespace Wlrix.Settings.Displays.NightLight;

/// <summary>
/// Night light settings kept in memory and applied to nothing. For <c>--demo</c>, the designer
/// and the tests, as <c>SampleOutputFeed</c> is for the displays.
/// </summary>
public sealed class SampleNightLightStore(NightLightSettings? settings = null) : INightLightStore
{
    private NightLightSettings _current = settings ?? NightLightSettings.Default;

    public event Action<NightLightSettings>? Changed;

    /// <summary>Everything saved, oldest first.</summary>
    public List<NightLightSettings> Saved { get; } = [];

    /// <summary>What <see cref="LoadAsync"/> fails with, for the tests. Null loads normally.</summary>
    public Exception? LoadFailure { get; init; }

    /// <summary>What the next <see cref="SaveAsync"/> fails with, for the tests.</summary>
    public Exception? NextSaveFailure { get; set; }

    public Task<NightLightState> LoadAsync() => LoadFailure is { } failure
        ? Task.FromException<NightLightState>(failure)
        : Task.FromResult(new NightLightState(_current, 1000, 6500));

    public Task<string?> SaveAsync(NightLightSettings settings)
    {
        if (NextSaveFailure is { } failure)
        {
            NextSaveFailure = null;
            return Task.FromException<string?>(failure);
        }
        _current = settings;
        Saved.Add(settings);
        return Task.FromResult<string?>(null);
    }

    /// <summary>Something other than this panel changed the settings.</summary>
    public void ChangeElsewhere(NightLightSettings settings)
    {
        _current = settings;
        Changed?.Invoke(settings);
    }

    public void Dispose() { }
}
