#!/usr/bin/env just --justfile
#
# Packaging for the wlRIX applications that own a file type.
#
# Most of the C# applications are launched by name from the Toolchest and the session, and the
# epoch's `install-cs` installs those. The ones here additionally register a `.desktop` entry,
# because something on the system opens files with them -- and adding one is a line in `apps`
# and a `data/*.desktop` beside the project.
#
# The epoch installs the payloads for every C# application, including these; `install-desktop`
# is the half it delegates back here, so the entries have one source of truth. `install` does
# both, for working on one application without a full epoch install.

rootdir := ''
prefix := '/usr'

usrdir := absolute_path(clean(rootdir / prefix))
bindir := usrdir / 'bin'
# The payload, not the launcher. A published .NET application is a directory of assemblies that
# the executable finds relative to itself, so it cannot live in bindir; `/usr/bin` gets a symlink
# to the host, which resolves through it because the apphost reads /proc/self/exe.
libdir := usrdir / 'lib' / 'wlrix'
appsdir := usrdir / 'share' / 'applications'

# The applications with a desktop entry, as `<project>:<installed name>:<entry>`.
#
# The installed name matches the epoch's `cs_apps`, and so does the directory under libdir --
# the two install the same payload to the same place, and disagreeing would leave whichever ran
# second shadowing a stale copy of the other.
apps := "Wlrix.Archiver:wlrix-archiver:com.wlrix.archiver.desktop \
         Wlrix.Files:wlrix-files:com.wlrix.files.desktop"

# List available recipes.
default:
  @just --list

# Build the applications for installation.
#
# `-r linux-x64` is not optional here, whatever the target machine is. A RID-agnostic publish
# ships `runtimes/` for every platform Avalonia supports -- Windows and macOS natives included --
# which is 567 MB against 28 MB for this. `--self-contained false` because the runtime is a
# distro package's job.
[doc("Publish the applications, Linux-only and framework-dependent")]
publish:
  #!/usr/bin/env bash
  set -euo pipefail
  for entry in {{apps}}; do
      project="${entry%%:*}"
      echo "==> publishing $project"
      dotnet publish "src/$project" -c Release -r linux-x64 --self-contained false \
          --nologo -o "src/$project/bin/Release/publish"
  done

# Install the applications, their launchers and the entries that make xdg-open route to them.
#
# Deliberately does not build: this is normally run as root, and building as root leaves obj/
# and bin/ owned by root for the next ordinary build.
#
#     just publish && sudo just install
[doc("Install the applications and register their file types (publish first; run as root)")]
install: install-desktop
  #!/usr/bin/env bash
  set -euo pipefail
  for entry in {{apps}}; do
      project="${entry%%:*}"
      rest="${entry#*:}"
      name="${rest%%:*}"
      out="src/$project/bin/Release/publish"

      if [ ! -x "$out/$name" ]; then
          echo "no published build of $project -- run 'just publish' first" >&2
          exit 1
      fi

      # `install -d` on bindir too: it exists on a live system but not under a staging rootdir,
      # and `ln` does not create parents the way `install -D` does.
      install -d '{{libdir}}' '{{bindir}}'
      # Cleared, not copied over: `cp` opens the destination for writing, which fails with
      # ETXTBSY if that binary is running, and reinstalling while the desktop is up is the
      # ordinary case. Unlinking first is safe even then -- the running process keeps its
      # inode -- and it also drops files a newer publish no longer produces.
      rm -rf "{{libdir}}/$name"
      install -d "{{libdir}}/$name"
      cp -r "$out/." "{{libdir}}/$name/"
      chmod 0755 "{{libdir}}/$name/$name"
      # The link target is the *installed* path, not the staged one, so a package built into a
      # rootdir still points somewhere real once unpacked.
      ln -sfn '{{prefix}}/lib/wlrix/'"$name/$name" "{{bindir}}/$name"
      echo "installed {{bindir}}/$name -> {{libdir}}/$name"
  done

