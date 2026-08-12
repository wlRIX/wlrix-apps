# wlrix-apps

The wlRIX user-facing applications: one .NET solution (`Wlrix.slnx`) with a project per app plus a shared `Wlrix.Common`
library. Apps recreate the IRIX Interactive Desktop surface.

- **Language:** C# / Avalonia
- **License:** GPL-3.0-or-later
- **Theme:** references the `Wlrix.Avalonia` package (from `wlrix-avalonia`)

## Projects

| Project                   | Type  | Purpose                                                           |
|---------------------------|-------|-------------------------------------------------------------------|
| `Wlrix.Common`            | lib   | Shared branding, constants, localization, and helpers.            |
| `Wlrix.Settings.Client`   | lib   | Reads and writes wlRIX settings, through `wlrix-settings-daemon`. |
| `Wlrix.Packages`          | lib   | The system package managers behind one interface. No UI.          |
| `Wlrix.Packages.Helper`   | exe   | `wlrix-pkg-helper` — the privileged half, run through `pkexec`.   |
| `Wlrix.Toolchest`         | app   | IRIX-style menu launcher anchored top-left of the desktop.        |
| `Wlrix.Desks`             | app   | Virtual-desktop (Rooms) overview and switcher.                    |
| `Wlrix.Console`           | app   | Tails the wlRIX component logs, one tab each.                     |
| `Wlrix.Settings.Keyboard` | app   | Keyboard settings panel.                                          |
| `Wlrix.SourcePicker`      | app   | The screen-share picker `xdg-desktop-portal-wlrix` puts up.       |
| `Wlrix.SoftwareManager`   | app   | Package manager, after IRIX's `swmgr`.                            |
| `Wlrix.Packages.Tests`    | tests | Parser and validation tests for `Wlrix.Packages`. Under `tests/`. |

More apps (file manager, terminal, etc.) get added as sibling projects.

## Translations

Apps are localizable through `Wlrix.Common.Localization`. An app owns a `Localization/Strings.resx`
and a small static `Strings` class over a `StringCatalog`; static labels in AXAML go through the
`{loc:Tr Key}` markup extension, which reads the same catalog.

`TrExtension` deliberately does not derive from Avalonia's `MarkupExtension` — Avalonia resolves markup extensions
structurally, by the presence of a `ProvideValue` method — so `Wlrix.Common`
stays free of an Avalonia reference.

Only **American English** (`en-US`) ships today, in the resources and in the code: `Color`,
`Initialize`, `License`, `Canceled`, `Behavior`. A translation is a `Strings.<culture>.resx`
beside the neutral one, and nothing else. `Wlrix.Toolchest` also carries a `ja` satellite.

## Managing software

`Wlrix.SoftwareManager` reads through `Wlrix.Packages`, which runs the package manager unprivileged in-process. Anything
that changes the system goes through `wlrix-pkg-helper` behind
`pkexec`, one process per transaction, with a fixed and validated verb set — there is no verb that takes a command, and
no shell anywhere in that path. See `src/Wlrix.Packages/README.md`.

The helper is exercisable without root:

```sh
dotnet run --project src/Wlrix.Packages.Helper -- --check install pacman cowsay
```

**A settings app does not touch config files.** wlRIX settings are IRIX-style separate panels, one window per concern,
and every one of them reads and writes through `Wlrix.Settings.Client`
— which asks `wlrix-settings-daemon` over `com.wlrix.Settings`. The daemon owns the TOML editing, the "tell the
compositor to reload" signal, and the schema the panel renders from, so a new panel is a window and a list of keys
rather than another copy of all three. See that project's README, and `wlrix-settings-daemon/README.md` for what it
guarantees about the file.

## Build

```sh
dotnet build
dotnet run --project src/Wlrix.Toolchest
```

## The local NuGet feed

`localfeed/` is a NuGet source (see `nuget.config`) holding packages that are not on nuget.org:
the wlRIX Avalonia theme and dialogs, and a **patched `Avalonia.Wayland`**. It is gitignored, so a fresh clone has to
rebuild it before `dotnet restore` will work.

The Wayland patches, on the `wlrix-12.1.0` branch of the [Avalonia fork](https://github.com/vic485/Avalonia)
(upstream's `12.1.0` tag plus two commits):

- **App id.** `WaylandPlatformOptions.AppId`, defaulting to the entry assembly name, mirroring
  `X11PlatformOptions.WmClass`. Without it every window's app id is empty and nothing —
  `wlrix-desktop`'s magic carpet, the Desks overview — can tell one application from another.
- **`CanResize=false` on the wire.** Mapped onto `min == max` size constraints, which is the only spelling xdg-shell has
  for "this window is a fixed size". `wlrix-compositor` reads it to drop the maximize button and the resize grips (see
  its README, *Window capabilities*). `SetCanMaximize`
  and `SetCanMinimize` stay no-ops: the protocol has no request for either, and
  `xdg_toplevel.wm_capabilities` runs the other way, compositor to client.

### Rebuilding the localfeed

```bash
cd ../Avalonia && git checkout wlrix-12.1.0 && git submodule update --init --recursive
```

```bash
dotnet pack src/Avalonia.Wayland/Avalonia.Wayland.csproj -c Release -p:PackageVersion=12.1.1-wlrix.2 -o /tmp/wl
```

Two things then need fixing up by hand, because upstream's own packages are assembled by Nuke (`numerge.json`) rather
than by `dotnet pack`, and running the whole Nuke pipeline for one library is not worth it:

1. The nuspec's dependency block lists `Avalonia.Dialogs`, which upstream **merges into** the
   `Avalonia` package. Replace the whole block with the three dependencies upstream's own
   `Avalonia.Wayland` declares: `NWayland 0.11.0`, `Avalonia 12.1.0`, `Avalonia.FreeDesktop 12.1.0`.
2. Those versions come out matching `PackageVersion` rather than the source version. They must say **12.1.0** — the tag
   the code is built from, and what the assembly references resolve to.

Then drop the `.nupkg` into `localfeed/`, delete the version it replaces, and bump
`Avalonia.Wayland` in `Directory.Packages.props` to match. Bump the `-wlrix.N` suffix on every rebuild: NuGet caches by
id and version, so reusing a version means the old one is served from
`~/.nuget/packages` and the new bits are silently ignored.
