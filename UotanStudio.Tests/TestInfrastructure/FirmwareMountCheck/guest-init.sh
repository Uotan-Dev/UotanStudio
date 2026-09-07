#!/bin/sh
export PATH=/bin:/sbin:/usr/bin:/usr/sbin
export LC_ALL=C
/bin/busybox --install -s
mount -t proc proc /proc
mount -t sysfs sysfs /sys
mount -t devtmpfs devtmpfs /dev
mkdir -p /mnt
echo KERNEL_BEGIN
uname -a
cat /proc/cmdline
grep -E 'CONFIG_(EXT4_FS|EROFS_FS|F2FS_FS|VIRTIO_BLK|DEVTMPFS|BLK_DEV_LOOP)' /reference/kernel.config
echo KERNEL_END
for module in virtio_pci virtio_blk ext4 erofs f2fs; do
    modprobe "$module" || echo "MODULE_LOAD_FAILED $module"
done
sleep 1
failures=0
verified=0
unavailable=0
index=0
while IFS="$(printf '\t')" read -r name format options hash size; do
    device=
    for serial in /sys/block/vd*/serial; do
        [ -e "$serial" ] || continue
        [ "$(cat "$serial")" = "uotan$index" ] && device="/dev/$(basename "$(dirname "$serial")")"
    done
    index=$((index + 1))
    echo "CASE_BEGIN $name $format $options $device"
    if [ -z "$device" ] || [ "$(blockdev --getro "$device")" != 1 ]; then
        echo "RESULT $name FAIL missing_or_writable_device"
        failures=$((failures + 1))
        continue
    fi
    mount -t "$format" -o "$options,nosuid,nodev" "$device" /mnt
    mount_rc=$?
    echo "MOUNT_EXIT $name $mount_rc"
    if [ "$mount_rc" != 0 ]; then
        echo "RESULT $name FAIL mount"
        failures=$((failures + 1))
        dmesg | tail -40
        continue
    fi
    cat /proc/mounts | grep ' /mnt '
    (cd /mnt && find . -type f | sort) > /actual-files
    (cd /mnt && find . -type d | sort) > /actual-directories
    diff -u /reference/files.txt /actual-files
    files_rc=$?
    diff -u /reference/directories.txt /actual-directories
    dirs_rc=$?
    (cd /mnt && sha256sum -c /reference/SHA256SUMS) > /hash-log 2>&1
    hash_rc=$?
    if [ "$hash_rc" = 0 ]; then echo "SHA256_VERIFIED $name $(wc -l < /hash-log) files"; else cat /hash-log; fi
    umount /mnt
    unmount_rc=$?
    echo "VERIFY_EXIT $name files=$files_rc directories=$dirs_rc sha256=$hash_rc unmount=$unmount_rc"
    if [ "$files_rc$dirs_rc$hash_rc$unmount_rc" = 0000 ]; then
        verified=$((verified + 1))
        echo "RESULT $name PASS"
    else
        failures=$((failures + 1))
        echo "RESULT $name FAIL verify"
    fi
    dmesg | tail -40
done < /reference/cases.tsv
echo "MOUNT_SUMMARY verified=$verified unavailable=$unavailable failed=$failures"
cat /proc/mounts
sync
poweroff -f
