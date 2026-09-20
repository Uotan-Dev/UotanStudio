# Vendored upstream magic corpus

Source: `file/file` (chrstos/testdisk), branch `master`, fetched 2026-09-19.
This is build-time input for the `UotanStudio.Firmware.MagicGen` generator; it is
NOT embedded or parsed at runtime by `UotanStudio.Firmware`.

- `Magdir/*` — upstream magic(5) text fragments.
- `Header`, `Localstuff` — non-Magdir fragments upstream compiles first.
- `LOAD_ORDER.txt` — canonical fragment order taken from `magic/Makefile.am`
  (`MAGIC_FRAGMENTS`), reproduced so generated tie-breaking matches upstream.

Upstream file(1) is distributed under the BSD-4-Clause (UC Berkeley) license
reproduced in the `file` repository `LICENSE`.
