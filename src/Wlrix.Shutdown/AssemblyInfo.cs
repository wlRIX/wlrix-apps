using System.Runtime.Versioning;

// Linux-only, said once here: the dialog asks logind to power the machine off, and draws its
// IRIX picture through Wlrix.Icons, which is Linux-only too. wlRIX has no other target.
[assembly: SupportedOSPlatform("linux")]