# Install only the desktop entries.
#
# The epoch's `install-cs` publishes and installs every C# application's payload, this repo's
# applications included, and then calls this for the half it does not know about. Separating
# them is what keeps the entries from having two definitions that can drift.
[doc("Install just the desktop entries and refresh the MIME cache (run as root)")]
install-desktop:
  #!/usr/bin/env bash
  set -euo pipefail
  for entry in {{apps}}; do
      project="${entry%%:*}"
      desktop="${entry##*:}"
      install -Dm0644 "src/$project/data/$desktop" '{{appsdir}}'"/$desktop"
      echo "installed {{appsdir}}/$desktop"
  done

  # The step that actually makes `xdg-open backup.tar.gz` work. Without it the entries are on
  # disk and nothing has indexed their MimeType=, so every lookup still misses. Skipped when
  # staging into a rootdir, where the cache belongs to the package manager's post-install.
  #
  # It does *not* make anything the default handler for its types; that is a user-scope write
  # to mimeapps.list, and `set-default-apps` or the application's own Options menu does it.
  if [ -z '{{rootdir}}' ] && command -v update-desktop-database >/dev/null; then
      update-desktop-database '{{appsdir}}'
      echo "updated the desktop MIME cache"
  fi

[doc("Remove the applications and their desktop entries")]
uninstall:
  #!/usr/bin/env bash
  set -euo pipefail
  for entry in {{apps}}; do
      rest="${entry#*:}"
      name="${rest%%:*}"
      desktop="${entry##*:}"
      rm -f "{{bindir}}/$name"
      rm -rf "{{libdir}}/$name"
      rm -f '{{appsdir}}'"/$desktop"
      echo "removed $name"
  done
  if [ -z '{{rootdir}}' ] && command -v update-desktop-database >/dev/null; then
      update-desktop-database '{{appsdir}}'
  fi

# Make wlRIX the system-wide default for the types its applications own.
#
# Opt-in, and separate from `install`, because claiming the default handler for every folder on
# a machine is not a decision an installer gets to take quietly. The per-user equivalent is in
# each application's own Options menu, which writes ~/.config/mimeapps.list instead.
[doc("Register the wlRIX applications as system defaults (run as root)")]
set-default-apps:
  #!/usr/bin/env bash
  set -euo pipefail
  target='{{usrdir}}/../etc/xdg/mimeapps.list'
  target="$(realpath -m "$target")"
  install -d "$(dirname "$target")"
  touch "$target"

  # Read-modify-write on the group, not a rewrite of the file: /etc/xdg/mimeapps.list is a
  # system administrator's file and may already say what opens a PDF.
  python3 - "$target" <<'PY'
  import sys, re
  path = sys.argv[1]
  wanted = {"inode/directory": "com.wlrix.files.desktop"}
  lines = open(path).read().splitlines()
  group = "[Default Applications]"
  if group not in [l.strip() for l in lines]:
      if lines and lines[-1].strip():
          lines.append("")
      lines.append(group)
  out, inside = [], False
  seen = set()
  for line in lines:
      stripped = line.strip()
      if stripped.startswith("[") and stripped.endswith("]"):
          if inside:
              for mime, entry in wanted.items():
                  if mime not in seen:
                      out.append(f"{mime}={entry};")
                      seen.add(mime)
          inside = stripped == group
      elif inside and "=" in stripped:
          mime = stripped.split("=", 1)[0].strip()
          if mime in wanted:
              out.append(f"{mime}={wanted[mime]};")
              seen.add(mime)
              continue
      out.append(line)
  if inside:
      for mime, entry in wanted.items():
          if mime not in seen:
              out.append(f"{mime}={entry};")
  open(path, "w").write("\n".join(out) + "\n")
  print(f"set {', '.join(wanted)} in {path}")
  PY

# Check the desktop entries parse and every MIME type in them is one the system knows.
#
# Worth having as a recipe rather than a habit: the entry format has no line continuations, so a
# MimeType= list wrapped for readability parses as several broken lines and the file still
# *looks* right in an editor.
[doc("Validate the desktop entries")]
check-desktop:
  #!/usr/bin/env bash
  set -euo pipefail
  for entry in {{apps}}; do
      project="${entry%%:*}"
      desktop="${entry##*:}"
      desktop-file-validate "src/$project/data/$desktop"
      echo "$desktop: valid"
  done

build:
  dotnet build -c Release --nologo

test:
  dotnet test -c Release --nologo
