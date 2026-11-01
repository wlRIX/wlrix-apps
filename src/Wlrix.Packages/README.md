# Wlrix.Packages

The system package managers, behind one interface. `Wlrix.SoftwareManager` is the only consumer today; the library has
no UI in it and no Avalonia reference, so anything else that needs to ask what is installed can use it too.

## The shape

`IPackageBackend` is **reads only** — search, list installed, list updates, describe one package, list repositories. All
of it runs unprivileged in the calling process.

The writes are deliberately not on the interface. A transaction is described by the backend and performed by
`Privileged/`, through a helper behind `pkexec`. Keeping the two apart means the code that can change the system is one
small file that can be read end to end, rather than a method on each of three backends.

`PackageBackendFactory` picks the backend from `/etc/os-release`, confirmed by finding the program on the `PATH`. Both
halves matter: os-release alone misses a container image without the tool installed, and the `PATH` alone picks wrong on
a system that happens to have two. A system it cannot place gets a `NullBackend`, and the window still opens — the same
posture the rest of wlRIX takes towards a missing component.

`ID_LIKE` is what makes derivatives work without being listed. CachyOS declares
`ID=cachyos ID_LIKE=arch` and inherits pacman from it; Linux Mint declares
`ID_LIKE="ubuntu debian"`.

## Two things that are easy to get wrong

**Every child process runs in the C locale.** Package managers translate their output, and this library reads that
output. On a Japanese system `pacman -Qi` answers with `名前` where the parser expects `Name`, so every field comes back
empty — with no error, because nothing failed.
`ProcessRunner` sets `LC_ALL=C` on every child, and that is the only reason the parsers work off an English-locale
machine.

**Arguments never become a command line.** A package name arrives from a search field.
`ProcessStartInfo.ArgumentList` is the only way anything is passed, and there is no shell anywhere in this path.

## Parsing

Package manager output is not an API and does change between releases. Three things keep that from becoming the user's
problem:

- The machine-readable form is used wherever one exists — `dpkg-query -W -f=`,
  `zypper --xmlout`, `apt-get -s` — rather than the human listing beside it. `apt list` is avoided entirely; apt itself
  prints a warning that its CLI is not a stable interface.
- Every parser is a **pure function over a string** under `Backends/Parsing/`. Nothing there starts a process.
- Every parser has fixture tests in `tests/Wlrix.Packages.Tests`, so a format change breaks a test rather than a user's
  system.

|        | search / list                                             | details          | repositories                           |
|--------|-----------------------------------------------------------|------------------|----------------------------------------|
| pacman | `-Ss`, `-Qi`, `checkupdates`                              | `-Si` then `-Qi` | parse `/etc/pacman.conf`               |
| apt    | `apt-cache search`, `dpkg-query -W`, `apt-get -s upgrade` | `apt-cache show` | parse `sources.list`, `sources.list.d` |
| zypper | `--xmlout search --details`, `list-updates`               | `zypper info`    | `--xmlout repos`                       |

A size of **zero means unknown**, not empty — no real package occupies nothing. `SizeParser`
rounds a sub-kilobyte size up to 1 rather than down to zero for exactly that reason.

## What is and is not tested on hardware

The pacman backend is exercised against a real Arch-derived system, and its fixtures under
`tests/…/Fixtures` are recorded from it.

**The apt and zypper backends have not been run on a Debian or SUSE machine.** They are written against those tools'
documented machine-readable output and covered by fixture tests, but the fixtures are hand-written from the
documentation rather than recorded. Everything they run is read-only, so the worst an unnoticed format change costs is
an empty list — but do not read
"there are tests" as "this has been used". Recording real fixtures on each is the obvious next step for anyone with the
machines.

## pacman and partial upgrades

Arch does not support partial upgrades, so nothing here may move pacman's sync database without
also upgrading. There is no `-Sy`: the helper refuses `refresh` on pacman, and the Updates list comes from
`checkupdates`, which syncs a temporary copy without root. That needs `pacman-contrib` and `fakeroot`. Without them
the list falls back to `-Qu` over the lists already on disk, and the status line says it may be out of date.

For the same reason a pacman Updates list is all or nothing. Start runs `-Syu`, and `SelectiveUpgrade` is what tells the
window the rows cannot be unticked. Installing stays a plain `-S`, so it can fail on a package the mirrors have since
replaced. The way out is to run the updates first, and the failure dialog says so.

## Restarts

`IPackageBackend.NeedsRestartAsync` answers whether the system needs a restart, after a transaction and when the
window opens.

|        | how it knows                                                                          |
|--------|---------------------------------------------------------------------------------------|
| pacman | the running kernel's modules are gone, or `RestartPolicy` matches an upgraded package |
| apt    | `/run/reboot-required` exists                                                         |
| zypper | `zypper needs-rebooting`, and the transaction's own exit code 102                     |

