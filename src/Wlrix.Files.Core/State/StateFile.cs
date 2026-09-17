using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Wlrix.Files.Core.State;

/// <summary>
/// Reading and writing one small JSON document, without ever losing the old one.
/// </summary>
/// <remarks>
/// Every write goes to a <c>.wlrix-new</c> beside the target and is moved into place when it
/// is complete — the same idiom the archiver uses and the operations engine generalizes. A
/// session file half-written when the compositor went down is the case this exists for: a
/// truncated JSON document is unreadable, and losing every bookmark to a crash during
/// shutdown would be a poor trade for a few bytes of tidiness.
/// </remarks>
internal static class StateFile
{
    /// <summary>The suffix an in-progress write is made under.</summary>
    public const string NewSuffix = ".wlrix-new";

    /// <summary>
    /// Reads <paramref name="path"/>, or answers <paramref name="fallback"/> if there is
    /// nothing readable there.
    /// </summary>
    /// <param name="versionOf">
    /// Pulls the document's schema version out. A version this build does not recognize is
    /// treated exactly like a corrupt file: the defaults are always a working application,
    /// and guessing at a newer schema is how a downgrade corrupts one.
    /// </param>
    public static T Load<T>(
        string path,
        JsonTypeInfo<T> typeInfo,
        T fallback,
        Func<T, int> versionOf,
        int currentVersion,
        Action<string>? warn = null)
        where T : class
    {
        if (!File.Exists(path))
            return fallback;

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), typeInfo);
            if (loaded is null)
                return fallback;
            if (versionOf(loaded) != currentVersion)
            {
                warn?.Invoke($"{path} is version {versionOf(loaded)}, not {currentVersion}; using defaults");
                return fallback;
            }
            return loaded;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not read {path}: {ex.Message}");
            return fallback;
        }
    }

    /// <summary>Writes <paramref name="value"/>, atomically. Answers whether it landed.</summary>
    /// <remarks>
    /// Failing to save is reported, never thrown. These are conveniences — which folder was
    /// showing, which sidebar entries exist — and a read-only home directory must not stop
    /// the file manager from running.
    /// </remarks>
    public static bool Save<T>(string path, T value, JsonTypeInfo<T> typeInfo, Action<string>? warn = null)
    {
        var temporary = path + NewSuffix;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, typeInfo));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warn?.Invoke($"could not write {path}: {ex.Message}");
            TryDelete(temporary);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover part file is untidy, not broken; the next save overwrites it.
        }
    }
}
