using System.Runtime.Versioning;

// Wlrix.Files.Core declares itself Linux-only, and that propagates to everything that
// calls it. Correct, and worth restating rather than suppressing: these tests read
// /proc/self/mountinfo fixtures and POSIX modes, and would not mean anything elsewhere.
[assembly: SupportedOSPlatform("linux")]
