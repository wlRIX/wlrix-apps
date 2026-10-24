# wlrix-apps

The wlRIX user-facing applications: one .NET solution (`Wlrix.slnx`) with a project per app plus a shared `Wlrix.Common`
library. Apps recreate the IRIX Interactive Desktop surface.

- **Language:** C# / Avalonia
- **License:** GPL-3.0-or-later
- **Theme:** references the `Wlrix.Avalonia` package (from `wlrix-avalonia`)

## Projects

| Project                   | Type | Purpose                                                             |
|---------------------------|------|---------------------------------------------------------------------|
| `Wlrix.Common`            | lib  | Shared branding, constants, localization, and helpers.              |
| `Wlrix.Settings.Client`   | lib  | Reads and writes wlRIX settings, through `wlrix-settings-daemon`.   |
| `Wlrix.Theme`             | lib  | Keeps an app drawing in the session's color scheme. One call.       |
| `Wlrix.Packages`          | lib  | The system package managers behind one interface. No UI.            |
| `Wlrix.Icons`             | lib  | The XDG icon theme and IRIX's icon state tints. No Avalonia at all. |
| `Wlrix.Files.Core`        | lib  | Files, listings, MIME and thumbnails. No Avalonia at all.           |
| `Wlrix.Settings.Outputs`  | lib  | The connected displays, over wlr-output-management. No UI.          |
| `Wlrix.Settings.Login.Core` | lib | The login screen's settings and their validation. No UI.           |
| `Wlrix.Packages.Helper`   | exe  | `wlrix-pkg-helper` — the privileged half, run through `pkexec`.     |
| `Wlrix.Settings.Login.Helper` | exe | `wlrix-login-helper` — writes `/etc/wlrix/greeter.toml` as root. |
| `Wlrix.Settings.Users.Helper` | exe | `wlrix-users-helper` — adds or removes pam_fprintd in `sudo` and `polkit-1`. |
| `Wlrix.Toolchest`         | app  | IRIX-style menu launcher anchored top-left of the desktop.          |
| `Wlrix.Desks`             | app  | Virtual-desktop (Rooms) overview and switcher.                      |
| `Wlrix.Console`           | app  | Tails the wlRIX component logs, one tab each.                       |
| `Wlrix.Settings.Keyboard` | app  | Keyboard settings panel: key repeat.                                |
| `Wlrix.Settings.International` | app | International panel, after IRIX's ipanel: country, language, keyboard layout, time zone, clock. |
| `Wlrix.Settings.International.Helper` | exe | `wlrix-locale-helper`: generates a locale for that panel, through pkexec. |
| `Wlrix.Settings.Mouse`    | app  | Mouse Settings, after IRIX's: acceleration, buttons, click speed, touchpad. |
| `Wlrix.Settings.Power`    | app  | Power Settings, wlRIX's own: the lid, idle profiles, lock before sleep. |
| `Wlrix.Settings.Network`  | app  | Network Settings: device status, the radio, saved Wi-Fi networks.   |
| `Wlrix.Settings.Bluetooth` | app | Bluetooth Settings, wlRIX's own: the adapter, pairing and forgetting devices. |
| `Wlrix.Settings.Windows`  | app  | Window settings panel: focus policy and the 4Dwm flags.             |
| `Wlrix.Settings.Displays` | app  | Displays panel, after KDE's: arrangement, modes, scale, HDR.        |
| `Wlrix.Settings.Audio`    | app  | Audio Panel, after IRIX's apanel. Replaces pavucontrol.             |
| `Wlrix.Settings.Sound`    | app  | System & Desktop Sounds panel, for `wlrix-sound`.                   |
| `Wlrix.Settings.Background` | app | Background panel, after IRIX's: wallpaper, fit, color, lock screen. |
| `Wlrix.Settings.Schemes`  | app  | Color scheme browser, after IRIX's. One Apply, whole desktop.       |
| `Wlrix.Settings.Login`    | app  | Login Settings: the greeter's keyboard, color scheme and displays.  |
| `Wlrix.Settings.Users`    | app  | User Accounts, after KDE's Users page: pictures, passwords, fingerprints. |
| `Wlrix.Pinentry`          | app  | The pinentry gpg-agent asks for passphrases with. IRIX-styled.      |
| `Wlrix.NetworkPrompt`     | app  | The Wi-Fi password dialog `wlrix-network` puts up. One question.    |
| `Wlrix.BluetoothPrompt`   | app  | The pairing dialog `wlrix-bluetooth` puts up: confirm, PIN, passkey, allow. |
| `Wlrix.PolkitAgent`       | app  | The polkit authentication agent: the password dialog for pkexec & co. |
| `Wlrix.Shutdown`          | app  | The Shut Down System dialog, behind the Toolchest's two items.      |
| `Wlrix.SourcePicker`      | app  | The screen-share picker `xdg-desktop-portal-wlrix` puts up.         |
| `Wlrix.SoftwareManager`   | app  | Package manager, after IRIX's `swmgr`.                              |
| `Wlrix.Archiver`          | app  | Archive browser and extractor, after KDE's Ark.                     |
| `Wlrix.Files`             | app  | The file manager, after IRIX's fm and KDE's Dolphin.                |
| `Wlrix.FilePicker`        | app  | The file dialog `xdg-desktop-portal-wlrix` puts up. On Files.Core.  |
| `Wlrix.Clock`             | app  | The desktop clock, after IRIX's: an analog face with a border only. |
| `Wlrix.ClipboardHistory`  | app  | The clipboard history popup, after KDE's. `Super+V` opens it.       |
| `Wlrix.ImageView`         | app  | The image viewer, after IRIX's imgview. Reads and writes SGI files. |
| `Wlrix.Writer`            | app  | The text editor, after NEdit, with tabs and a side-by-side split.   |
| `Wlrix.IconCatalog`       | app  | The Icon Catalog, after IRIX's: applications and control panels on notebook pages. |

