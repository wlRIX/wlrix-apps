using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ReactiveUI;
using Wlrix.Packages.UserSoftware;
using Wlrix.SoftwareManager.Localization;
using ZLogger;

namespace Wlrix.SoftwareManager.ViewModels;

/// <summary>
/// The User Software window: what the user has installed for themselves, outside the system
/// package manager. Nothing here needs root, so there is no Start button and no polkit prompt —
/// Install and Remove happen as they are pressed.
/// </summary>
public sealed class UserSoftwareViewModel : ViewModelBase
{
    private readonly IUserSoftwareSource _source;
    private readonly ILogger<UserSoftwareViewModel> _logger;

    private UserPackage? _selected;
    private string _path = string.Empty;
    private string _status = string.Empty;

    public UserSoftwareViewModel(IUserSoftwareSource source, ILogger<UserSoftwareViewModel> logger)
    {
        _source = source;
        _logger = logger;

        Install = ReactiveCommand.Create(RunInstall,
            this.WhenAnyValue(x => x.Path, path => path.Trim().Length > 0));
        // Select rather than the two-argument WhenAnyValue: with a nullable property the
        // overload that takes a selector and the one that watches two properties are ambiguous.
        Remove = ReactiveCommand.Create(RunRemove,
            this.WhenAnyValue(x => x.Selected).Select(selected => selected is not null));

        Reload();
    }

    /// <summary>Design-time constructor.</summary>
    public UserSoftwareViewModel()
        : this(new AppImageSource(NullLogger<AppImageSource>.Instance),
            NullLogger<UserSoftwareViewModel>.Instance)
    {
    }

    public ObservableCollection<UserPackage> Packages { get; } = [];

    public ReactiveCommand<Unit, Unit> Install { get; }
    public ReactiveCommand<Unit, Unit> Remove { get; }

    /// <summary>Where AppImages are kept, shown so the user knows where their files went.</summary>
    public string Directory => AppImageSource.Directory;

    public UserPackage? Selected
    {
        get => _selected;
        set => this.RaiseAndSetIfChanged(ref _selected, value);
    }

    /// <summary>The path to an AppImage to install. Filled by typing or by dropping a file.</summary>
    public string Path
    {
        get => _path;
        set => this.RaiseAndSetIfChanged(ref _path, value);
    }

    /// <summary>What happened, when it is worth saying.</summary>
    public string Status
    {
        get => _status;
        set => this.RaiseAndSetIfChanged(ref _status, value);
    }

    /// <summary>Re-reads the managed directory.</summary>
    public void Reload()
    {
        Packages.Clear();
        foreach (var package in _source.List())
            Packages.Add(package);

        if (Packages.Count == 0)
            Status = Strings.UserSoftwareEmpty(Directory);
    }

    private void RunInstall()
    {
        var path = Expand(Path.Trim());

        try
        {
            var installed = _source.Install(path);
            Path = string.Empty;
            Reload();

            Selected = Packages.FirstOrDefault(package => package.Id == installed.Id);
            Status = Strings.UserSoftwareInstalled(installed.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or FileNotFoundException or InvalidOperationException)
        {
            _logger.ZLogWarning(ex, $"Could not install {path}.");
            Status = ex.Message;
        }
    }

    private void RunRemove()
    {
        if (Selected is not { } package)
            return;

        try
        {
            _source.Remove(package.Id);
            Reload();
            Status = Strings.UserSoftwareRemoved(package.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidOperationException)
        {
            _logger.ZLogWarning(ex, $"Could not remove {package.Name}.");
            Status = ex.Message;
        }
    }

    /// <summary>Expands a leading <c>~</c>, which is what a user types and not a path.</summary>
    private static string Expand(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal)
            ? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;
}
