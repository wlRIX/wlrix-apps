using System.Security.Cryptography;
using System.Text;
using Wlrix.Packages.Processes;

namespace Wlrix.Packages.AppStream;

/// <summary>
/// Turns JPEG-XL icons into PNG files in a per-user cache.
///
/// Arch's catalog ships nothing but JPEG-XL, and SkiaSharp cannot decode it, so <c>djxl</c> does.
/// Each icon is converted once and only when a row needs it, and at most two at a time. An icon
/// that cannot be converted, because <c>djxl</c> is not installed for one, is a row with no icon.
/// </summary>
public sealed class JxlIconConverter(IProcessRunner runner, string cacheDirectory)
{
    private readonly SemaphoreSlim _running = new(2);

    /// <summary>The icon as a PNG file, or null if it could not be had.</summary>
    public async Task<string?> ToPngAsync(string source, CancellationToken cancellationToken = default)
    {
        var target = Path.Combine(cacheDirectory, NameFor(source));

        await _running.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Older than the source means the catalog package was upgraded since.
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source))
                return target;

            Directory.CreateDirectory(cacheDirectory);

            // A partial file must never sit at the name that means "already converted".
            var partial = $"{target}.{Environment.ProcessId}.tmp.png";
            try
            {
                var result = await runner.RunAsync("djxl", [source, partial, "--quiet"], cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Succeeded || !File.Exists(partial))
                    return null;

                File.Move(partial, target, overwrite: true);
                return target;
            }
            finally
            {
                File.Delete(partial);
            }
        }
        catch (Exception ex) when (ex is PackageProcessException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            _running.Release();
        }
    }

    // Hashed, since the same file name is in every size directory of every origin.
    private static string NameFor(string source) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..32] + ".png";
}
