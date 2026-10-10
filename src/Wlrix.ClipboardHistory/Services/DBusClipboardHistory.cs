// SPDX-License-Identifier: GPL-3.0-or-later

using Tmds.DBus.Protocol;
using Wlrix.ClipboardHistory.DBus;
using Wlrix.ClipboardHistory.Models;

namespace Wlrix.ClipboardHistory.Services;

/// <summary>
/// <see cref="IClipboardHistory"/> over <c>com.wlrix.Clipboard</c> on the session bus.
/// </summary>
/// <remarks>
/// The daemon is started by <c>wlrix-session</c>, not by bus activation: it has to be running
/// all session to record anything, so there is nothing for activation to add. When it is not on
/// the bus the first call fails, and the window says so.
/// </remarks>
public sealed class DBusClipboardHistory : IClipboardHistory
{
    /// <summary>The daemon's well-known name.</summary>
    public const string BusName = "com.wlrix.Clipboard";

    /// <summary>The object the history is served at.</summary>
    public const string ObjectPath = "/com/wlrix/Clipboard";

    private readonly DBusConnection _connection;
    private readonly Clipboard1 _proxy;
    private IDisposable? _watch;

    private DBusClipboardHistory(DBusConnection connection)
    {
        _connection = connection;
        _proxy = new Clipboard1(connection, BusName, ObjectPath);
    }

    /// <inheritdoc/>
    public event Action? Changed;

    /// <summary>
    /// Connect to the session bus and start listening for changes.
    /// </summary>
    /// <remarks>
    /// Throws when there is no session bus. Whether the daemon answers is only found out on the
    /// first call.
    /// </remarks>
    public static async Task<DBusClipboardHistory> ConnectAsync()
    {
        var address = DBusAddress.Session
            ?? throw new InvalidOperationException("no session bus to connect to");
        var connection = new DBusConnection(new DBusConnectionOptions(address) { AutoConnect = false });
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            var history = new DBusClipboardHistory(connection);
            history._watch = await history._proxy
                .WatchHistoryChangedAsync(() => history.Changed?.Invoke(), emitOnCapturedContext: false)
                .ConfigureAwait(false);
            return history;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ClipboardEntry>> GetHistoryAsync()
    {
        var rows = await _proxy.GetHistoryAsync().ConfigureAwait(false);
        return rows
            .Select(row => new ClipboardEntry(
                row.Item1,
                row.Item2 == "image" ? EntryKind.Image : EntryKind.Text,
                row.Item3,
                row.Item4,
                row.Item5,
                DateTimeOffset.FromUnixTimeSeconds(row.Item6)))
            .ToList();
    }

    /// <inheritdoc/>
    public async Task<ulong?> GetActiveAsync()
    {
        var id = await _proxy.GetActiveAsync().ConfigureAwait(false);
        return id == 0 ? null : id;
    }

    /// <inheritdoc/>
    public Task ActivateAsync(ulong id) => _proxy.ActivateAsync(id);

    /// <inheritdoc/>
    public Task SetTextAsync(ulong id, string text) => _proxy.SetTextAsync(id, text);

    /// <inheritdoc/>
    public Task RemoveAsync(ulong id) => _proxy.RemoveAsync(id);

    /// <inheritdoc/>
    public Task SetStarredAsync(ulong id, bool starred) => _proxy.SetStarredAsync(id, starred);

    /// <inheritdoc/>
    public Task ClearAsync() => _proxy.ClearAsync();

    public void Dispose()
    {
        _watch?.Dispose();
        _watch = null;
        _connection.Dispose();
    }
}
