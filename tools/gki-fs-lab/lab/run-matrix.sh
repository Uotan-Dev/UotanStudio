#!/usr/bin/env bash
set -euo pipefail

RUN_DIR=""
FORMATS="EXT4,EROFS,F2FS"
KERNELS="5.10,6.6,6.18"
ACCEL="auto"
CACHE_ROOT=/lab/cache/kernels
WORK_ROOT=/lab/work/matrix
CASES_ROOT=/lab/cases

usage() {
  echo "usage: run-matrix.sh --run-dir <dir> [--formats ...] [--kernels ...] [--accel auto|kvm|tcg]" >&2
}

while [ $# -gt 0 ]; do
  case "$1" in
    --run-dir) RUN_DIR=$2; shift 2 ;;
    --formats) FORMATS=$2; shift 2 ;;
    --kernels) KERNELS=$2; shift 2 ;;
    --accel) ACCEL=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) echo "unknown argument: $1" >&2; usage; exit 2 ;;
  esac
done

[ -n "$RUN_DIR" ] || { usage; exit 2; }
mkdir -p "$RUN_DIR/logs" "$WORK_ROOT" "$CASES_ROOT/repacked"
rm -rf "$WORK_ROOT"/*

if [ "$ACCEL" = "auto" ]; then
  if [ -c /dev/kvm ] && [ -r /dev/kvm ]; then
    ACCEL=kvm
  else
    ACCEL=tcg
  fi
fi
if [ "$ACCEL" = "kvm" ] && { [ ! -c /dev/kvm ] || [ ! -r /dev/kvm ]; }; then
  echo "KVM requested but /dev/kvm is unavailable" >&2
  exit 2
fi
echo "$ACCEL" > "$RUN_DIR/logs/acceleration.txt"

IFS=',' read -r -a requested_formats <<< "$FORMATS"
: > "$CASES_ROOT/cases.tsv"
device_letters=(a b c d e f g h)
device_index=0
for format in "${requested_formats[@]}"; do
  lower=$(echo "$format" | tr '[:upper:]' '[:lower:]')
  device="/dev/vd${device_letters[$device_index]}"
  printf '%s\t%s\t%s\n' "$lower" "$lower" "$device" >> "$CASES_ROOT/cases.tsv"
  device_index=$((device_index + 1))
done

stage="$WORK_ROOT/initramfs"
root="$stage/root"
mkdir -p "$root"/bin "$root"/lib "$root"/dev "$root"/proc "$root"/sys "$root"/mnt "$root"/manifests "$root"/tmp
cp /bin/busybox.static "$root/bin/busybox"
for applet in sh mount umount mkdir cat echo stat readlink sha256sum od tr cut wc find true false poweroff sync touch dd nproc uname zcat grep awk sed sleep test expr head tail ls rm; do
  ln -sf busybox "$root/bin/$applet"
done
for tool in e2fsck fsck.f2fs fsck.erofs getfattr; do
  binary=$(command -v "$tool" || true)
  [ -n "$binary" ] || { echo "missing tool: $tool" >&2; exit 2; }
  cp "$binary" "$root/bin/"
  ldd "$binary" 2>/dev/null | awk '{for (i = 1; i <= NF; i++) if ($i ~ /^\//) print $i}' | sort -u | while read -r library; do
    cp "$library" "$root/lib/" 2>/dev/null || true
  done
done
cp /lib/ld-musl-x86_64.so.1 "$root/lib/" 2>/dev/null || true
cp /repo/tools/gki-fs-lab/lab/guest-init.sh "$root/init"
chmod 755 "$root/init"
mknod "$root/dev/console" c 5 1
mknod "$root/dev/null" c 1 3
cp "$CASES_ROOT/cases.tsv" "$root/cases.tsv"
for format in "${requested_formats[@]}"; do
  lower=$(echo "$format" | tr '[:upper:]' '[:lower:]')
  cp "$RUN_DIR/manifests/$lower.tsv" "$root/manifests/$lower.tsv"
done
(cd "$root" && find . -print | cpio -o -H newc 2>/dev/null) | gzip -1 > "$stage/initramfs.cpio.gz"

IFS=',' read -r -a requested_kernels <<< "$KERNELS"
failures=0
for version in "${requested_kernels[@]}"; do
  cache=$(find "$CACHE_ROOT" -maxdepth 1 -type d -name "$version-*" | sort | tail -1)
  [ -n "$cache" ] && [ -s "$cache/bzImage" ] || { echo "kernel cache missing for $version" >&2; exit 2; }
  log="$RUN_DIR/logs/qemu-$version.log"
  rm -f "$log"
  run_images="$CASES_ROOT/run-$version"
  rm -rf "$run_images"
  mkdir -p "$run_images"
  for format in "${requested_formats[@]}"; do
    lower=$(echo "$format" | tr '[:upper:]' '[:lower:]')
    cp "$CASES_ROOT/repacked/$lower.img" "$run_images/$lower.img"
    if [ "$lower" = "ext4" ]; then
      truncate -s 128M "$run_images/$lower.img"
      resize2fs "$run_images/$lower.img" >/dev/null 2>&1 || {
        echo "resize2fs failed on the rebuilt ext4 image" >&2
        exit 2
      }
    fi
  done

  qemu_args=(
    -machine q35 -m 4096 -display none -monitor none -no-reboot
    -kernel "$cache/bzImage" -initrd "$stage/initramfs.cpio.gz"
    -append "console=ttyS0,115200 rdinit=/init panic=-1 oops=panic loglevel=7 8250.nr_uarts=4"
  )
  if [ "$ACCEL" = "kvm" ]; then
    qemu_args+=(-accel kvm -cpu host -smp 4)
  else
    qemu_args+=(-accel tcg,thread=multi -cpu max -smp 2)
  fi
  index=0
  while IFS="$(printf '\t')" read -r name format device; do
    [ -n "$name" ] || continue
    qemu_args+=(-drive "file=$run_images/$format.img,if=none,id=d$index,format=raw,cache=writeback")
    qemu_args+=(-device "virtio-blk-pci,drive=d$index,serial=$name,num-queues=4")
    index=$((index + 1))
  done < "$CASES_ROOT/cases.tsv"
  qemu_args+=(-serial "file:$log")

  echo "[qemu $version] accel=$ACCEL"
  set +e
  timeout 900 qemu-system-x86_64 "${qemu_args[@]}"
  rc=$?
  set -e
  if [ "$rc" -ne 0 ]; then
    echo "[qemu $version] exit $rc" >&2
    tail -40 "$log" >&2 || true
    failures=$((failures + 1))
    continue
  fi
  if grep -q "SUMMARY .*failures=0" "$log" && [ "$(grep -c '^RESULT ' "$log" || true)" -eq "${#requested_formats[@]}" ]; then
    echo "[qemu $version] pass"
  else
    echo "[qemu $version] result check failed" >&2
    tail -60 "$log" >&2 || true
    failures=$((failures + 1))
  fi
done

[ "$failures" -eq 0 ] || exit 1
echo "[qemu] done"
