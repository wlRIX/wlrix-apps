using Wlrix.Files.ViewModels;
using Xunit;

namespace Wlrix.Files.Tests;

/// <summary>A file name on the status line, while it is being copied.</summary>
public class StatusNameTests
{
    [Fact]
    public void AShortNameIsShownWhole() =>
        Assert.Equal("ubuntu.iso", MainWindowViewModel.Shortened("ubuntu.iso"));

    [Fact]
    public void ALongNameKeepsBothEnds()
    {
        // The end carries the version and the extension, which tell two downloads apart; the
        // amount and rate that follow the name on the line must not be pushed off it.
        var name = "ubuntu-24.04.1-desktop-amd64-with-a-much-longer-tail-v2.iso";

        var shown = MainWindowViewModel.Shortened(name);

        Assert.Equal(40, shown.Length);
        Assert.StartsWith("ubuntu-24.04", shown);
        Assert.EndsWith("tail-v2.iso", shown);
        Assert.Contains('…', shown);
    }
}
