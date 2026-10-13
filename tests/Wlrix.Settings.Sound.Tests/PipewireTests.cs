using Wlrix.Settings.Sound.Services;
using Xunit;

namespace Wlrix.Settings.Sound.Tests;

/// <summary>Reading the sound server's answers.</summary>
public class PipewireTests
{
    [Fact]
    public void OnlySinksAreOutputs()
    {
        // Trimmed from a real pw-dump: a sink, a source and a stream. Only the sink is somewhere
        // a desktop sound can be played through.
        const string dump = """
            [
              { "id": 30, "type": "PipeWire:Interface:Node",
                "info": { "props": { "media.class": "Audio/Sink",
                                     "node.name": "alsa_output.pci-0000_00_1f.3.analog-stereo",
                                     "node.description": "Built-in Audio Analog Stereo" } } },
              { "id": 31, "type": "PipeWire:Interface:Node",
                "info": { "props": { "media.class": "Audio/Source",
                                     "node.name": "alsa_input.pci-0000_00_1f.3.analog-stereo",
                                     "node.description": "Built-in Audio Microphone" } } },
              { "id": 77, "type": "PipeWire:Interface:Node",
                "info": { "props": { "media.class": "Stream/Output/Audio", "node.name": "wlrix-sound" } } },
              { "id": 0, "type": "PipeWire:Interface:Core", "info": { "props": {} } },
              { "id": 5, "type": "PipeWire:Interface:Metadata", "props": {} }
            ]
            """;

        var outputs = Pipewire.ParseSinks(dump);

        var output = Assert.Single(outputs);
        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", output.Name);
        Assert.Equal("Built-in Audio Analog Stereo", output.Description);
    }

    [Fact]
    public void AnUnreadableDumpIsNoOutputs() =>
        Assert.Empty(Pipewire.ParseSinks("not json"));

    [Theory]
    [InlineData("Volume: 0.40\n", false)]
    [InlineData("Volume: 0.40 [MUTED]\n", true)]
    [InlineData("Translate fault\n", null)]
    public void TheMuteIsReadFromWpctl(string answer, bool? muted) =>
        Assert.Equal(muted, Pipewire.ParseMuted(answer));
}
