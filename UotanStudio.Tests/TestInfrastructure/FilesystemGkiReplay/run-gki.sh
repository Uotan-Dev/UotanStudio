#!/bin/sh
set -eu

images=${1:?expected directory containing ext4.img, erofs.img, f2fs.img}
kernel=${2:?expected Google GKI 5.10 bzImage}
kernel_profile=${3:-gki}
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT

case "$kernel_profile" in
    gki) file "$kernel" | grep -q 'version 5\.10\..*android' ;;
    wsl) file "$kernel" | grep -q 'microsoft-standard-WSL2' ;;
    *) echo "Unknown kernel profile: $kernel_profile" >&2; exit 1 ;;
esac
for format in ext4 erofs f2fs; do
    test -s "$images/$format.img"
done
mkdir -p "$work/root/bin" "$work/root/dev" "$work/root/images" "$work/root/lib" "$work/root/mnt" "$work/root/proc" "$work/root/sys"
cp /home/zicai/gki/initrd/bin/busybox "$work/root/bin/busybox"
cp /usr/bin/getfattr "$work/root/bin/getfattr"
cp /lib/ld-musl-x86_64.so.1 "$work/root/lib/"
cp -a /home/zicai/gki/initrd/dev/console /home/zicai/gki/initrd/dev/null "$work/root/dev/"
mknod "$work/root/dev/kmsg" c 1 11
cp "$script_dir/guest-init.sh" "$work/root/init"
chmod 755 "$work/root/init"
for format in ext4 erofs f2fs; do
    cp "$images/$format.img" "$work/root/images/$format.img"
done
if [ -s "$images/mixed-f2fs.img" ]; then
    cp "$images/mixed-f2fs.img" "$work/root/images/mixed-f2fs.img"
fi
if [ -s "$images/native-f2fs.img" ]; then
    cp "$images/native-f2fs.img" "$work/root/images/native-f2fs.img"
fi
if [ -s "$images/external-erofs.img" ]; then
    cp "$images/external-erofs.img" "$work/root/images/external-erofs.img"
fi
if [ -s "$images/casefold-ext4.img" ]; then
    cp "$images/casefold-ext4.img" "$work/root/images/casefold-ext4.img"
fi
(cd "$work/root" && find . -print | cpio -o -H newc 2>/dev/null | gzip -1 > "$work/guest.cpio.gz")
timeout 120 qemu-system-x86_64 -nographic -no-reboot -m 4096 -smp 2 \
    -kernel "$kernel" -initrd "$work/guest.cpio.gz" \
    -append 'console=ttyS0 rdinit=/init loglevel=8 earlyprintk=serial' > "$work/console.log" 2>&1 || {
        cat "$work/console.log"
        exit 1
    }
cat "$work/console.log"
grep -q 'GKI_REPLAY_RESULT:0' "$work/console.log"
for format in ext4 erofs f2fs; do
    grep -q "GKI_MOUNT_OK:$format" "$work/console.log"
    grep -q "GKI_READ_OK:$format" "$work/console.log"
    grep -q "GKI_METADATA_OK:$format" "$work/console.log"
    grep -q "GKI_UMOUNT_OK:$format" "$work/console.log"
done
if [ -s "$images/mixed-f2fs.img" ]; then
    grep -q GKI_MIXED_MOUNT_OK "$work/console.log"
    grep -q GKI_MIXED_READ_OK "$work/console.log"
    grep -q GKI_MIXED_UMOUNT_OK "$work/console.log"
fi
if [ -s "$images/native-f2fs.img" ]; then
    grep -q GKI_NATIVE_MOUNT_OK "$work/console.log"
    grep -q GKI_NATIVE_METADATA_OK "$work/console.log"
    grep -q GKI_NATIVE_UMOUNT_OK "$work/console.log"
fi
if [ -s "$images/external-erofs.img" ]; then
    grep -q GKI_EXTERNAL_EROFS_MOUNT_OK "$work/console.log"
    grep -q GKI_EXTERNAL_EROFS_READ_OK "$work/console.log"
    grep -q GKI_EXTERNAL_EROFS_UMOUNT_OK "$work/console.log"
fi
if [ -s "$images/casefold-ext4.img" ]; then
    grep -q GKI_CASEFOLD_MOUNT_OK "$work/console.log"
    grep -q GKI_CASEFOLD_LOOKUP_OK "$work/console.log"
    grep -q GKI_CASEFOLD_UMOUNT_OK "$work/console.log"
fi
