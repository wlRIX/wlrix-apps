using Wlrix.Common;
using Xunit;

namespace Wlrix.Common.Tests;

/// <summary>Asking the sound daemon for a sound.</summary>
public class SystemSoundTests
{
    [Fact]
    public void PlayingWithNoDaemonIsSilenceNotAnError()
    {
        // The test host has no wlrix-sound listening, or one it must not depend on. Either way
        // the call has to return quietly: it is made in the middle of a drop or a launch, and a
        // desktop without its sound daemon is a quiet desktop, not a broken one.
        SystemSound.Play("bell");
        SystemSound.Play("not a valid name/../at all");
    }
}

/// <summary>An app's own dialog sounding like the MessageDialog it resembles.</summary>
public class DialogIconSoundTests
{
    [Theory]
    [InlineData("Error", "dialog-error")]
    [InlineData("Critical", "dialog-error-serious")]
    [InlineData("Warning", "dialog-warning")]
    [InlineData("Question", "dialog-question")]
    [InlineData("Information", "dialog-information")]
    [InlineData("Unheard-of", "dialog-information")]
    public void EachIconHasItsSound(string icon, string sound) =>
        Assert.Equal(sound, SystemSound.ForDialogIcon(icon));
}
