#!/bin/sh
set -eu
export LC_ALL=C
[ "$#" = 3 ] || { echo 'Usage: build-erofs-deflate.sh BUILD_ROOT RUNTIME_ROOT OUTPUT_DIRECTORY' >&2; exit 2; }
[ "$(id -u)" = 0 ] || { echo 'Run as root to enter the isolated build root.' >&2; exit 2; }
build_root=$(realpath "$1")
runtime_root=$(realpath "$2")
mkdir -p "$3"
output=$(realpath "$3")
script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
release=6.18.48-0-virt
headers=/usr/src/linux-headers-$release
[ "$(cat "$build_root$headers/include/config/kernel.release")" = "$release" ]
grep -qx 'CONFIG_EROFS_FS=m' "$runtime_root/boot/config-$release"
grep -qx 'CONFIG_ZLIB_INFLATE=y' "$runtime_root/boot/config-$release"
(cd "$build_root/work/erofs" && sha256sum -c "$script_root/erofs-v6.18.48.sha256")
chroot "$build_root" /usr/bin/gcc --version > "$output/compiler.txt"
apk --root "$build_root" info -v > "$output/packages.txt"
chroot "$build_root" /usr/bin/make -C "$headers" M=/work/erofs \
    CONFIG_EROFS_FS_ZIP_DEFLATE=y KCFLAGS=-DCONFIG_EROFS_FS_ZIP_DEFLATE=1 -j4 modules > "$output/build.log" 2>&1
chroot "$build_root" /usr/bin/readelf -p .modinfo /work/erofs/erofs.ko > "$output/modinfo.txt"
grep -q "vermagic=$release " "$output/modinfo.txt"
chroot "$build_root" /usr/bin/nm /work/erofs/erofs.ko > "$output/symbols.txt"
grep -q ' z_erofs_deflate_decomp$' "$output/symbols.txt"
cp "$build_root/work/erofs/erofs.ko" "$output/erofs.ko"
gzip -n -c "$output/erofs.ko" > "$output/erofs.ko.gz"
cp -n "$runtime_root/boot/config-$release" "$output/kernel.base.config"
sed 's/^# CONFIG_EROFS_FS_ZIP_DEFLATE is not set$/CONFIG_EROFS_FS_ZIP_DEFLATE=y/' \
    "$output/kernel.base.config" > "$output/kernel.config"
grep -qx 'CONFIG_EROFS_FS_ZIP_DEFLATE=y' "$output/kernel.config"
mkdir -p "$output/source"
while read -r hash name; do cp "$build_root/work/erofs/$name" "$output/source/$name"; done < "$script_root/erofs-v6.18.48.sha256"
cp "$script_root/erofs-v6.18.48.sha256" "$output/source/SHA256SUMS"
cp "$script_root/erofs-v6.18.48.sha256" "$output/"
cp "$script_root/build-erofs-deflate.sh" "$output/"
(cd "$output" && sha256sum erofs.ko erofs.ko.gz kernel.base.config kernel.config > SHA256SUMS)
echo "DEFLATE_MODULE_READY $output"
