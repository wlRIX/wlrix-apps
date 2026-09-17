using Wlrix.Files.Core;
using Xunit;

namespace Wlrix.Files.Core.Tests;

public class LocationTests
{
    [Fact]
    public void ABareAbsolutePathIsReadAsALocalLocation()
    {
        // argv, a config file and a drop all hand over bare paths, and requiring
        // file:// at each of those call sites would be noise.
        var location = Location.Parse("/home/vic/notes.txt");
        Assert.True(location.IsLocal);
        Assert.Equal("/home/vic/notes.txt", location.Path);
        Assert.Equal("file:///home/vic/notes.txt", location.ToUriString());
    }

    [Theory]
    [InlineData("/a/./b", "/a/b")]
    [InlineData("/a//b", "/a/b")]
    [InlineData("/a/b/", "/a/b")]
    [InlineData("/a/b/..", "/a")]
    [InlineData("/a/b/../../c", "/c")]
    [InlineData("/", "/")]
    [InlineData("///", "/")]
    public void PathsAreCanonicalizedSoTwoSpellingsOfOnePlaceCompareEqual(string input, string expected)
    {
        // Equality drives the window registry, the watcher's de-duplication and the
        // per-directory view state, so two spellings of one directory must not be two keys.
        Assert.Equal(expected, Location.Parse(input).Path);
    }

    [Fact]
    public void DotDotCannotClimbAboveTheRoot()
    {
        // The kernel treats /.. as /, and a stray one in a config file should not be
        // fatal. There is nothing above the root to escape to, so this cannot leak.
        Assert.Equal("/", Location.Parse("/../../..").Path);
        Assert.Equal("/etc", Location.Parse("/../../etc").Path);
    }

    [Fact]
    public void UserinfoIsStrippedBecauseLocationsAreLoggedAndSerialized()
    {
        // A location reaches the log, the thumbnail filename hash and bookmarks.json.
        // A password that got into any of those would be very hard to get back out.
        var location = Location.Parse("smb://vic:hunter2@nas.local/share/dir");
        Assert.DoesNotContain("hunter2", location.ToUriString(), StringComparison.Ordinal);
        Assert.DoesNotContain("vic", location.ToUriString(), StringComparison.Ordinal);
        Assert.Equal("nas.local", location.Host);
        Assert.Equal("smb://nas.local", location.MountKey);
    }

    [Fact]
    public void TheMountKeyIgnoresTheShareSoOneConnectionServesAWholeHost()
    {
        // SMB reaches every share on a host over one session; keying per share would
        // open a redundant connection for each.
        var a = Location.Parse("smb://nas.local/photos/2024");
        var b = Location.Parse("smb://nas.local/documents");
        Assert.Equal(a.MountKey, b.MountKey);
    }

    [Fact]
    public void ANonDefaultPortIsPartOfTheMountKeyButTheDefaultOneIsNot()
    {
        Assert.Equal("sftp://host", Location.Parse("sftp://host/home").MountKey);
        Assert.Equal("sftp://host:2222", Location.Parse("sftp://host:2222/home").MountKey);
    }

    [Fact]
    public void ParentWalksUpAndStopsAtTheRoot()
    {
        var location = Location.Parse("/a/b/c");
        Assert.Equal("/a/b", location.Parent!.Path);
        Assert.Equal("/a", location.Parent!.Parent!.Path);
        Assert.Equal("/", location.Parent!.Parent!.Parent!.Path);
        Assert.Null(location.Parent!.Parent!.Parent!.Parent);
    }

    [Fact]
    public void NameIsTheLastComponentAndEmptyAtTheRoot()
    {
        Assert.Equal("c.txt", Location.Parse("/a/b/c.txt").Name);
        Assert.Equal(string.Empty, Location.Parse("/").Name);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("a/b")]
    public void ChildRefusesAnythingThatIsNotASinglePathComponent(string name)
    {
        // Names come from a directory listing, which on a remote is data from another
        // machine. A crafted one must not be able to walk out of the directory.
        Assert.Throws<ArgumentException>(() => Location.Parse("/base").Child(name));
    }

    [Fact]
    public void ChildEscapesNothingEvenForAwkwardNames()
    {
        var child = Location.Parse("/base").Child("a b&c#d.txt");
        Assert.Equal("/base/a b&c#d.txt", child.Path);
        Assert.Equal("a b&c#d.txt", child.Name);
        // ...but the canonical URI is percent-encoded, because it is a URI. The ampersand
        // survives and the hash does not, which is GLib's rule rather than the RFC's; see
        // TheUriMatchesWhatGlibWouldHaveWritten.
        Assert.Equal("file:///base/a%20b&c%23d.txt", child.ToUriString());
    }

    [Fact]
    public void TheUriFormRoundTripsThroughParse()
    {
        // The thumbnail cache hashes ToUriString and later has to find the same file
        // again from a location rebuilt out of JSON.
        foreach (var text in new[] { "/a/b c", "/a/#hash", "smb://host/share/naïve", "/日本語/ファイル.txt" })
        {
            var original = Location.Parse(text);
            Assert.Equal(original, Location.Parse(original.ToUriString()));
        }
    }

