#!/bin/sh
set -eu

source_tree=${1:?expected Microsoft WSL kernel source tree}
output=${2:?expected build output directory}
current=$(uname -r)
kernel_version=$(make -s -C "$source_tree" kernelversion)
case "$current" in
    "$kernel_version"-microsoft-standard-WSL2) ;;
    *) echo "Source version $kernel_version does not match running WSL kernel $current" >&2; exit 1 ;;
esac
test ! -e "$output/.config"
mkdir -p "$output"
zcat /proc/config.gz > "$output/.config"
"$source_tree/scripts/config" --file "$output/.config" \
    --enable EXT4_FS --enable EXT4_FS_POSIX_ACL --enable EXT4_FS_SECURITY \
    --enable EROFS_FS --enable EROFS_FS_XATTR --enable EROFS_FS_POSIX_ACL \
    --enable EROFS_FS_SECURITY --enable EROFS_FS_ZIP --enable EROFS_FS_ZIP_LZMA \
    --enable EROFS_FS_ZIP_DEFLATE --enable EROFS_FS_ZIP_ZSTD \
    --enable F2FS_FS --enable F2FS_FS_XATTR --enable F2FS_FS_POSIX_ACL \
    --enable F2FS_FS_SECURITY --enable F2FS_FS_COMPRESSION \
    --enable UNICODE \
    --disable DEBUG_INFO_BTF
make -C "$source_tree" O="$output" olddefconfig
for config in EXT4_FS EXT4_FS_POSIX_ACL EXT4_FS_SECURITY \
    EROFS_FS EROFS_FS_XATTR EROFS_FS_POSIX_ACL EROFS_FS_SECURITY \
    EROFS_FS_ZIP EROFS_FS_ZIP_LZMA EROFS_FS_ZIP_DEFLATE EROFS_FS_ZIP_ZSTD \
    F2FS_FS F2FS_FS_XATTR F2FS_FS_POSIX_ACL F2FS_FS_SECURITY \
    F2FS_FS_COMPRESSION F2FS_FS_LZ4 F2FS_FS_ZSTD; do
    grep -qx "CONFIG_${config}=y" "$output/.config"
done
grep -qx 'CONFIG_UNICODE=y' "$output/.config"
grep -qx '# CONFIG_DEBUG_INFO_BTF is not set' "$output/.config"
test "$(make -s -C "$source_tree" O="$output" kernelrelease)" = "$current"
make -C "$source_tree" O="$output" -j 12 bzImage
test -s "$output/arch/x86/boot/bzImage"
file "$output/arch/x86/boot/bzImage"