`RestartPolicy` follows CachyOS's `cachyos-reboot-required` hook, except that only the running kernel counts. It does
not call the hook, which exists only on CachyOS and finds who to notify from `users`. As with the rest of those
backends, the apt and zypper rows have not been run on a real system.

## AppStream

`AppStream/` gives a package a product name, an icon and a category, read from the catalog files without root.
`appstreamcli` is not involved, since it has no way to ask by package. A package no catalog names is an ordinary
package, and its row looks the way it always has. So does every row on a system with no catalogs. The name only changes
what a row shows: marks and transactions still go by the package name.

|        | catalog                                  | icons                                         | comes from               |
|--------|------------------------------------------|-----------------------------------------------|--------------------------|
| pacman | `/usr/share/swcatalog/xml/*.xml.gz`      | `/usr/share/swcatalog/icons/<origin>/<size>/` | `archlinux-appstream-data` |
| apt    | `/var/lib/swcatalog/yaml/*.yml.gz`       | `/var/lib/swcatalog/icons/<origin>/<size>/`   | `appstream`              |
| zypper | `/var/cache/swcatalog/xml/*.xml.gz`      | `/var/cache/swcatalog/icons/<origin>/<size>/` | `libzypp-plugin-appdata` |

Nothing chooses between those rows: every catalog in all three roots is read. The Arch fixture is cut down from the real
package, and loading that package takes about 270 ms and 5 MB. Debian's layout comes from AppStream's source and
openSUSE's from the specification and a changelog. As with those backends, neither has been run on a real system, and
the DEP-11 fixture is hand-written.

Which component speaks for a package:

- One desktop application names it, whatever add-ons come along (`codeblocks`).
- Several leave the package's name alone: `libreoffice-fresh` is not "LibreOffice Writer". The one named after the
  package, or else the first by id, lends its icon and categories.
- With no application, a single component of any other type names it, and several say nothing (`gsfonts`).

`/usr/share/metainfo` is not read. Those files do not say which package they belong to, and finding out means asking the
package manager about each file.

Arch's icons are all JPEG-XL, which SkiaSharp cannot decode. The icon theme goes first, as the specification asks, and
that covers what is installed. For the rest `JxlIconConverter` runs `djxl` once per icon, only for rows on screen, and
keeps a PNG under `$XDG_CACHE_HOME/wlrix/software-manager/icons`. Without `djxl` those rows have no icon.

Catalog text decides what is joined onto a path, so an origin or icon name must be a single path segment. The check also
applies to stock names, because the icon theme takes an absolute path as it stands.

## Stopping

pkexec and everything under it run as root, so the application cannot signal them. Stop sends a `stop` line down the
helper's stdin, the helper sends pacman `SIGINT`, and the transaction is watched until it really ends. The window offers
this only until the first package operation, because interrupting after that would leave some packages replaced and
others not. apt and zypper have no tested safe interrupt, so `SafeInterrupt` is off and Stop stays gray. Stdin simply
ending means the window closed, and the transaction carries on.

## User software

`UserSoftware/` is the other half, and it never touches root: `IUserSoftwareSource` covers software the user installs
into their own home directory, with `AppImageSource` as the first implementation and the shape a Flatpak one would take.

Installing an AppImage is three things — copy it into `~/Applications`, mark it executable, and write a `.desktop` entry
into `~/.local/share/applications`. That last one is the point:
an AppImage in a directory is a file, and an AppImage with a desktop entry is an application the Toolchest's scanner
will find.

Nothing is read out of the AppImage itself. Getting its real name, icon or categories means running it with
`--appimage-extract` — executing an untrusted binary to find out what it is, before the user has asked to run it. The
generated entry uses the file name and a generic icon, both of which the user can edit.

Removal only ever deletes inside `~/Applications`, and only a desktop entry this wrote (they carry a `wlrix-appimage-`
prefix), so a hand-written entry for the same AppImage survives.

## pacman and repositories

pacman reports `ListRepositories` but not `ModifyRepositories`, and that is not an oversight. It has no command for
adding or removing one; the only way is to edit `/etc/pacman.conf`, a hand-owned file holding the user's mirror choices
and their comments. A half-understood editor turning it into something pacman rejects would cost them their whole
configuration — the same failure `wlrix-settings-daemon` exists to stop happening to `compositor.toml`. The UI grays the
buttons out instead.

Commented-out sections are reported as **disabled repositories rather than absent ones**, because that is how the file
is used: the stock configuration ships `[multilib]` commented out, and a user looking for it wants to see it listed and
switched off.
