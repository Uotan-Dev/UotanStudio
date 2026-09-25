#!/bin/sh
set -eu

image=${1:?expected output image path}
test "$(id -u)" -eq 0
test ! -e "$image"
mountpoint=$(mktemp -d)
mounted=0
cleanup() {
    if [ "$mounted" -eq 1 ]; then
        umount "$mountpoint"
    fi
    rmdir "$mountpoint"
}
trap cleanup EXIT

truncate -s 64M "$image"
mkfs.f2fs -f -O extra_attr,compression "$image"
mount -t f2fs -o loop "$image" "$mountpoint"
mounted=1
printf 'native-f2fs-content' > "$mountpoint/data"
f2fs_io setflags nocompression "$mountpoint/data"
ln "$mountpoint/data" "$mountpoint/alias"
ln -s data "$mountpoint/link"
mkdir "$mountpoint/empty"
mknod "$mountpoint/device" c 1 3
chown 70001:80002 "$mountpoint/data"
chmod 4755 "$mountpoint/data"
setfattr -n user.binary -v 0x00FF017F "$mountpoint/data"
setfattr -n security.capability -v 0x0000000201000000000000000000000000000000 "$mountpoint/data"
touch -a -d '@2100000000' "$mountpoint/data"
touch -m -d '@2200000000' "$mountpoint/data"
sync
umount "$mountpoint"
mounted=0
