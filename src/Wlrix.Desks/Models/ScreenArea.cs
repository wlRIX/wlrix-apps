using Avalonia;

namespace Wlrix.Desks.Models;

/// <summary>One monitor: where it is in the screen layout, and its connector name.</summary>
/// <param name="Name">The connector name, e.g. <c>DP-1</c>, which <c>background.toml</c>'s
/// <c>[[output]]</c> sections are keyed by; null when the compositor gave none.</param>
public sealed record ScreenArea(Rect Bounds, string? Name);
