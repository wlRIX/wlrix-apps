using System.Runtime.Versioning;

// Linux-only for the same reason as Wlrix.Files.Core: the XDG icon theme specification and
// its base directories are a freedesktop system's, and wlRIX has no other target.
[assembly: SupportedOSPlatform("linux")]
