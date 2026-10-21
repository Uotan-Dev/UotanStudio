#!/usr/bin/env bash
set -euo pipefail

RUN_DIR=""
FORMATS="EXT4,EROFS,F2FS"
WORK=/lab/work/fixtures

usage() {
  echo "usage: make-fixtures.sh --run-dir <dir> [--formats EXT4,EROFS,F2FS]" >&2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --run-dir) RUN_DIR=$2; shift 2 ;;
    --formats) FORMATS=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

[ -n "$RUN_DIR" ] || { usage; exit 2; }
mkdir -p "$RUN_DIR/images/original" "$RUN_DIR/logs" "$WORK"
rm -rf "$WORK"/*
source_tree="$WORK/source"
mkdir -p "$source_tree/empty" "$source_tree/nested/深层"
printf 'source-content\n' > "$source_tree/data.txt"
ln "$source_tree/data.txt" "$source_tree/alias.txt"
ln -s data.txt "$source_tree/link.txt"
printf 'nested-content\n' > "$source_tree/nested/深层/中文 名称.txt"
printf '#!/bin/sh\necho fixture\n' > "$source_tree/script.sh"
chmod 0755 "$source_tree/script.sh"
printf 'plain-text\n' > "$source_tree/plain.txt"
printf '' > "$source_tree/empty.txt"

python3 - "$source_tree" <<'PY'
import os
import random
from pathlib import Path

root = Path(os.sys.argv[1])
rng = random.Random(20261021)
(root / "random.bin").write_bytes(rng.randbytes(256 * 1024))
(root / "compressible.txt").write_bytes((b"uotan-filesystem-fixture\n" * 65536)[:1024 * 1024])
sparse = root / "sparse.bin"
with sparse.open("wb") as handle:
    handle.write(b"sparse-head\n")
    handle.seek(512 * 1024)
    handle.write(b"sparse-tail\n")
os.setxattr(root / "data.txt", b"user.uotan", b"\x01\x02")
PY

chown -R 70001:80002 "$source_tree"
chmod 4755 "$source_tree/data.txt"
chmod 0640 "$source_tree/plain.txt"
find "$source_tree" -exec touch -h -d '@1700000000' {} +
chmod 0755 "$source_tree" "$source_tree/nested" "$source_tree/nested/深层" "$source_tree/empty"

IFS=',' read -r -a requested <<< "$FORMATS"
for format in "${requested[@]}"; do
  case "$format" in
    EXT4)
      image="$RUN_DIR/images/original/ext4.img"
      truncate -s 128M "$image"
      mke2fs -q -F -t ext4 -b 4096 -L uotan-ext4 -O ^orphan_file -d "$source_tree" "$image"
      e2fsck -fn "$image" >"$RUN_DIR/logs/fsck-original-ext4.log" 2>&1
      ;;
    EROFS)
      image="$RUN_DIR/images/original/erofs.img"
      mkfs.erofs -zlz4 -b4096 -T1700000000 -L uotan-erofs "$image" "$source_tree" \
        >"$RUN_DIR/logs/mkfs-erofs.log" 2>&1
      fsck.erofs "$image" >"$RUN_DIR/logs/fsck-original-erofs.log" 2>&1
      ;;
    F2FS)
      image="$RUN_DIR/images/original/f2fs.img"
      truncate -s 128M "$image"
      mkfs.f2fs -f -O extra_attr -l uotan-f2fs "$image" >"$RUN_DIR/logs/mkfs-f2fs.log" 2>&1
      sload.f2fs -f "$source_tree" -P -t / "$image" >"$RUN_DIR/logs/sload-f2fs.log" 2>&1
      fsck.f2fs --dry-run --no-kernel-check "$image" >"$RUN_DIR/logs/fsck-original-f2fs.log" 2>&1
      ;;
    *) echo "unsupported format: $format" >&2; exit 2 ;;
  esac
  sha256sum "$image" | awk '{print $1}' > "$image.sha256"
  echo "$format $(stat -c '%s' "$image") $(cat "$image.sha256")"
done > "$RUN_DIR/images/fixtures.tsv"

rm -rf "$WORK"/*
echo "[fixtures] done"
