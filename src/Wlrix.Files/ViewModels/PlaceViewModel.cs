using Wlrix.Files.Core;

namespace Wlrix.Files.ViewModels;

/// <summary>One entry in the sidebar's Places section.</summary>
public sealed class PlaceViewModel(string label, Location location)
{
    public string Label { get; } = label;

    public Location Location { get; } = location;

    /// <summary>The label. This appears in the Internet menu as a MenuItem header.</summary>
    public override string ToString() => Label;
}