    [Fact]
    public void EqualityAndHashingAreValueBasedSoALocationWorksAsADictionaryKey()
    {
        var a = Location.Parse("/a/b");
        var b = Location.Parse("/a/./b/");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a == b);

        var map = new Dictionary<Location, int> { [a] = 1 };
        Assert.True(map.ContainsKey(b));
    }

    [Fact]
    public void ContainsIsTrueForDescendantsAndFalseForMerePrefixes()
    {
        var dir = Location.Parse("/home/vic");
        Assert.True(dir.Contains(Location.Parse("/home/vic/notes")));
        // "/home/victor" starts with "/home/vic" as a string but is not inside it.
        Assert.False(dir.Contains(Location.Parse("/home/victor")));
        Assert.False(dir.Contains(dir));
        Assert.False(dir.Contains(Location.Parse("smb://host/home/vic/x")));
    }

    [Fact]
    public void TryGetLocalPathSucceedsOnlyForFileLocations()
    {
        Assert.True(Location.Parse("/tmp/x").TryGetLocalPath(out var path));
        Assert.Equal("/tmp/x", path);
        Assert.False(Location.Parse("smb://host/share").TryGetLocalPath(out var none));
        Assert.Null(none);
    }

    [Fact]
    public void AFileUriWithAHostIsRejectedRatherThanSilentlyTreatedAsLocal()
    {
        // RFC 8089 allows it, but nothing here can act on it, and quietly dropping the
        // host would read the wrong machine's files.
        Assert.False(Location.TryParse("file://otherhost/etc/passwd", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("smb://")]
    public void UnusableTextIsRejected(string text) => Assert.False(Location.TryParse(text, out _));

    // --- the thumbnail URI, against GLib -----------------------------------

    /// <summary>
    /// Paths and the URIs <c>g_filename_to_uri</c> actually produced for them, with the MD5
    /// the thumbnail spec names the cache file by.
    /// </summary>
    /// <remarks>
    /// Captured from GLib on this machine rather than derived from the RFC, because the
    /// question is not what the spec permits but what every other file manager on the system
    /// does. The cache is shared: hash the URI differently and neither side sees the other's
    /// thumbnails.
    /// </remarks>
    [Theory]
    [InlineData("/tmp/a b.png", "file:///tmp/a%20b.png", "f2584ab78dd95a88bd0d3f0ecaee7a8c")]
    [InlineData("/tmp/foo,bar.png", "file:///tmp/foo,bar.png", "228bd7823ecd8fd68ad7e15aeea6fe11")]
    [InlineData("/tmp/it's (x)+y.png", "file:///tmp/it's%20(x)+y.png", "6d8a0e7c2b9c6333cc8312d54e93e911")]
    [InlineData("/tmp/\u00fc.png", "file:///tmp/%C3%BC.png", "2f9670d8f4af46e8d9529ba707ce1e2c")]
    [InlineData("/tmp/a@b:c.png", "file:///tmp/a@b:c.png", "887fedf67d2e7999b60cce08eea75a1e")]
    [InlineData("/tmp/100%.png", "file:///tmp/100%25.png", "8a004371214b77f20cdd27a950302c6a")]
    public void TheUriMatchesWhatGlibWouldHaveWritten(string path, string expected, string md5)
    {
        var uri = Location.FromLocalPath(path).ToUriString();
        Assert.Equal(expected, uri);

        var digest = Convert.ToHexStringLower(
            System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(uri)));
        Assert.Equal(md5, digest);
    }

    [Fact]
    public void TheCharactersGlibKeepsAreKeptAndTheOnesItEscapesAreEscaped()
    {
        // Both halves captured from g_filename_to_uri. Note the semicolon: an RFC 3986
        // sub-delimiter, legal in a path segment, and escaped by GLib anyway. Reading the
        // specification instead of asking the implementation gets that one wrong.
        foreach (var safe in "!$&'()*+,=:@")
            Assert.Equal($"file:///x{safe}y", Location.FromLocalPath($"/x{safe}y").ToUriString());

        Assert.Equal("file:///x%3By", Location.FromLocalPath("/x;y").ToUriString());
        Assert.Equal("file:///x%23y", Location.FromLocalPath("/x#y").ToUriString());
        Assert.Equal("file:///x%3Fy", Location.FromLocalPath("/x?y").ToUriString());
        Assert.Equal("file:///x%5By%5Dz", Location.FromLocalPath("/x[y]z").ToUriString());
        Assert.Equal("file:///a%20b", Location.FromLocalPath("/a b").ToUriString());
    }

    [Fact]
    public void ASeparatorInsideAComponentIsStillEscaped()
    {
        // The one character that must never survive: a path component containing a slash
        // would otherwise become two components and point somewhere else entirely.
        var location = Location.Parse("/home/vic").Child("a b");
        Assert.Equal("file:///home/vic/a%20b", location.ToUriString());
        Assert.Throws<ArgumentException>(() => Location.Parse("/home/vic").Child("a/b"));
    }
}
