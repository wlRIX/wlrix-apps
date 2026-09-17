using System.Collections.Concurrent;
using Wlrix.Files.Core.Filesystems;

namespace Wlrix.Files.Core;

/// <summary>Builds an <see cref="IFileSystem"/> for a mount key that has none yet.</summary>
/// <remarks>
/// The seam remote backends register through, so that <see cref="FileSystemProvider"/>
/// itself never has to know about SMB, FTP or SFTP — and so that Core keeps building
/// with no protocol libraries referenced at all.
/// </remarks>
public interface IFileSystemFactory
{
    /// <summary>The scheme this handles, lowercased.</summary>
    string Scheme { get; }

    /// <summary>Creates a filesystem for one mount. Called once per mount key.</summary>
    Task<IFileSystem> CreateAsync(Location location, CancellationToken cancellationToken);
}

/// <summary>Hands out the filesystem serving a location.</summary>
/// <remarks>
/// An interface so the operations engine depends on the capability rather than on
/// <see cref="FileSystemProvider"/> itself, which lets a test hand it an in-memory
/// filesystem for <c>file://</c> where the real provider always serves the disk.
/// </remarks>
public interface IFileSystemProvider
{
    /// <summary>The filesystem serving a location, creating and connecting it if needed.</summary>
    Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken);
}

/// <summary>
/// Hands out the one <see cref="IFileSystem"/> serving each mount.
/// </summary>
/// <remarks>
/// One instance per <see cref="Location.MountKey"/> is a hard rule, not a cache
/// optimization: a remote filesystem owns a connection and serializes its operations
/// on it, so a second instance for the same host would open a redundant session and
/// silently lose that serialization.
///
/// <para>
/// Local is always present and needs no factory. Remote schemes register one.
/// </para>
/// </remarks>
public sealed class FileSystemProvider : IFileSystemProvider, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Task<IFileSystem>> _instances = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IFileSystemFactory> _factories = new(StringComparer.Ordinal);

    public FileSystemProvider(IEnumerable<IFileSystemFactory>? factories = null)
    {
        _instances[LocalFileSystem.LocalMountKey] = Task.FromResult<IFileSystem>(new LocalFileSystem());
        foreach (var factory in factories ?? [])
            _factories[factory.Scheme] = factory;
    }

    /// <summary>The filesystem serving a location, creating and connecting it if needed.</summary>
    /// <exception cref="FileOperationException">
    /// No factory is registered for the scheme.
    /// </exception>
    public Task<IFileSystem> GetAsync(Location location, CancellationToken cancellationToken)
    {
        // GetOrAdd can run the factory more than once under contention, so the *task*
        // is cached rather than the instance: every caller then awaits the same
        // connection attempt instead of racing to open several.
        return _instances.GetOrAdd(location.MountKey, _ => CreateAsync(location, cancellationToken));
    }

    private async Task<IFileSystem> CreateAsync(Location location, CancellationToken cancellationToken)
    {
        if (!_factories.TryGetValue(location.Scheme, out var factory))
            throw new FileOperationException(FileErrorKind.Unsupported, location,
                $"no filesystem is registered for '{location.Scheme}'");
        return await factory.CreateAsync(location, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops a mount, disposing it. Used when a share is disconnected.</summary>
    public async ValueTask RemoveAsync(string mountKey)
    {
        if (mountKey == LocalFileSystem.LocalMountKey || !_instances.TryRemove(mountKey, out var task))
            return;
        await DisposeOneAsync(task).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        foreach (var task in _instances.Values)
            await DisposeOneAsync(task).ConfigureAwait(false);
        _instances.Clear();
    }

    private static async ValueTask DisposeOneAsync(Task<IFileSystem> task)
    {
        try
        {
            var fs = await task.ConfigureAwait(false);
            await fs.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileOperationException or OperationCanceledException)
        {
            // A mount that never finished connecting has nothing to dispose, and
            // shutdown is no time to raise the failure again.
        }
    }
}
