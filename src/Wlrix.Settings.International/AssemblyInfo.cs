using System.Runtime.Versioning;

// Linux-only, said once here rather than per call site: systemd's localed and timedated, the
// XDG icon theme and glibc's locale files this panel is built on belong to a freedesktop system,
// and wlRIX has no other target.
[assembly: SupportedOSPlatform("linux")]
