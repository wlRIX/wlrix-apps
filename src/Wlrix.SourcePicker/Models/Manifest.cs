using System.Text.Json.Serialization;

namespace Wlrix.SourcePicker.Models;

/// <summary>
/// The question <c>xdg-desktop-portal-wlrix</c> asks, read from stdin.
/// </summary>
/// <remarks>
/// The wire format is documented in the portal backend's README, which is the authority. These
/// types must not drift from it: the two programs are released together but built separately,
/// and a mismatch shows up as a picker that offers nothing rather than as a build error.
/// </remarks>
public sealed class Manifest
{
    /// <summary>The application asking, for the dialog's wording. Often empty.</summary>
    [JsonPropertyName("app_id")]
    public string AppId { get; init; } = "";

    /// <summary>Whether more than one source may be chosen.</summary>
    [JsonPropertyName("multiple")]
    public bool Multiple { get; init; }

    /// <summary>Whether the pointer will be in the stream, which is worth telling the user.</summary>
    [JsonPropertyName("cursor")]
    public bool Cursor { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<Source> Sources { get; init; } = [];
}

/// <summary>One thing that could be shared.</summary>
public sealed class Source
{
    /// <summary>Opaque here; it goes back to the portal verbatim.</summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = "";

    /// <summary><c>monitor</c> or <c>window</c>.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary>The application, for a window. Empty for a monitor.</summary>
    [JsonPropertyName("app_id")]
    public string AppId { get; init; } = "";

    /// <summary>
    /// The source's own size. Zero for a window: the compositor only reports a window's capture
    /// size once a session is open on it, so the tile takes its shape from the preview instead.
    /// </summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary>
    /// The live thumbnail file, or null if the portal could not publish one. A source without a
    /// preview is still offered -- a tile with a label and no picture is better than a source
    /// the user cannot choose.
    /// </summary>
    [JsonPropertyName("preview")]
    public string? Preview { get; init; }

    public bool IsMonitor => Kind == "monitor";
}

/// <summary>What is written back to stdout when the user accepts.</summary>
public sealed class Selection
{
    [JsonPropertyName("sources")]
    public required IReadOnlyList<string> Sources { get; init; }
}

/// <summary>
/// Source-generated serialization, so no reflection-based JSON is pulled in.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(Selection))]
public partial class PickerJsonContext : JsonSerializerContext;
