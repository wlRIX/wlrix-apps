# Test archives

Committed rather than generated at test time, because the interesting cases are the ones a writing library will not
produce. Python's `zipfile` sets general-purpose bit 11 and writes UTF-8 for any non-ASCII name — which is the case that
already works — so `sjis-names.zip` was built by overriding its private name encoder. Regenerating these by hand would
quietly turn the regression tests into tests of nothing.

| File                 | What it is for                                                                                                |
|----------------------|---------------------------------------------------------------------------------------------------------------|
| `sjis-names.zip`     | CP932 filenames with **bit 11 clear**. The mojibake regression.                                               |
| `utf8-names.zip`     | UTF-8 filenames with **bit 11 set**. Detection must not second-guess it.                                      |
| `no-dir-entries.zip` | `a/b/c.txt` and friends with no directory records. Parents must be synthesized.                               |
| `dos-attrs.zip`      | External attributes holding FAT bits, not a Unix mode. The Mode column must stay blank.                       |
| `special-bits.tar`   | A plain, a sticky and a setgid directory plus a setuid file. SharpCompress reports the first two identically. |
| `modes.tar`          | Modes, uid/gid and a symlink, for the Mode/Owner/Group columns.                                               |
| `modes.tar.gz`       | The same tar gzipped, for compound-extension format detection.                                                |

The generator lives in this repository's history alongside the commit that added them; the archives themselves are the
fixture.
