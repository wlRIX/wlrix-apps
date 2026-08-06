using System.Text.Json;
using Wlrix.SourcePicker.Models;

namespace Wlrix.SourcePicker.Services;

/// <summary>Reads the portal's question off stdin.</summary>
public static class ManifestReader
{
    /// <summary>
    /// Read the whole of stdin and parse it.
    /// </summary>
    /// <remarks>
    /// To the end of the stream rather than a line: the portal writes the manifest and closes
    /// the pipe, and that close is what says the list is complete. Waiting for a newline would
    /// hang on a manifest that does not end with one.
    /// </remarks>
    public static Manifest Read(Stream input)
    {
        using var reader = new StreamReader(input);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                "no manifest on stdin; this program is run by xdg-desktop-portal-wlrix, not by hand");
        }

        return JsonSerializer.Deserialize(json, PickerJsonContext.Default.Manifest)
               ?? throw new InvalidOperationException("the manifest was empty");
    }
}
