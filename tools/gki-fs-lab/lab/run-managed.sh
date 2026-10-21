#!/usr/bin/env bash
set -euo pipefail

DRIVER=""
RUN_DIR=""
FORMATS="EXT4,EROFS,F2FS"
ALLOW_FRESH=${ALLOW_FRESH:-0}

usage() {
  echo "usage: run-managed.sh --driver <binary> --run-dir <dir> [--formats EXT4,EROFS,F2FS]" >&2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --driver) DRIVER=$2; shift 2 ;;
    --run-dir) RUN_DIR=$2; shift 2 ;;
    --formats) FORMATS=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

[ -n "$DRIVER" ] && [ -n "$RUN_DIR" ] || { usage; exit 2; }
[ -x "$DRIVER" ] || { echo "driver is not executable: $DRIVER" >&2; exit 2; }
mkdir -p "$RUN_DIR/reports" "$RUN_DIR/manifests" /lab/work/managed /lab/cases/repacked

IFS=',' read -r -a requested <<< "$FORMATS"
for format in "${requested[@]}"; do
  lower=$(echo "$format" | tr '[:upper:]' '[:lower:]')
  source="$RUN_DIR/images/original/$lower.img"
  [ -s "$source" ] || { echo "missing source image: $source" >&2; exit 2; }
  echo "[managed] $format"
  args=(
    roundtrip
    --format "$format"
    --source "$source"
    --work "/lab/work/managed/$lower"
    --repacked "/lab/cases/repacked/$lower.img"
    --manifest "$RUN_DIR/manifests/$lower.tsv"
    --report "$RUN_DIR/reports/managed-$lower.json"
  )
  if [ "$ALLOW_FRESH" = 1 ]; then
    args+=(--allow-fresh true)
  fi
  set +e
  "$DRIVER" "${args[@]}" 2>&1 | tee "$RUN_DIR/logs/managed-$lower.log"
  rc=${PIPESTATUS[0]}
  set -e
  [ "$rc" -eq 0 ] || { echo "[managed] $format failed with $rc" >&2; exit "$rc"; }
done

echo "[managed] done"
