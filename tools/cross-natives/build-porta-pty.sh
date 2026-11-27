#!/usr/bin/env bash
set -euo pipefail

arch="$1"
src="$2"
out="$3"

case "$arch" in
  riscv64) triple="riscv64-unknown-linux-gnu"; symver="GLIBC_2.27"; marker="RISC-V" ;;
  loongarch64) triple="loongarch64-unknown-linux-gnu"; symver="GLIBC_2.36"; marker="LoongArch" ;;
  *) echo "::error::Unsupported architecture '$arch'." >&2; exit 1 ;;
esac

cc="clang --target=$triple --sysroot=/crossrootfs/$arch -fuse-ld=lld"
pty_dir="$src/portapty/src/Porta.Pty.Native"
mkdir -p "$out"
printf '%s\n#include "porta_pty.c"\n' "__asm__(\".symver cfsetispeed,cfsetispeed@$symver\"); __asm__(\".symver cfsetospeed,cfsetospeed@$symver\");" > "$pty_dir/shim.c"
${cc} -std=gnu99 -O2 -fvisibility=hidden -shared -fPIC -I"$pty_dir" "$pty_dir/shim.c" -lutil -o "$out/libporta_pty.so"

if command -v llvm-readelf >/dev/null 2>&1; then
  llvm-readelf -h "$out/libporta_pty.so" | grep -q "$marker"
elif command -v file >/dev/null 2>&1; then
  file "$out/libporta_pty.so" | grep -q "$marker"
fi
ls -l "$out/libporta_pty.so"
