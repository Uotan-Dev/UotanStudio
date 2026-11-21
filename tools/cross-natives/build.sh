#!/usr/bin/env bash
set -euo pipefail

arch="$1"
src="$2"
out="$3"

case "$arch" in
  loongarch64) triple="loongarch64-unknown-linux-gnu" ;;
  riscv64) triple="riscv64-unknown-linux-gnu" ;;
  *)
    echo "::error::Unsupported architecture '$arch'." >&2
    exit 1
    ;;
esac

cc="clang --target=$triple --sysroot=/crossrootfs/$arch -fuse-ld=lld"

host_ar=$(command -v llvm-ar || command -v ar)
host_ranlib=$(command -v llvm-ranlib || command -v ranlib)

case "$arch" in
  riscv64)
    symver_asm="__asm__(\".symver cfsetispeed,cfsetispeed@GLIBC_2.27\");
__asm__(\".symver cfsetospeed,cfsetospeed@GLIBC_2.27\");
__asm__(\".symver forkpty,forkpty@GLIBC_2.27\");
__asm__(\".symver pthread_sigmask,pthread_sigmask@GLIBC_2.27\");"
    ;;
  loongarch64)
    symver_asm="__asm__(\".symver cfsetispeed,cfsetispeed@GLIBC_2.36\");
__asm__(\".symver cfsetospeed,cfsetospeed@GLIBC_2.36\");"
    ;;
esac

if ! command -v make >/dev/null 2>&1; then
  tdnf install -y make >/dev/null 2>&1 || apt-get install -y make >/dev/null 2>&1
fi

mkdir -p "$out"

onig_prefix="$src/onig-prefix"
cd "$src/oniguruma"
if [ ! -f configure ]; then
  autoreconf -i
fi
cp "$(dirname "$0")/config.sub" "$(dirname "$0")/config.guess" .
CC="$cc" LD=ld.lld AR="$host_ar" RANLIB="$host_ranlib" \
  ./configure --enable-shared=no --with-pic=yes --host="$triple" \
  --prefix="$onig_prefix" --disable-dependency-tracking -q || {
  echo "::error::oniguruma configure failed; config.log tail:" >&2
  tail -40 config.log >&2 || true
  exit 1
}
make -j"$(nproc)" >/dev/null
make install >/dev/null

${cc} -shared -fPIC -O2 -s \
  -I"$onig_prefix/include" \
  "$src/onigwrap/onigwrap/onigwrap.c" \
  "$onig_prefix/lib/libonig.a" \
  -o "$out/libonigwrap.so"

pty_dir="$src/portapty/src/Porta.Pty.Native"
printf '%s\n#include "porta_pty.c"\n' "$symver_asm" > "$pty_dir/shim.c"
${cc} -std=gnu99 -O2 -fvisibility=hidden -shared -fPIC \
  -I"$pty_dir" \
  "$pty_dir/shim.c" \
  -lutil \
  -o "$out/libporta_pty.so"

marker="LoongArch"
[ "$arch" = "riscv64" ] && marker="RISC-V"
for lib in "$out/libonigwrap.so" "$out/libporta_pty.so"; do
  [ -f "$lib" ] || { echo "::error::Cross build produced no $lib." >&2; exit 1; }
  if command -v llvm-readelf >/dev/null 2>&1; then
    llvm-readelf -h "$lib" | grep -q "$marker" || {
      echo "::error::$lib is not a $arch ELF." >&2
      exit 1
    }
  elif command -v file >/dev/null 2>&1; then
    file "$lib" | grep -q "$marker" || {
      echo "::error::$lib is not a $arch ELF." >&2
      exit 1
    }
  fi
done

ls -l "$out"
