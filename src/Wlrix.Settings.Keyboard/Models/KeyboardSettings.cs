namespace Wlrix.Settings.Keyboard.Models;

/// <summary>
/// The keyboard settings the app manages, mirroring the compositor's <c>[keyboard]</c> table.
/// These are the only keys the app writes; anything else in the section (variant, options,
/// rules) is preserved untouched.
/// </summary>
public sealed record KeyboardSettings
{
    /// <summary>The xkb layout, e.g. <c>jp</c>. A comma-separated list is kept verbatim.</summary>
    public string Layout { get; init; } = "us";

    /// <summary>The xkb model, e.g. <c>jp106</c>.</summary>
    public string Model { get; init; } = "pc105";

    /// <summary>Milliseconds a key is held before it repeats. 100–1000.</summary>
    public int RepeatDelay { get; init; } = 200;

    /// <summary>Repeats per second once repetition starts. 1–100, or 0 to disable repeat.</summary>
    public int RepeatRate { get; init; } = 25;
}
