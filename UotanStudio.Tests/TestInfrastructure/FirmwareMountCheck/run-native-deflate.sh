#!/bin/sh
set -eu
export LC_ALL=C
unavailable() { echo "UNAVAILABLE $*"; exit 77; }
if [ "$#" -ne 1 ]; then echo "Usage: sh run-native-deflate.sh --probe|GENERATED_DIRECTORY" >&2; exit 2; fi
for tool in fsck.erofs mkfs.erofs realpath mktemp rm mkdir find sort diff sha256sum grep; do
    command -v "$tool" >/dev/null 2>&1 || unavailable "Missing host tool: $tool"
done
help=$(fsck.erofs --help 2>&1 || true)
printf '%s\n' "$help" | grep -q -- '--extract' || unavailable "fsck.erofs does not support extraction."
compressors=$(mkfs.erofs --help 2>&1 || true)
printf '%s\n' "$compressors" | grep -qw deflate || unavailable "The installed erofs-utils does not advertise DEFLATE support."
if [ "$1" = --probe ]; then fsck.erofs --version; exit 0; fi
run_root=$(realpath "$1")
stage=$(mktemp -d /tmp/uotan-erofs-deflate.XXXXXX)
trap 'rm -rf "$stage"' EXIT INT TERM
mkdir -p "$run_root/logs"
exec > "$run_root/logs/native-erofs-deflate.log" 2>&1
fsck.erofs --version
fsck.erofs --extract="$stage/extracted" "$run_root/images/erofs-deflate.img"
echo NATIVE_EXTRACT_EXIT_0
(cd "$stage/extracted" && find . -type f | sort) | diff - "$run_root/reference/files.txt"
(cd "$stage/extracted" && find . -type d | sort) | diff - "$run_root/reference/directories.txt"
(cd "$stage/extracted" && sha256sum -c "$run_root/reference/SHA256SUMS")
echo NATIVE_DEFLATE_VERIFY_PASS
