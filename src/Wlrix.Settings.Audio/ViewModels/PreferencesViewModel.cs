// SPDX-License-Identifier: GPL-3.0-or-later

using ReactiveUI;
using Wlrix.Settings.Audio.Models;

namespace Wlrix.Settings.Audio.ViewModels;

/// <summary>
/// A device's Preferences: what it is, and its card's profile. The profile is the one thing
/// here that can change, and it changes as soon as it is picked, like everything else in the
/// panel.
/// </summary>
public sealed class PreferencesViewModel : ViewModelBase
{
    private readonly AudioCard? _card;
    private readonly Action<AudioCard, string> _setProfile;
    private CardProfile? _profile;

    public PreferencesViewModel(DeviceColumnViewModel column, AudioCard? card,
        Action<AudioCard, string> setProfile)
    {
        _card = card;
        _setProfile = setProfile;
        Title = column.Title;
        Name = column.Device.Name;
        Driver = column.Device.Driver ?? "";
        Connection = column.BusText;
        Profiles = card?.Profiles.Where(p => p.Available || p.Name == card.ActiveProfile).ToList() ?? [];
        _profile = Profiles.FirstOrDefault(p => p.Name == card?.ActiveProfile);
    }

    public string Title { get; }
    public string Name { get; }
    public string Driver { get; }
    public string Connection { get; }
    public bool HasCard => _card is not null;
    public bool HasNoCard => _card is null;
    public IReadOnlyList<CardProfile> Profiles { get; }

    public CardProfile? Profile
    {
        get => _profile;
        set
        {
            if (value is null || value == _profile)
                return;
            this.RaiseAndSetIfChanged(ref _profile, value);
            if (_card is not null)
                _setProfile(_card, value.Name);
        }
    }
}
