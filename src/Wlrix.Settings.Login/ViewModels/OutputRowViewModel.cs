using ReactiveUI;
using Wlrix.Settings.Login.Core;
using Wlrix.Settings.Login.Localization;
using Wlrix.Settings.Login.Services;
using Wlrix.Settings.Outputs;

namespace Wlrix.Settings.Login.ViewModels;

/// <summary>A resolution in the mode list, and what to call it. Null is "Automatic".</summary>
internal sealed record ModeChoice(GreeterMode? Value, string Display);

/// <summary>An orientation in the rotation list, by the compositor's name for it.</summary>
internal sealed record TransformChoice(string Value, string Display);

/// <summary>
/// One display on the Displays tab: a monitor that is connected now, an entry greeter.toml
/// already has for one that is not, or both.
///
/// <see cref="IsSet"/> is whether the login screen has an opinion about it at all. Off, nothing
/// is written for it and the compositor places it the way it would with no file, which is the
/// right thing for a display nobody has thought about.
/// </summary>
internal sealed class OutputRowViewModel : ViewModelBase
{
    private bool _isSet;
    private bool _enabled = true;
    private ModeChoice _mode;
    private decimal _scale = 1;
    private TransformChoice _transform;
    private decimal _x;
    private decimal _y;
    private OutputHead? _head;

    public OutputRowViewModel(string name)
    {
        Name = name;
        Modes = [Automatic];
        _mode = Automatic;
        _transform = Transforms[0];
    }

    /// <summary>The choices every display has: the compositor's names, in its order.</summary>
    public static IReadOnlyList<TransformChoice> Transforms { get; } =
        GreeterOutput.Transforms.Select(id => new TransformChoice(id, Strings.Transform(id))).ToList();

    private static ModeChoice Automatic { get; } = new(null, Strings.ModeAutomatic);

    /// <summary>The connector, e.g. <c>DP-1</c>.</summary>
    public string Name { get; }

    /// <summary>The connector and the monitor's own description, when it is connected.</summary>
    public string Title => _head is { Description.Length: > 0 } head ? $"{Name}: {head.Description}" : Name;

    /// <summary>Whether the monitor is plugged in now. One that is not can only be kept or removed.</summary>
    public bool IsConnected => _head is not null;

    public bool IsDisconnected => _head is null;

    public bool IsSet
    {
        get => _isSet;
        set => this.RaiseAndSetIfChanged(ref _isSet, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => this.RaiseAndSetIfChanged(ref _enabled, value);
    }

    /// <summary>The modes the monitor offers, after "Automatic" (its preferred mode).</summary>
    public IReadOnlyList<ModeChoice> Modes { get; private set; }

    public ModeChoice Mode
    {
        get => _mode;
        set => this.RaiseAndSetIfChanged(ref _mode, value);
    }

    public decimal Scale
    {
        get => _scale;
        set => this.RaiseAndSetIfChanged(ref _scale, value);
    }

    public TransformChoice Transform
    {
        get => _transform;
        set => this.RaiseAndSetIfChanged(ref _transform, value);
    }

    public decimal X
    {
        get => _x;
        set => this.RaiseAndSetIfChanged(ref _x, value);
    }

    public decimal Y
    {
        get => _y;
        set => this.RaiseAndSetIfChanged(ref _y, value);
    }

    /// <summary>The monitor as the compositor describes it now, or null when it is unplugged.</summary>
    public void Connect(OutputHead? head)
    {
        _head = head;
        var selected = _mode.Value;
        var modes = new List<ModeChoice> { Automatic };
        if (head is not null)
        {
            foreach (var mode in head.Modes.Select(OutputMapping.FromMode).Distinct())
                modes.Add(new ModeChoice(mode, Strings.Mode(mode)));
        }

        // A mode the file names that this monitor does not list (or that is set for one that is
        // unplugged) stays choosable, so opening the panel and pressing Apply changes nothing.
        if (selected is not null && modes.All(choice => choice.Value != selected))
            modes.Add(new ModeChoice(selected, Strings.Mode(selected)));

        Modes = modes;
        this.RaisePropertyChanged(nameof(Modes));
        Mode = modes.First(choice => choice.Value == selected);
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(IsDisconnected));
    }

    /// <summary>Takes on <paramref name="output"/>'s settings, and marks the display as set.</summary>
    public void Load(GreeterOutput output)
    {
        IsSet = true;
        Enabled = output.Enabled ?? true;
        Scale = (decimal)(output.Scale ?? 1.0);
        X = output.X ?? 0;
        Y = output.Y ?? 0;
        Transform = Transforms.FirstOrDefault(choice => choice.Value == output.Transform) ?? Transforms[0];
        _mode = new ModeChoice(output.Mode, output.Mode is { } mode ? Strings.Mode(mode) : Strings.ModeAutomatic);
        Connect(_head);
    }

    /// <summary>The <c>[[output]]</c> entry for this display, or null when it is not set.</summary>
    public GreeterOutput? ToOutput() => !IsSet
        ? null
        : new GreeterOutput(
            Name,
            Enabled ? null : false,
            Mode.Value,
            (int)X,
            (int)Y,
            Scale == 1 ? null : (double)Scale,
            Transform.Value == "normal" ? null : Transform.Value);
}
