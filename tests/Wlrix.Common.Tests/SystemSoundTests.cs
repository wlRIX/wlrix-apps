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
