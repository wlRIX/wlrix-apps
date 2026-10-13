// SPDX-License-Identifier: GPL-3.0-or-later

using Wlrix.Writer.Models;

namespace Wlrix.Writer.Services;

/// <summary>What a file looked like on disk the last time the editor read or wrote it.</summary>
/// <remarks>
/// The change-on-disk check compares against this, which is what keeps the editor's own saves
/// from coming back as "the file has changed": the save records the stamp of what it wrote.
/// </remarks>
public readonly record struct DiskStamp(DateTime LastWriteUtc, long Length);

/// <summary>A file read into the editor.</summary>
public sealed record LoadedFile(string Text, TextFormat Format, DiskStamp Stamp);

/// <summary>Reading and writing the files behind documents.</summary>
public static class DocumentIO
{
    /// <summary>The stamp of the file at <paramref name="path"/>, or null if there is none.</summary>
    public static DiskStamp? Probe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new DiskStamp(info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Reads a text file.</summary>
    /// <exception cref="BinaryFileException">The file is not text.</exception>
    /// <exception cref="IOException">The file could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be read.</exception>
    public static LoadedFile Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var decoded = TextFile.Decode(bytes);
        var stamp = Probe(path) ?? new DiskStamp(DateTime.MinValue, bytes.Length);
        return new LoadedFile(decoded.Text, decoded.Format, stamp);
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> in <paramref name="format"/>.</summary>
    /// <returns>The new stamp, and the format actually written (see <see cref="TextFile.Encode"/>).</returns>
    /// <remarks>
    /// <para>
    /// Written to a temporary file beside the target and renamed over it, so a crash or a full
    /// disk halfway through leaves the old file whole rather than a truncated new one. The
    /// original's permission bits are copied to the replacement first; a rename keeps nothing of
    /// the file it replaces.
    /// </para>
    /// <para>
    /// A symbolic link is followed and its target replaced, so the link survives: renaming over
    /// the link itself would turn it into a regular file and leave the real one untouched.
    /// </para>
    /// <para>
    /// Where the directory cannot take a new file but the file itself is writable (a
    /// group-writable file in a directory owned by somebody else), the write falls back to
    /// rewriting the file in place, which is all the user would get from any other editor.
    /// </para>
    /// </remarks>
    public static (DiskStamp Stamp, TextFormat Format) Save(string path, string text, TextFormat format)
    {
        var (bytes, written) = TextFile.Encode(text, format);
        var target = ResolveLinks(path);

        if (!TryReplace(target, bytes))
            WriteInPlace(target, bytes);

        return (Probe(target) ?? new DiskStamp(DateTime.UtcNow, bytes.Length), written);
    }

    /// <summary>The file a path finally names, through any chain of symbolic links.</summary>
    public static string ResolveLinks(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            var info = new FileInfo(full);
            if (info.LinkTarget is null)
                return full;
            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? full;
        }
        catch (IOException)
        {
            // A loop, or a link that cannot be read. Writing to the path as given at least
            // reports a real error rather than this one.
            return full;
        }
    }

    private static bool TryReplace(string target, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(target) ?? ".";
        var temporary = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.wlrix-writer");

        FileStream stream;
        try
        {
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        }
        catch (UnauthorizedAccessException) when (File.Exists(target))
        {
            return false;
        }

        try
        {
            using (stream)
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // The guard is for the platform analyzer; this program only runs on Linux.
            if (!OperatingSystem.IsWindows() && File.Exists(target))
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));

            File.Move(temporary, target, overwrite: true);
            return true;
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void WriteInPlace(string target, byte[] bytes)
    {
        using var stream = new FileStream(target, FileMode.Truncate, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left behind as a dot file; nothing better to do with it.
        }
    }
}