Every project with tests has a `<project>.Tests` beside it under `tests/`. More apps (a terminal, among others) get
added as sibling projects.

## Installing

`wlrix-apps` had no packaging until the archiver needed to be an `xdg-open` handler. The `Justfile` covers the
applications with a desktop entry: those that open files, and those the Icon Catalog shows, the control panels among
them. The others are still launched by name from the Toolchest and the session.

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

## Text editing

`Wlrix.Writer` is NEdit's look with tabs added. The editing surface is
[AvaloniaEdit](https://github.com/AvaloniaUI/AvaloniaEdit), and Preferences ▸ Language Mode is
TextMate grammars through `AvaloniaEdit.TextMate`. Both are MIT. The tokenizer is Oniguruma,
reached through `Onigwrap`, a native library; the `linux-x64` publish ships one copy of it.

**Colors come from the session's scheme, not from the screenshot.** The text area, the text, the
selection and the line numbers are scheme brushes, so the editor repaints when the scheme does.
The highlighting palette has a light and a dark variant, picked by the scheme's own `IsDark`. Its
default foreground and background are filled in from the scheme at run time, so plain text always
matches it exactly.

**One window, many files.** The editor owns `com.wlrix.writer` on the session bus, the same
arrangement `Wlrix.Files` uses. A second `wlrix-writer notes.txt`, from a shell or a file manager,
hands its files to the open window as new tabs and exits without drawing anything. A name that does
not exist yet opens as an empty document that Save writes without asking for a name.

**Saves are a write and a rename.** The new text goes to a temporary file beside the original,
which gets the original's permission bits and is then renamed over it. A crash or a full disk
partway through therefore leaves the old file whole. A symbolic link is followed, so it survives as
a link. Where the directory cannot take a new file but the file itself is writable, the save falls
back to rewriting the file in place.

**Changes on disk are noticed by directory.** Each directory with an open file has one inotify
watch, because a file replaced by a rename leaves a watch on the old file watching nothing.
Events are only a hint: the file's time and size are compared with what the editor last read or
wrote, so its own saves never prompt. The prompt waits until the window has the focus.

**Search ▸ Find is NEdit's dialog.** It is modeless, so with Keep Dialog checked it stays beside
the text and each Find selects the next match. Every query is compiled to a .NET regular
expression; a plain string is escaped first, so Whole Word and Case Sensitive work the same way for
both kinds. The search starts from the selection and wraps around the end of the document, and the
dialog says when it wrapped or found nothing. AvaloniaEdit's own Ctrl+F search panel is removed, so
there is only one search.

**Search ▸ Replace is NEdit's Replace/Find,** with the same options, the same history and the same
Keep Dialog. Replace replaces the selection if it is an occurrence and otherwise the next one, and
Replace & Find then selects the occurrence after it. "Replace all in" is scoped to tabs: Tab,
Selection, and All Tabs in place of NEdit's Multiple Files. Each Replace All is one undo step per
document. A regular expression's replacement takes .NET substitutions (`$1`, `${name}`, `$&`), not
NEdit's `\1` and `&`.

AvaloniaEdit's templates look up Simple-theme resource keys such as `FontSizeNormal` and
`ThemeBorderThickness`, which `WlrixTheme` does not define, so the editor's `App.axaml` defines
them. AvaloniaEdit also hosts its line numbers in an `ItemsControl`. `WlrixTheme` has templated
that control since 0.4.3. Before that, an `ItemsControl` rendered as nothing, and every app that
used one carried its own template.

`Program` also names a default font family. AvaloniaEdit asks for one while it is being
constructed, and on a system where fontconfig gives Skia no default, Avalonia's `$Default` throws.

## Icon Catalog

`Wlrix.IconCatalog` (`wlrix-icon-catalog`, Toolchest → Find → Icon Catalog) replaced the Toolchest's
Applications menu. Its notebook pages, tabs along the bottom as IRIX had them, come in two kinds:

- **System pages** fill themselves from the installed desktop entries: ControlPanels first, then one page per
  freedesktop main category. An application is on every page its categories name, so Steam is under both Games and
  Internet. A category with nothing in it has no tab.
- **Custom pages** are the user's, made, renamed and removed from the Page menu.

**Drop anything on any page.** A launcher, a program or a document dragged from Files or the desktop stays on that
page. On a system page, Selected ▸ Remove hides an installed application there, and dropping it back un-hides it.
What the user changed is kept in `~/.local/share/wlrix/icon-catalog/state.json`. An icon whose application has been
uninstalled is skipped but remembered, so it comes back if the application does.

**Installs and removals show up on their own.** Every `applications` directory is watched with inotify, and a burst of
changes is one rescan half a second after it settles.

**Icons stand on the desktop's magic carpet**, flat when the application is not running and upright when it is,
tinted by the same rule as Files: stippled at rest, white under the pointer, yellow when selected, and knocked back
when the window does not have the keyboard. Running state comes from `ext-foreign-toplevel-list-v1`, matched the way
`wlrix-desktop` matches it, plus the processes the catalog started itself. wlRIX's own settings panels mark themselves
`X-WLRIX-ControlPanel=true` in their desktop entries and stand on the control panel carpet. Every second row of the
grid is pushed right by half a cell, as in IRIX's catalog.

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

## Login Settings

`Wlrix.Settings.Login` sets the login screen's keyboard layout, color scheme and displays. It is an exception to the
rule above: those are the machine's settings, not a user's, so they are not the settings daemon's either. They live
in `/etc/wlrix/greeter.toml`, a `compositor.toml` that `wlrix-greeter-start` hands to the greeter's compositor with
`--config` (see the wlrix-greeter README). The panel reads that file directly, since it is world-readable, and writes it
through `wlrix-login-helper` under `pkexec`, the way the Software Manager runs `wlrix-pkg-helper`. The helper takes
values, never a path: it re-validates them, splices them into the file (keeping every other section and comment), has
`wlrix-compositor --check-config` accept the result, and renames it into place. Use System Defaults removes the file.

The Copy menu fills the form in from the person using it — the keyboard and scheme from the settings daemon, the displays
as the compositor reports them through `Wlrix.Settings.Outputs` — and, like every other change here, waits for Apply.

```sh
dotnet run --project src/Wlrix.Settings.Login -- --demo   # made-up displays
dotnet run --project src/Wlrix.Settings.Login.Helper -- --check write --layout jp --palette gotham
```

## User Accounts

`Wlrix.Settings.Users` (`wlrix-settings-users`, Toolchest → System → User Accounts) is modeled on KDE's Users page in
IRIX dress. It keeps nothing of its own: every account is AccountsService's (`org.freedesktop.Accounts`), and polkit,
through `wlrix-polkit-agent`, asks for whatever password a change needs.

- **The list** has *Your Account* first, then *Other Accounts*, each with a square picture -- or the initials on a
  colored square when there is none -- the full name and the login name. **Add New...** asks for a full name, a login
  name suggested from it until typed over, the account type and a password.
- **Name, Username, Account type, Email** collect until **Apply** writes them together; **Reset** drops them. Leaving
  an account with changes pending asks whether to apply them first.
- **Your own account** cannot change its login name or account type, or delete itself.
- **At least one administrator** stays: demoting or deleting the last unlocked one is refused, unless root clearly
  works (AccountsService reports it unlocked, with an ordinary password). A locked root, Ubuntu's `*`, keeps the rule.
- **The picture** is clicked to change: a stock picture from `share/wlrix/avatars` (`wlrix-assets`; a person adds
  their own in `~/.local/share/wlrix/avatars`), any image file, center-cropped to a 256 px square, or **Remove**. For
  your own account `~/.face.icon` and `~/.face` are written too, which the greeter and the lock screen read first.
- **Change Password...** hashes here, with libxcrypt's default method (yescrypt), and hands AccountsService the hash.
- **Fingerprints...** is shown for your own account when fprintd is running and has a reader. It lists the enrolled
  fingers, deletes one or all, and enrolls another, touch by touch with fprintd's retry messages. The reader is
  claimed only while the dialog is open. Under the list, when pam_fprintd is installed, **Use a fingerprint for**:
  - *Unlocking the screen* is this person's `lock.toml` `[unlock] fingerprint`, through the settings daemon. The lock
    screen takes the finger and the password at the same time (see wlrix-lock's README).
  - *sudo in a terminal* and *Administrator password prompts* are for the whole machine, and ask for an
    administrator's password: `wlrix-users-helper`, through pkexec (`com.wlrix.settings.users.fingerprint`), adds a
    marker comment and `-auth sufficient pam_fprintd.so` just before the first auth line of `/etc/pam.d/sudo` (and
    `sudo-i` when it has a stack of its own) or `/etc/pam.d/polkit-1`, or removes every pam_fprintd line again. The
    finger is asked for first; the password follows a timeout, three mismatches or Ctrl+C. The `-` makes PAM skip the
    line if fprintd is uninstalled later. A service only in `/usr/lib/pam.d` is copied to `/etc/pam.d` to be edited,
    and the copy is removed when turning it off makes it the vendor's again. When `system-auth` or `common-auth`
    already loads pam_fprintd, both switches show on and grayed: that is the system's setting, not this panel's.
- **Delete User...** asks whether to delete the home directory too; Cancel is the default.

## Passphrases

`Wlrix.Pinentry` (`wlrix-pinentry`) is a pinentry: gpg-agent starts it for each passphrase, PIN or confirmation and
speaks the Assuan protocol to it over stdin and stdout, exactly as it does to GnuPG's own pinentries. It shows the same
IRIX dialog as the polkit agent, using the description, prompt and button labels gpg-agent sends (already in the user's
language). Without a `WAYLAND_DISPLAY` -- gpg over ssh, or on a VT -- it hands the conversation to `pinentry-curses`.

The wlRIX install makes it the machine's default: the epoch's `install-cs` writes `/etc/gnupg/gpg-agent.conf`
(`data/gpg-agent.conf.in`), which GnuPG 2.4 reads before each user's own `~/.gnupg/gpg-agent.conf`. That is safe for
every desktop on the machine, because outside a wlRIX session (`XDG_CURRENT_DESKTOP`) it hands the conversation to the
distribution's `/usr/bin/pinentry`, which picks the Qt, GNOME or GTK pinentry as it would have anyway. An existing
`/etc/gnupg/gpg-agent.conf` is the administrator's and is left alone; `install-cs` prints the line to add instead, and
`uninstall` removes the file only while it is unchanged. A user can choose another pinentry in their own file:

```
pinentry-program /usr/bin/pinentry-qt
```

and gpg-agent picks up either file on `gpgconf --reload gpg-agent`. Not offered: the quality bar for new passphrases,
generated passphrases and saving to an external password manager; gpg-agent works without all three.

## Audio

`Wlrix.Settings.Audio` is the Audio Panel, after IRIX's `apanel`, and wlRIX's replacement for pavucontrol. It is the
exception to the rule above: volume and devices belong to the sound server, not to a config file, so the panel talks to
it directly through **libpulse** (`libpulse.so.0`, P/Invoke, no binding package). That is the client side of
PulseAudio and of PipeWire's pipewire-pulse alike, so it needs `libpulse` installed at runtime either way. The one
PipeWire-only setting, the graph's sample rate, goes through `pw-metadata`; on PulseAudio that menu is disabled.

```sh
dotnet run --project src/Wlrix.Settings.Audio -- --demo   # the IRIX screenshot's devices; touches nothing
```

## Sounds

`Wlrix.Settings.Sound` is IRIX's System & Desktop Sounds panel, for the sounds `wlrix-sound` plays. Its settings come
in two kinds, and they go two different ways:

- **Mute System** is the sound server's. On IRIX it muted the machine, not the desktop's sounds, so it is the default
  output's mute, set through `wpctl`. It is the same mute the Audio Panel shows.
- **Everything else** is `sounds.toml`, written through `wlrix-settings-daemon` like any other panel's settings:
  - the kinds of sound (`sounds.play.*`): IRIX's four checkboxes plus *Window Sounds*, which IRIX did not have;
  - the output desktop sounds play through (`sounds.output`, a sink listed from `pw-dump`);
  - the sound scheme (`sounds.theme`, the installed themes from `com.wlrix.Sound1.ListThemes`).

Turning a kind of sound on plays one of its sounds, and so does picking a new scheme or output, through
`com.wlrix.Sound1.Preview`. IRIX's *Key Click* is left out: it was the keyboard's own hardware, and the nearest thing
here, a sound per keypress, is not worth the latency.

## Mouse

`Wlrix.Settings.Mouse` is IRIX's Mouse Settings panel. Everything it sets is in `compositor.toml`, written through
`wlrix-settings-daemon`. The top panel has a tab per kind of device, hanging below it, and the cursor theme has a panel of
its own underneath, since it is the same whatever is pointing. Both tabs are kept the height of the taller, so switching
does not resize the window. `wlrix-settings-mouse --touchpad` opens on the Touchpad tab.

The **Mouse** tab:

- **Mouse Acceleration** is `compositor.mouse.acceleration`, libinput's pointer speed from -1 to 1. The five diamonds are
  -1, -0.5, 0, 0.5 and 1; **Finer Control** shows a slider for the values between them. IRIX's *Threshold* slider is
  left out, because libinput has no threshold: its acceleration is a curve over pointer speed, not a step.
- **Mouse Mapping** is `compositor.mouse.left_handed`, libinput's button swap.
- **Click Speed** is `compositor.mouse.double_click_ms`. The diamonds are 600, 400 and 250 ms, Slow to Fast, and the
  slider reads the same way round. The compositor uses it for the window-menu button, and the portal passes it to GTK
  as `org.gnome.desktop.peripherals.mouse` `double-click`. Avalonia applications do not follow it yet: they take their
  double-click time from `Avalonia.FreeDesktop`, which does not ask the portal for it.
- **Test** times your clicks against the Click Speed currently set, including a value nobody else has read yet. It says
  *Double Click* at once, and *Single Click* only once the double-click time has passed without a second click.
- **Cursor Theme**, wlRIX's addition, is `compositor.cursor.theme`, listing the themes that have a `cursors` directory
  where an XCursor loader looks, with a row of each theme's pointers. It is written only when it is changed, so a file
  that names no theme keeps following `XCURSOR_THEME`.

The **Touchpad** tab, wlRIX's own, is `compositor.touchpad.*`. Mouse Mapping and Click Speed apply to touchpads too:
they belong to the person, not the device.

- **Touchpad Acceleration**, the same diamonds and Finer Control as the mouse's, kept separate because the two are
  rarely right at one value.
- **Tapping**: tap to click (on by default, since a pad with no buttons is unusable without it; two fingers is the
  secondary click, three the middle), tap and drag, and drag lock. The last two are grayed out with tapping off.
- **Scrolling**: two fingers or the right edge, and natural scrolling.
- **Secondary Click**, for pads with no separate buttons: the pad's own default, a two-finger press, or the bottom-right
  corner. The pad's own default removes the key, leaving libinput to choose per model; scroll does the same while it
  matches the default.
- **Disable while typing**, and **Turn off while a mouse is connected**.

Whether there is a touchpad comes from udev's database (`ID_INPUT_TOUCHPAD=1` in `/run/udev/data/c13:*`, which anyone
can read), since libinput's devices belong to the compositor. With none, the tab says so and its settings still apply
to one connected later.

Under the nested compositor there is no libinput, and the host decides acceleration, button mapping and everything on
the Touchpad tab.

## Bluetooth

`Wlrix.Settings.Bluetooth` (`wlrix-settings-bluetooth`, Toolchest → System → Bluetooth Properties, and the last row
of the Bluetooth icon's menu) talks to bluetoothd directly, as Network Settings talks to NetworkManager. **This
Computer** switches the radio (grayed out at a hardware switch), makes the computer visible to other devices, and has
the tray-icon checkbox (`bluetooth.indicator`, through the settings daemon). **Devices** lists paired devices first,
then the ones a search finds -- one runs for as long as the window is open and Bluetooth is on -- leaving out nameless
beacons. **Pair** pairs, then trusts and connects the device; **Forget** asks first. The state is read again every
second, one `GetManagedObjects` for everything.

Pairing questions do not come to the panel: bluetoothd puts them to the default agent, which is `wlrix-bluetooth`,
and it shows `Wlrix.BluetoothPrompt` (`wlrix-bluetooth-prompt`) -- one line of JSON in, one out, as the network
prompt does. Its modes are *confirm* (does the device show this number?), *display* (type this on the keyboard being
paired, closed by the daemon when it is done), *pin*, *passkey* and *authorize*.

## Power

`Wlrix.Settings.Power` (`wlrix-settings-power`, Toolchest → System → Power Properties) is wlRIX's own; IRIX ran on
workstations with no lid. Everything it sets is in `idle.toml`, written through `wlrix-settings-daemon`, and acted on
by `wlrix-idle`, which holds logind's lid inhibitor so the person's choice wins over `/etc/systemd/logind.conf`:

- **When the Lid Is Closed** is `idle.lid.on_battery`, `idle.lid.on_ac` and `idle.lid.docked`: suspend, hibernate,
  lock the screen (lock and turn it off, stay awake), turn the screen off, or do nothing. *With another monitor* is
  logind's own test, a dock or more than one display; the compositor switches the laptop's panel off then whatever is
  chosen. A machine that cannot hibernate is locked instead. On a computer with no lid (no `/proc/acpi/button/lid/*`)
  the panel says so, and the settings still apply on one that has.
- **When Idle** is `idle.plugged_in.*` and `idle.on_battery.*`, the two profiles `wlrix-idle` switches between as the
  power source changes: when to dim, when to turn the screen off and whether to lock then, and when to sleep, each from
  one minute to an hour or Never, plus any odd value a hand-edited file holds. The On battery column is shown only on
  a computer with a battery (`/sys/class/power_supply`, a `Battery` not scoped to a device). Until it is changed it
  follows the plugged-in column -- that is what applies with no `[on_battery]` in the file -- and the section is written
  only once it is.
- **Lock the screen before sleeping** is `idle.before_sleep.lock`, on by default: every suspend, from the lid or
  anywhere else, locks first.

Changes apply at once; there is nothing to drag. The daemon checks a write with the *installed* `wlrix-idle
--check-config`, so install `wlrix-idle` before using the panel, or the `[lid]` keys are refused.

## Phones

The file manager lists Android phones and other MTP devices (a phone set to **File Transfer** in its USB notification)
under Devices, beside the disks. It talks to them through **libmtp** (`libmtp.so.9`, P/Invoke) and finds them through
**libudev** (`libudev.so.1`), so both are needed at runtime. libmtp's package also installs the udev rules that matter:
they tag the device `ID_MTP_DEVICE=1`, which is how it is found, and `uaccess`, which is what lets the person at the
seat open it without root.

While the file manager has a phone open, nothing else can open it: libmtp claims the USB interface. **Disconnect** in
the device's context menu releases it. Files on the phone open in other applications as a copy, because no application
can read `mtp://`; saving that copy does not change the phone.

## Build

```sh
dotnet build
dotnet run --project src/Wlrix.Toolchest
```

## The local NuGet feed

`localfeed/` is a NuGet source (see `nuget.config`) holding packages that are not on nuget.org:
the wlRIX Avalonia theme and dialogs, and a **patched `Avalonia.Wayland`**. It is gitignored, so a fresh clone has to
rebuild it before `dotnet restore` will work.

The Wayland patches, on the `wlrix-12.1.4` branch of the [Avalonia fork](https://github.com/vic485/Avalonia)
(upstream's `12.1.4` tag plus the wlRIX commits). App id support used to be one of them; it is upstream as of 12.1.4
(`WaylandPlatformOptions.AppId`, defaulting to the entry assembly name), so the apps get it from the real release.
What remains:

- **`CanResize=false` on the wire.** Mapped onto `min == max` size constraints, which is the only spelling xdg-shell has
  for "this window is a fixed size". `wlrix-compositor` reads it to drop the maximize button and the resize grips (see
  its README, *Window capabilities*). `SetCanMaximize`
  and `SetCanMinimize` stay no-ops: the protocol has no request for either, and
  `xdg_toplevel.wm_capabilities` runs the other way, compositor to client.
- **`Activate()` via `xdg_activation_v1`.** Raises an existing window on request, which Files' Classic mode needs to
  bring forward a directory that is already open.
- **Drag icons.** A `WaylandDragImage` item on the drag's `IDataTransfer` becomes the `start_drag` icon surface, and
  the `dnd-` cursor names are tried before the generic ones.
- **Popup grabs.** A popup that takes focus (menus, context menus) asks for `xdg_popup.grab`, so the compositor
  dismisses it on an outside click.
- **The desktop's double-click time.** Read from the settings portal's `org.gnome.desktop.peripherals.mouse`
  `double-click`, which `xdg-desktop-portal-wlrix` serves from the compositor's `[mouse] double_click_ms`, and followed
  when it changes. Upstream uses a fixed 500 ms on Linux.
- **Smaller fixes.** `XCURSOR_THEME` is honored, key repeat stops when a modal dialog disables its owner, and
  pointer enter/leave events carry a timestamp.

### Rebuilding the localfeed

`localfeed/` is **committed**. Two of the four packages in it exist nowhere else, so a checkout without them restores to
`NU1101` rather than to something merely unpatched, and that would take CI and every fresh clone with it. The four total
under 400 KB and change only when a pinned version is bumped. Whatever you rebuild, commit the result.

```bash
cd ../Avalonia && git checkout wlrix-12.1.4 && git submodule update --init --recursive
```

```bash
dotnet pack src/Avalonia.Wayland/Avalonia.Wayland.csproj -c Release -p:PackageVersion=12.1.5-wlrix.2 -o /tmp/wl
```

Two things then need fixing up by hand, because upstream's own packages are assembled by Nuke (`numerge.json`) rather
than by `dotnet pack`, and running the whole Nuke pipeline for one library is not worth it:

1. The nuspec's dependency block lists `Avalonia.Dialogs`, which upstream **merges into** the
   `Avalonia` package. Replace the whole block with the three dependencies upstream's own
   `Avalonia.Wayland` declares: `NWayland 0.11.0`, `Avalonia 12.1.4`, `Avalonia.FreeDesktop 12.1.4`.
2. Those versions come out matching `PackageVersion` rather than the source version. They must say **12.1.4** — the tag
   the code is built from, and what the assembly references resolve to.

Then drop the `.nupkg` into `localfeed/`, delete the version it replaces, bump
`Avalonia.Wayland` in `Directory.Packages.props` to match, and commit all three changes together — a bumped pin without
its package is a red CI run. Bump the `-wlrix.N` suffix on every rebuild: NuGet caches by id and version, so reusing a
version means the old one is served from
`~/.nuget/packages` and the new bits are silently ignored.
