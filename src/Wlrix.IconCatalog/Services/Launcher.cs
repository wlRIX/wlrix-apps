// SPDX-License-Identifier: GPL-3.0-or-later

using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wlrix.Common;
using Wlrix.Common.Desktop;
using Wlrix.IconCatalog.Localization;
using Wlrix.IconCatalog.Models;
using ZLogger;

namespace Wlrix.IconCatalog.Services;

/// <summary>Opens what an icon stands for.</summary>
/// <remarks>
/// A launcher runs its <c>Exec</c> line the way the toolchest's Applications menu did, split by
/// the spec's quoting and never handed to a shell, with a terminal around it when it asks for
/// one. A program file runs as it is. Anything else -- a document, a folder -- goes to
/// <c>xdg-open</c>, so it opens in whatever the user opens it with elsewhere.
/// </remarks>
public sealed class Launcher(RunningApps running, ILogger<Launcher> logger)
{
    /// <summary>Raised, with a message for the user, when something would not start.</summary>
    public event Action<string>? LaunchFailed;

    public void Open(CatalogIcon icon)
    {
        if (icon.Entry is { } entry)
            Launch(icon, entry);
        else if (icon.Path is { } path)
            Start(icon, icon.IsProgram ? path : "xdg-open", icon.IsProgram ? [] : [path]);
    }

    private void Launch(CatalogIcon icon, DesktopEntry entry)
    {
        var exec = entry.Exec ?? string.Empty;
        // Nothing is dropped on an icon here, so the file codes expand to nothing; %i, %c and %k
        // still mean the launcher's own icon, name and file.
        var fields = new ExecFields(Icon: entry.Icon, DisplayName: icon.Name, EntryPath: entry.FilePath);
        if (ExecParser.Parse(exec, fields) is not { } parsed
            || parsed.File.Length == 0)
        {
            Fail(icon.Name, Strings.NoRunnableCommand(exec));
            return;
        }

        var (file, args) = parsed;
        if (!entry.Terminal)
        {
            Start(icon, file, args);
            return;
        }

        if (TerminalEmulator.Resolve() is not { } terminal)
        {
            Fail(icon.Name, Strings.NoTerminalForApp);
            return;
        }

        Start(icon, terminal.File, terminal.Wrap(file, args));
    }

    private void Start(CatalogIcon icon, string file, IReadOnlyList<string> args)
    {
        try
        {
            var info = Executables.StartInfoFor(file);
            info.UseShellExecute = false;
            info.WorkingDirectory = icon.Entry?.Path is { Length: > 0 } directory && Directory.Exists(directory)
                ? directory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var arg in args)
                info.ArgumentList.Add(arg);

            var process = Process.Start(info);
            logger.ZLogInformation($"Launched '{icon.Name}': {file} (pid {process?.Id})");
            SystemSound.Play("wlrix-launch");

            // Only a launcher has a carpet to stand up; xdg-open's process says nothing about
            // whether the document's application is running.
            if (process is not null && icon.Entry is not null)
                running.TrackLaunch(icon.Key, process);
            else
                process?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            logger.ZLogError(ex, $"Failed to launch '{icon.Name}' ({file}).");
            Fail(icon.Name, Strings.CouldNotStart(icon.Name, ex.Message));
        }
    }

    private void Fail(string name, string message)
    {
        logger.ZLogWarning($"Launch of '{name}' failed: {message}");
        LaunchFailed?.Invoke(message);
    }
}
