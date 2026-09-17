using System.Text.Json;
using Wlrix.Files.Core.Portal;

namespace Wlrix.FilePicker.Services;

/// <summary>Reads the portal's question off stdin.</summary>
public static class RequestReader
{
    /// <summary>Read the whole of stdin and parse it.</summary>
    /// <remarks>
    /// To the end of the stream rather than a line: the portal writes the request and closes
    /// the pipe, and that close is what says it is complete. Waiting for a newline would hang
    /// on a request that does not end with one.
    /// </remarks>
    public static FileChooserRequest Read(Stream input)
    {
        using var reader = new StreamReader(input);
        var json = reader.ReadToEnd();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException(
                "no request on stdin; this program is run by xdg-desktop-portal-wlrix, not by hand");
        }

        return JsonSerializer.Deserialize(json, FileChooserJson.Default.FileChooserRequest)
               ?? throw new InvalidOperationException("the request was empty");
    }
}
