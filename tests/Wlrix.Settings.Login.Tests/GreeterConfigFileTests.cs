using Wlrix.Settings.Login.Core;
using Xunit;

namespace Wlrix.Settings.Login.Tests;

/// <summary>
/// The splice that writes greeter.toml: the panel's three sections replaced, everything else in
/// the file kept as it was written.
/// </summary>
public class GreeterConfigFileTests
{
    private const string System = """
        # The wlRIX compositor.
        #
        # This is the system default.

        # The pointer.
        [cursor]
        theme = "sgi"
        size = 32
        """;

    private static readonly GreeterConfig Config = new()
    {
        Palette = "gotham",
        Layout = "jp",
        Model = "jp106",
        RepeatRate = 25,
        Outputs = [new GreeterOutput("DP-1", null, new GreeterMode(1920, 1080, 60), 0, 0, 1.5, "90")],
    };

    private static GreeterConfig Read(string text)
    {
        Assert.True(GreeterConfigFile.TryParse(text, out var config, out var problem), problem);
        return config;
    }

    [Fact]
    public void SeedingKeepsTheSystemCursorAndReplacesItsHeader()
    {
        var text = GreeterConfigFile.Apply(System, Config, seeding: true);
        Assert.DoesNotContain("This is the system default", text);
        Assert.Contains("# The wlRIX login screen.", text);
        Assert.Contains("# The pointer.\n[cursor]\ntheme = \"sgi\"", text);
        Assert.Equal(Config, Read(text));
    }

    [Fact]
    public void ApplyingAgainReplacesRatherThanAdds()
    {
        var once = GreeterConfigFile.Apply(System, Config, seeding: true);
        var changed = Config with { Palette = "classic", Outputs = [] };
        var twice = GreeterConfigFile.Apply(once, changed, seeding: false);

        Assert.Equal(changed, Read(twice));
        Assert.Equal(1, Count(twice, "[cursor]"));
        Assert.Equal(1, Count(twice, "[appearance]"));
        Assert.Equal(1, Count(twice, "Written by Login Settings"));
        Assert.Equal(0, Count(twice, "[[output]]"));
        Assert.Equal(twice, GreeterConfigFile.Apply(twice, changed, seeding: false));
    }

    [Fact]
    public void HandWrittenSectionsAndCommentsElsewhereAreKept()
    {
        const string existing = """
            # Mine.
            [cursor]
            theme = "sgi"

            [focus]
            # Pointer focus at the login screen too.
            policy = "pointer"

            [keyboard]
            layout = "us"

            [windows]
            opaque_move = false
            """;
        var text = GreeterConfigFile.Apply(existing, Config, seeding: false);

        Assert.Contains("# Mine.\n[cursor]", text);
        Assert.Contains("[focus]\n# Pointer focus at the login screen too.\npolicy = \"pointer\"", text);
        Assert.Contains("[windows]\nopaque_move = false", text);
        Assert.Equal(1, Count(text, "[keyboard]"));
        Assert.Equal("jp", Read(text).Layout);
    }

    [Fact]
    public void NothingSetWritesNoSections()
    {
        var text = GreeterConfigFile.Apply(System, new GreeterConfig(), seeding: true);
        Assert.DoesNotContain("[keyboard]", text);
        Assert.DoesNotContain("[appearance]", text);
        Assert.DoesNotContain("Written by Login Settings", text);
    }

    [Fact]
    public void ScalesAreWrittenAsFloats()
    {
        var config = new GreeterConfig { Outputs = [new GreeterOutput("DP-1", Scale: 2)] };
        Assert.Contains("scale = 2.0\n", GreeterConfigFile.Apply(string.Empty, config, seeding: false));
    }

    [Fact]
    public void AnOutputIsReadBack()
    {
        var output = Read("""
            [[output]]
            name = "DP-2"
            mode = "3840x2160"
            position = [2560, 0]
            scale = 2.0
            transform = "flipped-90"
            enabled = false
            """).Outputs.Single();
        Assert.Equal(new GreeterOutput("DP-2", false, new GreeterMode(3840, 2160, null), 2560, 0, 2.0, "flipped-90"), output);
    }

    private static int Count(string text, string what) => text.Split(what).Length - 1;
}
