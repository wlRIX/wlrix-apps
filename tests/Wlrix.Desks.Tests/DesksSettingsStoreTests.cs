using System.Text.Json;
using Wlrix.Desks.Models;
using Wlrix.Desks.Services;
using Xunit;

namespace Wlrix.Desks.Tests;

/// <summary>
/// That what the overview remembers survives a round trip, and that nothing it might find on
/// disk can stop it opening.
/// </summary>
/// <remarks>
/// Every test works in a temporary directory of its own. None of them may go anywhere near
/// <c>&lt;AppData&gt;/desks</c>, which belongs to whoever is running the suite.
/// </remarks>
public sealed class DesksSettingsStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wlrix-desks-tests-" + Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void WhatWasSavedComesBack()
    {
        var saved = new DesksSettings
        {
            ShowSnapshots = false,
            ShowGlobalDesk = false,
            WindowLabel = WindowLabel.AppId,
            WindowWidth = 900,
            WindowHeight = 400,
        };

        new DesksSettingsStore(_root).Save(saved);
        var loaded = new DesksSettingsStore(_root).Load();

        Assert.False(loaded.ShowSnapshots);
        Assert.False(loaded.ShowGlobalDesk);
        Assert.Equal(WindowLabel.AppId, loaded.WindowLabel);
        Assert.Equal(900, loaded.WindowWidth);
        Assert.Equal(400, loaded.WindowHeight);
    }

    [Fact]
    public void TheLabelModeIsWrittenByName()
    {
        // Small enough to read, so it should be readable: nobody looking into this file should
        // have to count an enum's members to learn what 1 meant.
        var store = new DesksSettingsStore(_root);
        store.Save(new DesksSettings { WindowLabel = WindowLabel.None });

        Assert.Contains("\"None\"", File.ReadAllText(store.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstRunGetsTheDefaults()
    {
        var loaded = new DesksSettingsStore(_root).Load();

        Assert.True(loaded.ShowSnapshots);
        Assert.True(loaded.ShowGlobalDesk);
        Assert.Equal(WindowLabel.Title, loaded.WindowLabel);
        Assert.Equal(720, loaded.WindowWidth);
        Assert.Equal(230, loaded.WindowHeight);
    }

    [Fact]
    public void AFileThatWillNotParseIsReportedAndIgnored()
    {
        var warnings = new List<string>();
        var store = new DesksSettingsStore(_root, warnings.Add);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.Path, "{ this is not json");

        var loaded = store.Load();

        Assert.True(loaded.ShowSnapshots);
        Assert.Single(warnings);
    }

    [Fact]
    public void ADocumentFromALaterBuildIsLeftAlone()
    {
        var store = new DesksSettingsStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.Path, JsonSerializer.Serialize(new
        {
            Version = DesksSettingsStore.CurrentVersion + 1,
            ShowSnapshots = false,
        }));

        // Not ours to guess at: a newer schema may mean something different by the same name.
        Assert.True(store.Load().ShowSnapshots);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(1e9)]
    public void AnUnusableSizeFallsBackToTheDefault(double size)
    {
        var store = new DesksSettingsStore(_root);
        store.Save(new DesksSettings { WindowWidth = size, WindowHeight = size });

        var loaded = new DesksSettingsStore(_root).Load();

        Assert.Equal(720, loaded.WindowWidth);
        Assert.Equal(230, loaded.WindowHeight);
    }

    [Fact]
    public void SavingSomewhereUnwritableDoesNotThrow()
    {
        var warnings = new List<string>();
        // A file where the directory should be: the store cannot create it, and must not care.
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, string.Empty);

        new DesksSettingsStore(_root, warnings.Add).Save(new DesksSettings());

        Assert.Single(warnings);
        File.Delete(_root);
    }
}
