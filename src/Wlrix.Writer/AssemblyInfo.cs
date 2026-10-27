// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.Versioning;

// Wlrix.Icons declares itself Linux-only and that propagates to its consumers.
// Correct: this is a Wayland desktop application and has no other target.
[assembly: SupportedOSPlatform("linux")]
