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
| `Wlrix.Theme`             | lib   | Keeps an app drawing in the session's color scheme. One call.     |
| `Wlrix.Packages`          | lib   | The system package managers behind one interface. No UI.          |
| `Wlrix.Packages.Helper`   | exe   | `wlrix-pkg-helper` — the privileged half, run through `pkexec`.   |
| `Wlrix.Toolchest`         | app   | IRIX-style menu launcher anchored top-left of the desktop.        |
| `Wlrix.Desks`             | app   | Virtual-desktop (Rooms) overview and switcher.                    |
| `Wlrix.Console`           | app   | Tails the wlRIX component logs, one tab each.                     |
| `Wlrix.Settings.Keyboard` | app   | Keyboard settings panel.                                          |
| `Wlrix.Settings.Windows`  | app   | Window settings panel: focus policy and the 4Dwm flags.           |
| `Wlrix.Settings.Schemes`  | app   | Color scheme browser, after IRIX's. One Apply, whole desktop.     |
| `Wlrix.Shutdown`          | app   | The Shut Down System dialog, behind the Toolchest's two items.    |
| `Wlrix.SourcePicker`      | app   | The screen-share picker `xdg-desktop-portal-wlrix` puts up.       |
| `Wlrix.SoftwareManager`   | app   | Package manager, after IRIX's `swmgr`.                            |
| `Wlrix.Archiver`          | app   | Archive browser and extractor, after KDE's Ark.                   |
| `Wlrix.Packages.Tests`    | tests | Parser and validation tests for `Wlrix.Packages`. Under `tests/`. |

More apps (file manager, terminal, etc.) get added as sibling projects.

## Installing

`wlrix-apps` had no packaging until the archiver needed to be an `xdg-open` handler; the
`Justfile` covers that one application so far, and the others are still launched by name from the Toolchest and the
session.

```
just publish            # Linux-only, framework-dependent
sudo just install       # /usr/bin, /usr/lib/wlrix, /usr/share/applications
```

`publish` passes `-r linux-x64` and that is not incidental: a RID-agnostic publish ships
`runtimes/` for every platform Avalonia supports, Windows and macOS natives included, which is 567 MB against 28 MB. A
published .NET application is a directory of assemblies the executable finds relative to itself, so the payload goes to
`/usr/lib/wlrix/archiver` and `/usr/bin` gets a symlink — the apphost reads `/proc/self/exe` and resolves through it.

An application that opens files owns a `data/<app-id>.desktop` beside its source. Three things about writing one are
easy to get wrong and all of them fail quietly:

- **The format has no line continuations.** A `MimeType=` list wrapped for readability parses as several broken lines
  and still looks right in an editor. `just check-desktop` catches it.
- **`update-desktop-database` is what makes the registration real.** Without it the entry is on disk, nothing has
  indexed its `MimeType=`, and every lookup still misses. `install` runs it, except when staging into a `rootdir`, where
  it belongs to the package's post-install.
- **`TryExec` hides the entry until the binary is genuinely on `PATH`.** That is what you want — no "Open With" entry
  for something that is not installed — but it also means an entry installed without its binary is invisible rather than
  broken, and GIO enforces it where `xdg-mime` does not.

Compressed tars are registered under two MIME types each, and that is not redundancy: `xdg-mime
query filetype backup.tar.gz` sniffs the gzip magic and answers `application/gzip`, while `gio
info` honors the `*.tar.gz` glob and answers `application/x-compressed-tar`. Register one and tarballs route from a file
manager but not from `xdg-open`, or the reverse.

## Translations

Apps are localizable through `Wlrix.Common.Localization`. An app owns a `Localization/Strings.resx`
and a small static `Strings` class over a `StringCatalog`; static labels in AXAML go through the
`{loc:Tr Key}` markup extension, which reads the same catalog.

`TrExtension` deliberately does not derive from Avalonia's `MarkupExtension` — Avalonia resolves markup extensions
structurally, by the presence of a `ProvideValue` method — so `Wlrix.Common`
stays free of an Avalonia reference.

Only **American English** (`en-US`) ships today, in the resources and in the code: `Color`,
`Initialize`, `License`, `Canceled`, `Behavior`. A translation is a `Strings.<culture>.resx`
beside the neutral one, and nothing else. `Wlrix.Toolchest` and `Wlrix.Archiver` also carry a `ja` satellite.

## Archives

`Wlrix.Archiver` reads tar, zip, 7z, rar and the common compressed formats through
[SharpCompress](https://github.com/adamhathcock/sharpcompress), which is pure managed and needs nothing installed.
Writing is narrower than reading — there is no encoder for 7z or rar — so each backend declares what it can do per
format and the menu enables itself from that: `Edit ▸
Remove from archive` is disabled on a rar because it would not work, not because it was forgotten. With `7z` on the
`PATH`, `SevenZipCliBackend` takes over that format and it becomes writable.

Reading a large archive is slow in a way no amount of tuning removes: gzip is not seekable, so listing a 3.1 GB
`.tar.gz` means decompressing all of it — about 51 seconds, against a third of a second to walk the 29,631 entries that
come out. The status line therefore carries a real percentage for the decompression (bytes read against the file's
length, which is known up front)
and a running count for the enumeration, plus a Cancel that actually stops it.

Two things about that are worth not undoing. The scratch file a compressed tar is unwrapped into goes under the app's
data directory, **not** `/tmp` — that is a tmpfs here, and the archive above unpacks to 9.3 GB. And only the top level
of the tree starts expanded: the same archive has 5,304 directories, and a `TreeView` asked to realize all of them at
once locks the window for seconds after the read has already finished.

**Filenames are the interesting part.** Zip and tar predate Unicode and neither has to say what encoding a name is in.
Zip's general-purpose bit 11 marks a name as UTF-8, but the archives that cause trouble are the ones written by tools
that never set it — a Shift-JIS zip from a Japanese Windows box, a GBK one from a Chinese one. Decoded as UTF-8 or
Latin-1 those come out as mojibake, and mojibake in a filename is not cosmetic: it is the name the extracted file gets.

`FilenameDecoder` takes valid UTF-8 at its word and otherwise scores the candidate code pages, weighing script coherence
over raw character counts — a misread Shift-JIS name reads as a jumble alternating between scripts, where the right
answer reads as one. `View ▸ Encoding` overrides the guess for the cases no heuristic gets right.

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

`localfeed/` is **committed**. Two of the four packages in it exist nowhere else, so a checkout without them restores to
`NU1101` rather than to something merely unpatched, and that would take CI and every fresh clone with it. The four total
under 400 KB and change only when a pinned version is bumped. Whatever you rebuild, commit the result.

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

Then drop the `.nupkg` into `localfeed/`, delete the version it replaces, bump
`Avalonia.Wayland` in `Directory.Packages.props` to match, and commit all three changes together — a bumped pin without
its package is a red CI run. Bump the `-wlrix.N` suffix on every rebuild: NuGet caches by id and version, so reusing a
version means the old one is served from
`~/.nuget/packages` and the new bits are silently ignored.
