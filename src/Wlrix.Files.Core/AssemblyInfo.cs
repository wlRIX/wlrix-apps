using System.Runtime.Versioning;

// This library is Linux-only, and says so once here rather than per call site.
//
// Everything it does is specific to a freedesktop system -- POSIX mode bits,
// /proc/self/mountinfo, the XDG base directories, the freedesktop trash and thumbnail
// specifications -- so the platform-compat analyzer is right that the calls are not
// portable, and wrong only in supposing this might run somewhere else. wlRIX is a
// Wayland desktop; there is no other target.
//
// Consumers are Linux-only too and mark themselves the same way, which is what
// Wlrix.Archiver's Program already does for UseManagedSystemDialogs.
[assembly: SupportedOSPlatform("linux")]
