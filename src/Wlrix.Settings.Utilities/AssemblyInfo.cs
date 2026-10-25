using System.Runtime.Versioning;

// Wlrix.Files.Core declares itself Linux-only and that propagates to its consumers.
// Correct: this is a Wayland desktop application and has no other target.
[assembly: SupportedOSPlatform("linux")]
