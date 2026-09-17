using Microsoft.Extensions.Logging;
using Wlrix.Files.Core.Config;
using Wlrix.Files.Core.State;
using Wlrix.Settings.Client;
using ZLogger;

namespace Wlrix.Files.Services;

/// <summary>
/// The settings that live in <c>files.toml</c>: read from the file, written through the daemon.
/// </summary>
/// <remarks>
/// The two halves are deliberately different mechanisms, and that is the whole stack's
/// convention rather than an inconsistency here. <b>Reading</b> is a file read, so a session
/// with no <c>wlrix-settings-daemon</c> running is an ordinary session — nothing in wlRIX
/// depends on the daemon to start. <b>Writing</b> goes through it, because it is the only
/// writer: it seeds a new user file from <c>/etc/wlrix</c> so one key does not discard every
/// system default, it validates the result with this program's own
/// <c>--check-config</c> before renaming it into place, and it preserves the comments somebody
/// put in the file by hand.
///
/// <para>
/// A write the daemon cannot take is reported and not applied. Pretending otherwise would
/// leave a menu with a tick beside a setting that is not in any file, and the next window to
/// open would disagree with this one.
/// </para>
/// </remarks>
public sealed class FilesSettings(ILogger<FilesSettings> logger) : IDisposable
{
    private const string ModeKey = "files.navigation.mode";
    private const string IconThemeKey = "files.appearance.icon_theme";

    private SettingsClient? _client;

    /// <summary>What the file says now.</summary>
    public FilesConfig Current { get; private set; } = FilesConfig.Load();

    /// <summary>Raised after the file has been re-read, on whatever thread noticed.</summary>
    public event Action? Changed;

    /// <summary>Re-reads the file. What <c>SIGHUP</c> means.</summary>
    /// <remarks>
    /// The daemon signals after it has renamed the new file into place, so by the time this
    /// runs the file on disk is the new one. Re-reading it rather than taking the values from
    /// the signal is what keeps a hand-edit and a panel write the same code path.
    /// </remarks>
    public void Reload()
    {
        var reloaded = FilesConfig.Load();
        if (reloaded == Current)
            return;

        Current = reloaded;
        Changed?.Invoke();
    }

    /// <summary>Asks the daemon to write the navigation mode.</summary>
    public Task<string?> SetNavigationModeAsync(NavigationMode mode) =>
        WriteAsync(ModeKey, mode.ToString().ToLowerInvariant());

    /// <summary>Asks the daemon to write the icon theme.</summary>
    public Task<string?> SetIconThemeAsync(string theme) => WriteAsync(IconThemeKey, theme);

    /// <summary>
    /// Writes one setting, answering null or a reason it could not be written.
    /// </summary>
    /// <remarks>
    /// The daemon is bus-activated, so the first write starts it. A machine where it is not
    /// installed answers with the bus error, which is the honest report — the alternative
    /// would be writing the file here and losing the seeding, the validation and the comments.
    /// </remarks>
    private async Task<string?> WriteAsync(string key, string value)
    {
        try
        {
            _client ??= await SettingsClient.ConnectAsync().ConfigureAwait(false);
            var result = await _client.SetAsync(key, value).ConfigureAwait(false);

            // Not waiting for the SIGHUP to come back around: the value is on disk by the time
            // Set answers, and a menu that only ticked once a signal had been delivered would
            // look broken for as long as the round trip took.
            Reload();

            // "Saved, but nothing picked it up" is not a failure and must not read as one. It
            // happens to be impossible for these two — this process is the owner and is
            // plainly running — but the daemon answers it for every key and swallowing the
            // answer here would hide a real one later.
            if (result.Advice is { } advice)
                logger.ZLogInformation($"{key}: {advice}");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.ZLogWarning($"could not write {key}: {ex.Message}");
            return ex.Message;
        }
    }

    public void Dispose()
    {
        _client?.Dispose();
        _client = null;
    }
}
