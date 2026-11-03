using Wlrix.Iconbox.Services;
using Xunit;

namespace Wlrix.Iconbox.Tests;

/// <summary>
/// The lookup has to agree with the compositor's (<c>icon_image.rs</c>), or a window would show
/// a different picture once the iconbox is turned off.
/// </summary>
public sealed class TileArtTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wlrix-iconbox-tests-" + Guid.NewGuid());
    private readonly string _user;
    private readonly string _system;

    public TileArtTests()
    {
        _user = Directory.CreateDirectory(Path.Combine(_root, "user")).FullName;
        _system = Directory.CreateDirectory(Path.Combine(_root, "system")).FullName;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Touch(string dir, string name)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, []);
        return path;
    }

    private TileArt Art() => new([_user, _system]);

    [Fact]
    public void The_users_copy_wins_over_the_systems()
    {
        Touch(_system, "com.wlrix.console.png");
        var user = Touch(_user, "com.wlrix.console.png");

        Assert.Equal(user, Art().Find("com.wlrix.console"));
    }

    [Fact]
    public void An_exact_name_anywhere_beats_a_case_insensitive_one()
    {
        Touch(_user, "BLENDER.png");
        var exact = Touch(_system, "blender.png");

        Assert.Equal(exact, Art().Find("blender"));
    }

    [Fact]
    public void A_name_differing_only_in_case_is_found()
    {
        var path = Touch(_system, "Blender.png");

        Assert.Equal(path, Art().Find("blender"));
    }

    [Fact]
    public void Anything_unmatched_gets_the_default()
    {
        var fallback = Touch(_system, "default.png");

        Assert.Equal(fallback, Art().Find("org.example.unknown"));
        Assert.Equal(fallback, Art().Find(string.Empty));
    }

    [Fact]
    public void Nothing_installed_is_no_picture()
    {
        Assert.Null(Art().Find("com.wlrix.console"));
    }
}
