#!/usr/bin/env bash
# Cross-builds the onigwrap and Porta.Pty native libraries for one guest
# architecture. Runs inside a dotnet-buildtools cross container, where clang
# can target the guest and /crossrootfs/<arch> holds the glibc sysroot.
#
# Sources expected under <sources-dir>:
#   oniguruma/  kkos/oniguruma (release tarball with pre-generated configure)
#   onigwrap/   aikawayataro/Onigwrap checkout
#   portapty/   tomlm/Porta.Pty checkout
# Usage: build.sh <loongarch64|riscv64> <sources-dir> <output-dir>
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

# GNU build tools that can handle the guest objects; the host toolchain only
# knows x86-64, so fall back to the llvm equivalents when present.
host_ar=$(command -v llvm-ar || command -v ar)
host_ranlib=$(command -v llvm-ranlib || command -v ranlib)

# Old symbol versions keep the libraries loadable on distros whose glibc
# predates the 2.42 re-versioning of cfset*; riscv64 also pins forkpty to
# its pre-libutil-merge version. loongarch64's baseline is glibc 2.36.
case "$arch" in
  riscv64)
    symver_asm="__asm__(\".symver cfsetispeed,cfsetispeed@GLIBC_2.27\");
__asm__(\".symver cfsetospeed,cfsetospeed@GLIBC_2.27\");
__asm__(\".symver forkpty,forkpty@GLIBC_2.27\");"
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

# Oniguruma: static PIC library for the guest.
onig_prefix="$src/onig-prefix"
cd "$src/oniguruma"
if [ ! -f configure ]; then
  autoreconf -i
fi
# The vendored copy ships a 2020 config.sub that predates loongarch64;
# refresh it with the checked-in one.
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

# onigwrap: thin C wrapper linked against the static oniguruma.
"${cc}" -shared -fPIC -O2 -s \
  -I"$onig_prefix/include" \
  "$src/onigwrap/onigwrap/onigwrap.c" \
  "$onig_prefix/lib/libonig.a" \
  -o "$out/libonigwrap.so"

# Porta.Pty: single-file C shim; libutil provides forkpty/openpty. Compile a
# shim TU that pins the glibc symbol versions before including the source.
pty_dir="$src/portapty/src/Porta.Pty.Native"
printf '%s\n#include "porta_pty.c"\n' "$symver_asm" > "$pty_dir/shim.c"
"${cc}" -std=gnu99 -O2 -fvisibility=hidden -shared -fPIC \
  -I"$pty_dir" \
  "$pty_dir/shim.c" \
  -lutil \
  -o "$out/libporta_pty.so"

# Sanity checks: both libraries must exist and be guest-arch ELF objects.
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
