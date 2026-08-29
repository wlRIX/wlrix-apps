#!/usr/bin/env just --justfile
#
# Packaging for the wlRIX applications.
#
# Only `Wlrix.Archiver` is wired up so far. The others are launched by name from the Toolchest
# and the session and have never been installed from here; adding one is a `publish` line, an
# `install` line and — if it opens files — a `data/*.desktop` beside it.

rootdir := ''
prefix := '/usr'

usrdir := absolute_path(clean(rootdir / prefix))
bindir := usrdir / 'bin'
# The payload, not the launcher. A published .NET application is a directory of assemblies that
# the executable finds relative to itself, so it cannot live in bindir; `/usr/bin` gets a symlink
# to the host, which resolves through it because the apphost reads /proc/self/exe.
libdir := usrdir / 'lib' / 'wlrix'
appsdir := usrdir / 'share' / 'applications'

archiver := 'wlrix-archiver'
archiver-proj := 'src' / 'Wlrix.Archiver'
archiver-out := archiver-proj / 'bin' / 'Release' / 'publish'
archiver-desktop := 'com.wlrix.archiver.desktop'

# List available recipes.
default:
  @just --list

# Build the archiver for installation.
#
# `-r linux-x64` is not optional here, whatever the target machine is. A RID-agnostic publish
# ships `runtimes/` for every platform Avalonia supports — Windows and macOS natives included —
# which is 567 MB against 28 MB for this. `--self-contained false` because the runtime is a
# distro package's job.
[doc("Publish the archiver, Linux-only and framework-dependent")]
publish:
  dotnet publish {{archiver-proj}} -c Release -r linux-x64 --self-contained false \
      --nologo -o {{archiver-out}}

# Install the archiver, its launcher and the entry that makes xdg-open route archives to it.
#
# Deliberately does not build: this is normally run as root, and building as root leaves obj/
# and bin/ owned by root for the next ordinary build.
#
#     just publish && sudo just install
[doc("Install the archiver and register it as an archive handler (publish first; run as root)")]
install:
  #!/usr/bin/env bash
  set -euo pipefail
  if [ ! -x '{{archiver-out}}/{{archiver}}' ]; then
      echo "no published build -- run 'just publish' first" >&2
      exit 1
  fi

  # `install -d` on bindir too: it exists on a live system but not under a staging rootdir,
  # and `ln` does not create parents the way `install -D` does.
  install -d '{{libdir}}/archiver' '{{bindir}}'
  cp -r '{{archiver-out}}/.' '{{libdir}}/archiver/'
  chmod 0755 '{{libdir}}/archiver/{{archiver}}'
  # The link target is the *installed* path, not the staged one, so a package built into a
  # rootdir still points somewhere real once unpacked.
  ln -sfn '{{prefix}}/lib/wlrix/archiver/{{archiver}}' '{{bindir}}/{{archiver}}'
  install -Dm0644 '{{archiver-proj}}/data/{{archiver-desktop}}' \
      '{{appsdir}}/{{archiver-desktop}}'

  # The step that actually makes `xdg-open backup.tar.gz` work. Without it the entry is on disk
  # and nothing has indexed its MimeType=, so every lookup still misses. Skipped when staging
  # into a rootdir, where the cache belongs to the package manager's post-install instead.
  if [ -z '{{rootdir}}' ] && command -v update-desktop-database >/dev/null; then
      update-desktop-database '{{appsdir}}'
      echo "updated the desktop MIME cache"
  fi

  echo "installed {{bindir}}/{{archiver}} -> {{libdir}}/archiver"
  echo "installed {{appsdir}}/{{archiver-desktop}}"

[doc("Remove the archiver and its desktop entry")]
uninstall:
  #!/usr/bin/env bash
  set -euo pipefail
  rm -f '{{bindir}}/{{archiver}}'
  rm -rf '{{libdir}}/archiver'
  rm -f '{{appsdir}}/{{archiver-desktop}}'
  if [ -z '{{rootdir}}' ] && command -v update-desktop-database >/dev/null; then
      update-desktop-database '{{appsdir}}'
  fi
  echo "removed the archiver"

# Check the desktop entry parses and every MIME type in it is one the system knows.
#
# Worth having as a recipe rather than a habit: the entry format has no line continuations, so a
# MimeType= list wrapped for readability parses as several broken lines and the file still
# *looks* right in an editor.
[doc("Validate the desktop entries")]
check-desktop:
  #!/usr/bin/env bash
  set -euo pipefail
  desktop-file-validate '{{archiver-proj}}/data/{{archiver-desktop}}'
  echo "{{archiver-desktop}}: valid"

build:
  dotnet build -c Release --nologo

test:
  dotnet test -c Release --nologo
