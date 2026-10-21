// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.ObjectModel;
using ReactiveUI;
using Wlrix.Settings.Power.Localization;

namespace Wlrix.Settings.Power.ViewModels;

/// <summary>A length of idle time as the lists show it; 0 is Never.</summary>
public sealed record DurationChoice(long Seconds, string Label)
{
    /// <summary>The lengths the lists offer, Never first.</summary>
    public static IReadOnlyList<long> Presets { get; } =
        [0, 60, 120, 180, 300, 600, 900, 1200, 1800, 2700, 3600];

    public static DurationChoice Of(long seconds) => new(seconds, Strings.Duration(seconds));
}

/// <summary>
/// One profile of <c>idle.toml</c> -- <c>[plugged_in]</c> or <c>[on_battery]</c> -- as one
/// column of the When Idle group: when to dim, when to turn the screen off and whether to lock
/// then, and when to sleep.
/// </summary>
public sealed class IdleProfile : ReactiveObject
{
    private readonly string _section;
    private readonly ObservableCollection<DurationChoice> _durations;
    private readonly Action<IdleProfile> _changed;
    private DurationChoice _dim;
    private DurationChoice _screenOff;
    private bool _lock = true;
    private DurationChoice _sleep;

    public IdleProfile(string section, ObservableCollection<DurationChoice> durations, Action<IdleProfile> changed)
    {
        _section = section;
        _durations = durations;
        _changed = changed;
        _dim = _screenOff = _sleep = Find(0);
    }

    public string DimKey => $"idle.{_section}.dim_after_secs";
    public string ScreenOffKey => $"idle.{_section}.screen_off_after_secs";
    public string LockKey => $"idle.{_section}.lock";
    public string SleepKey => $"idle.{_section}.suspend_after_secs";
    public IReadOnlyList<string> Keys => [DimKey, ScreenOffKey, LockKey, SleepKey];

    public DurationChoice Dim
    {
        get => _dim;
        set => Change(ref _dim, value, nameof(Dim));
    }

    public DurationChoice ScreenOff
    {
        get => _screenOff;
        set => Change(ref _screenOff, value, nameof(ScreenOff));
    }

    public bool Lock
    {
        get => _lock;
        set
        {
            if (_lock == value)
                return;
            this.RaiseAndSetIfChanged(ref _lock, value);
            _changed(this);
        }
    }

    public DurationChoice Sleep
    {
        get => _sleep;
        set => Change(ref _sleep, value, nameof(Sleep));
    }

    /// <summary>The four values, as they are written.</summary>
    public Dictionary<string, object> Values() => new()
    {
        [DimKey] = _dim.Seconds,
        [ScreenOffKey] = _screenOff.Seconds,
        [LockKey] = _lock,
        [SleepKey] = _sleep.Seconds,
    };

    /// <summary>Show these values without writing them -- a load, or a change made elsewhere.</summary>
    public void Show(long dim, long screenOff, bool locks, long sleep)
    {
        SetQuietly(ref _dim, Find(dim), nameof(Dim));
        SetQuietly(ref _screenOff, Find(screenOff), nameof(ScreenOff));
        if (_lock != locks)
        {
            _lock = locks;
            this.RaisePropertyChanged(nameof(Lock));
        }
        SetQuietly(ref _sleep, Find(sleep), nameof(Sleep));
    }

    /// <summary>Show what another profile shows.</summary>
    public void ShowLike(IdleProfile other) =>
        Show(other._dim.Seconds, other._screenOff.Seconds, other._lock, other._sleep.Seconds);

    /// <summary>Read this profile out of the daemon's values; absent keys keep what is shown.</summary>
    public void ShowFrom(IReadOnlyDictionary<string, object?> values) =>
        Show(
            Number(values, DimKey) ?? _dim.Seconds,
            Number(values, ScreenOffKey) ?? _screenOff.Seconds,
            values.TryGetValue(LockKey, out var locks) && locks is bool l ? l : _lock,
            Number(values, SleepKey) ?? _sleep.Seconds);

    private static long? Number(IReadOnlyDictionary<string, object?> values, string key) =>
        values.TryGetValue(key, out var value) ? value switch
        {
            long number => number,
            int number => number,
            uint number => number,
            _ => null,
        } : null;

    /// <summary>
    /// The choice for <paramref name="seconds"/>: a preset, or one added to the lists for a value
    /// a hand-edited file holds, so opening the window never quietly changes it.
    /// </summary>
    private DurationChoice Find(long seconds)
    {
        if (_durations.FirstOrDefault(choice => choice.Seconds == seconds) is { } found)
            return found;
        var custom = DurationChoice.Of(seconds);
        var at = _durations.TakeWhile(choice => choice.Seconds < seconds).Count();
        _durations.Insert(at, custom);
        return custom;
    }

    private void Change(ref DurationChoice field, DurationChoice? value, string property)
    {
        // A ComboBox briefly selects nothing while its items change; that is not a choice.
        if (value is null || field == value)
            return;
        this.RaiseAndSetIfChanged(ref field, value, property);
        _changed(this);
    }

    private void SetQuietly(ref DurationChoice field, DurationChoice value, string property)
    {
        if (field == value)
            return;
        field = value;
        this.RaisePropertyChanged(property);
    }
}
