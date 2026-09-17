# Fixtures

Committed rather than generated, because each one is a real shape that caused a
question, and regenerating them would lose that.

- **`mountinfo-desktop.txt`** — a trimmed `/proc/self/mountinfo` from a btrfs machine.
  Kept for the four cases that matter and that `/proc/mounts` cannot express:
  - `/` and `/home` are **different subvolumes of one btrfs device**, so they share a
    `major:minor` (`0:24`). A move between them really is a rename.
  - `/mnt/scratch` and `/mnt/work` are a **bind mount pair** on `8:17` — different
    mount points, one filesystem, again renameable.
  - `/mnt/tag\040space` exercises the kernel's **octal escaping** of a space.
  - `/boot`, `/mnt/nas` and the pseudo-filesystems check the real-filesystem allowlist,
    and `/mnt/nas` is mounted `ro`.

- **`mime/`** — a trimmed shared-mime-info database. Never read `/usr/share/mime` in a
  test: CI's shared-mime-info version differs from any developer's, so an assertion
  about what `.md` resolves to would fail for reasons unrelated to this code. Each
  entry is here for a case:
  - `*.tar.gz` against `*.gz` — the longest-suffix rule, and the one that decides
    whether a tarball opens in the archiver.
  - `core` and `*.gs` carry `:cs`, so a case-insensitive match must not claim `CORE`.
  - `Makefile`/`makefile` are literals, which must beat any wildcard that also matches.
  - `README*` has weight 20 and `*.html` has 80, so ties resolve by weight rather than
    by file order.
  - `sconscript.*` and `*~` are patterns that are neither literal nor a plain extension.
