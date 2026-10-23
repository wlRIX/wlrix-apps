using System.Runtime.Versioning;

// Linux-only, said once here rather than per call site: the XDG icon theme, desktop entries
// and Unix file modes this application is built on belong to a freedesktop system, and wlRIX
// has no other target.
[assembly: SupportedOSPlatform("linux")]
