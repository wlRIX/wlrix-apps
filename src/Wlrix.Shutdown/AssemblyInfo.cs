using System.Runtime.Versioning;

// Linux-only, said once here: the dialog asks logind to power the machine off. wlRIX has no
// other target.
[assembly: SupportedOSPlatform("linux")]
