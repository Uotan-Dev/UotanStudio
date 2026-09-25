#!/bin/busybox sh
/bin/busybox --install -s /bin
export PATH=/bin
exec >/dev/kmsg 2>&1
mount -t proc proc /proc
mount -t sysfs sysfs /sys
report() { printf '<0>%s\n' "$*" > /dev/kmsg; }
report GKI_REPLAY_BEGIN
uname -r
failed=0
for format in ext4 erofs f2fs; do
    mkdir -p "/mnt/$format"
    options=loop,ro
    if [ "$format" = f2fs ]; then
        options=loop
    fi
    if mount -t "$format" -o "$options" "/images/$format.img" "/mnt/$format"; then
        report "GKI_MOUNT_OK:$format"
        if [ "$(cat "/mnt/$format/data")" = source-content ] &&
           [ "$(cat "/mnt/$format/alias")" = source-content ] &&
           [ "$(readlink "/mnt/$format/link")" = data ] &&
           [ -d "/mnt/$format/empty" ]; then
            report "GKI_READ_OK:$format"
        else
            report "GKI_READ_FAIL:$format"
            failed=1
        fi
        attributes=$(stat -c '%u:%g:%a:%Y' "/mnt/$format/data")
        binary_hash=$(getfattr -n user.binary --only-values "/mnt/$format/data" 2>/dev/null | sha256sum)
        binary_hash=${binary_hash%% *}
        capability_hash=$(getfattr -n security.capability --only-values "/mnt/$format/data" 2>/dev/null | sha256sum)
        capability_hash=${capability_hash%% *}
        if [ "$attributes" = '70001:80002:4755:2500000000' ] &&
           [ "$binary_hash" = 785b0751fc2c53dc14a4ce3d800e69ef9ce1009eb327ccf458afe09c242c26c9 ] &&
           [ "$capability_hash" = 0af5fa788adb7dd10f02c81cc94989ceb649d1f7e4fc092da809b75bfff30fd7 ] &&
           [ "$(stat -c '%i' "/mnt/$format/data")" = "$(stat -c '%i' "/mnt/$format/alias")" ] &&
           [ -c "/mnt/$format/device" ]; then
            report "GKI_METADATA_OK:$format"
        else
            report "GKI_METADATA_FAIL:$format:$attributes:$binary_hash:$capability_hash"
            failed=1
        fi
        report "GKI_UMOUNT_START:$format"
        if umount "/mnt/$format"; then
            report "GKI_UMOUNT_OK:$format"
        else
            report "GKI_UMOUNT_FAIL:$format"
            failed=1
        fi
    else
        report "GKI_MOUNT_FAIL:$format"
        failed=1
    fi
done
if [ -f /images/mixed-f2fs.img ]; then
    mkdir -p /mnt/mixed
    if mount -t f2fs -o loop /images/mixed-f2fs.img /mnt/mixed; then
        report GKI_MIXED_MOUNT_OK
        expected=88fdfca4194cabc29f73fee7331f1a6b137b42a8e362691a35d4eb36d3bea33e
        lz4=$(sha256sum /mnt/mixed/lz4)
        zstd=$(sha256sum /mnt/mixed/zstd)
        if [ "${lz4%% *}" = "$expected" ] && [ "${zstd%% *}" = "$expected" ]; then
            report GKI_MIXED_READ_OK
        else
            report "GKI_MIXED_READ_FAIL:${lz4%% *}:${zstd%% *}"
            failed=1
        fi
        if umount /mnt/mixed; then
            report GKI_MIXED_UMOUNT_OK
        else
            report GKI_MIXED_UMOUNT_FAIL
            failed=1
        fi
    else
        report GKI_MIXED_MOUNT_FAIL
        failed=1
    fi
fi
if [ -f /images/native-f2fs.img ]; then
    mkdir -p /mnt/native
    if mount -t f2fs -o loop /images/native-f2fs.img /mnt/native; then
        report GKI_NATIVE_MOUNT_OK
        attributes=$(stat -c '%u:%g:%a' /mnt/native/data)
        binary_hash=$(getfattr -n user.binary --only-values /mnt/native/data 2>/dev/null | sha256sum)
        binary_hash=${binary_hash%% *}
        capability_hash=$(getfattr -n security.capability --only-values /mnt/native/data 2>/dev/null | sha256sum)
        capability_hash=${capability_hash%% *}
        if [ "$(cat /mnt/native/data)" = native-f2fs-content ] &&
           [ "$attributes" = '70001:80002:4755' ] &&
           [ "$binary_hash" = 50caa30dd66cff1ae68eb7e3a7b65717280ebbb8ca31cab9607d35242ebc87e1 ] &&
           [ "$capability_hash" = 0af5fa788adb7dd10f02c81cc94989ceb649d1f7e4fc092da809b75bfff30fd7 ] &&
           [ "$(stat -c '%i' /mnt/native/data)" = "$(stat -c '%i' /mnt/native/alias)" ] &&
           [ "$(readlink /mnt/native/link)" = data ] &&
           [ -d /mnt/native/empty ] &&
           [ -c /mnt/native/device ]; then
            report GKI_NATIVE_METADATA_OK
        else
            report "GKI_NATIVE_METADATA_FAIL:$attributes:$binary_hash:$capability_hash"
            failed=1
        fi
        if umount /mnt/native; then
            report GKI_NATIVE_UMOUNT_OK
        else
            report GKI_NATIVE_UMOUNT_FAIL
            failed=1
        fi
    else
        report GKI_NATIVE_MOUNT_FAIL
        failed=1
    fi
fi
if [ -f /images/external-erofs.img ]; then
    mkdir -p /mnt/external-erofs
    if mount -t erofs -o loop,ro /images/external-erofs.img /mnt/external-erofs; then
        report GKI_EXTERNAL_EROFS_MOUNT_OK
        hash=$(sha256sum /mnt/external-erofs/data)
        if [ "${hash%% *}" = a4d80cd14fd693919ee998c73c81d6e2b81f2b6247ab1e2a5a22453f3aab2a24 ]; then
            report GKI_EXTERNAL_EROFS_READ_OK
        else
            report "GKI_EXTERNAL_EROFS_READ_FAIL:${hash%% *}"
            failed=1
        fi
        if umount /mnt/external-erofs; then
            report GKI_EXTERNAL_EROFS_UMOUNT_OK
        else
            report GKI_EXTERNAL_EROFS_UMOUNT_FAIL
            failed=1
        fi
    else
        report GKI_EXTERNAL_EROFS_MOUNT_FAIL
        failed=1
    fi
fi
if [ -f /images/casefold-ext4.img ]; then
    mkdir -p /mnt/casefold
    if mount -t ext4 -o loop,ro /images/casefold-ext4.img /mnt/casefold; then
        report GKI_CASEFOLD_MOUNT_OK
        if [ "$(cat /mnt/casefold/Case/upper)" = casefold-content ]; then
            report GKI_CASEFOLD_LOOKUP_OK
        else
            report GKI_CASEFOLD_LOOKUP_FAIL
            failed=1
        fi
        if umount /mnt/casefold; then
            report GKI_CASEFOLD_UMOUNT_OK
        else
            report GKI_CASEFOLD_UMOUNT_FAIL
            failed=1
        fi
    else
        report GKI_CASEFOLD_MOUNT_FAIL
        failed=1
    fi
fi
report "GKI_REPLAY_RESULT:$failed"
poweroff -f
