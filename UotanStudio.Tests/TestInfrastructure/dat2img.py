#!/usr/bin/env python3
"""Independent reference decoder for Android block-OTA *full-update* DAT pairs.

This decoder is written from the documented AOSP ``blockimgdiff`` transfer-list
text grammar, deliberately NOT derived from the UotanStudio managed C# reader, so
that a byte-identical reconstruction is a genuine cross-implementation check of
the DAT file-format interpretation (range-set grammar, block layout and the
sequential new.dat consumption rule).

Scope: the full-update commands only -- ``new``, ``zero`` and ``erase``. Any
incremental command (``move`` / ``stash`` / ``free`` / ``bsdiff`` / ``imgdiff``)
requires a source image and is rejected, because a full OTA is the only input a
source-less decoder can reconstruct exactly.

Usage: dat2img.py <new.dat> <transfer.list> <output.img>

Block size is fixed at 4096 bytes per the transfer block size. The output image
is sized to the highest destination block boundary; blocks never covered by a
``new`` command remain zero, matching an unwritten flash region.
"""

import sys

BLOCK = 4096


def fail(message):
    sys.stderr.write("dat2img: " + message + "\n")
    raise SystemExit(1)


def parse_rangeset(token):
    fields = token.split(",")
    if len(fields) < 3:
        fail("range set too short: %r" % token)
    count = int(fields[0])
    if count == 0 or count % 2 != 0 or count != len(fields) - 1:
        fail("range set must declare an even endpoint count: %r" % token)
    ranges = []
    i = 1
    while i < len(fields):
        start = int(fields[i])
        end = int(fields[i + 1])
        if end <= start:
            fail("invalid range %d,%d" % (start, end))
        ranges.append((start, end))
        i += 2
    return ranges


def read_header(lines):
    version = int(lines[0])
    if version < 1 or version > 4:
        fail("unsupported transfer-list version %d" % version)
    index = 2
    if version >= 2:
        # stash entry count and maximum simultaneously stashed blocks.
        index = 4
    return version, index


def main(argv):
    if len(argv) != 4:
        sys.stderr.write("usage: dat2img.py <new.dat> <transfer.list> <output.img>\n")
        return 2
    dat_path, list_path, out_path = argv[1], argv[2], argv[3]

    with open(list_path, "r", encoding="utf-8") as handle:
        lines = [line.strip() for line in handle if line.strip() != ""]
    if len(lines) < 3:
        fail("transfer list header is truncated")
    _version, index = read_header(lines)

    commands = []
    total_blocks = 0
    for line in lines[index:]:
        tokens = line.split()
        kind = tokens[0]
        if kind in ("new", "zero", "erase"):
            ranges = parse_rangeset(tokens[1])
        elif kind in ("move", "stash", "free", "bsdiff", "imgdiff"):
            fail("incremental command %r is not supported by the full-OTA decoder" % kind)
        else:
            fail("unknown command %r" % kind)
        for start, end in ranges:
            total_blocks = max(total_blocks, end)
        commands.append((kind, ranges))

    with open(dat_path, "rb") as dat, open(out_path, "wb") as out:
        out.truncate(total_blocks * BLOCK)
        for kind, ranges in commands:
            if kind == "new":
                for start, end in ranges:
                    need = (end - start) * BLOCK
                    chunk = dat.read(need)
                    if len(chunk) != need:
                        fail("unexpected end of new.dat")
                    out.seek(start * BLOCK)
                    out.write(chunk)
            else:  # zero / erase leave their blocks as pre-zeroed space.
                for start, end in ranges:
                    out.seek(start * BLOCK)
                    out.write(b"\x00" * ((end - start) * BLOCK))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
