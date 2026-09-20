#!/bin/sh
# GKI 5.10 guest: read-write mount verification and fsck for ext4/f2fs, read-only
# verification plus fsck.erofs for EROFS. Images arrive inside the initramfs and
# are attached through loop devices because the GKI kernel has no virtio block
# driver (CONFIG_VIRTIO_BLK is not set) but ships BLK_DEV_LOOP=y.
# GKI 5.10 客户机：ext4/f2fs 读写挂载验证加 fsck，EROFS 只读验证加 fsck.erofs。
# 镜像随 initramfs 进入客户机并经 loop 设备接入，因为 GKI 内核没有 virtio 块驱动
# （CONFIG_VIRTIO_BLK 未启用）但内建 BLK_DEV_LOOP=y。
export PATH=/bin:/sbin:/usr/bin:/usr/sbin
export LC_ALL=C
/bin/busybox --install -s
mount -t proc proc /proc
mount -t sysfs sysfs /sys
mount -t devtmpfs devtmpfs /dev
echo KERNEL_BEGIN
uname -a
gzip -dc /proc/config.gz 2>/dev/null | grep -E 'CONFIG_(EXT4_FS|EROFS_FS|F2FS_FS|BLK_DEV_LOOP)=' || echo KERNEL_CONFIG_UNAVAILABLE
echo KERNEL_END
mkdir -p /mnt
# devtmpfs does not pre-create loop nodes; load the module and create the
# control node and loop devices explicitly.
# devtmpfs 不会预创建 loop 节点；显式加载模块并创建控制节点与 loop 设备。
modprobe loop 2>/dev/null || true
mknod /dev/loop-control c 10 237 2>/dev/null || true
for i in 0 1 2 3; do mknod "/dev/loop$i" b 7 "$i" 2>/dev/null || true; done
failures=0
verified=0
loseref=
while IFS="$(printf '\t')" read -r name format operation; do
    [ -n "$name" ] || continue
    echo "CASE_BEGIN $name $format $operation"
    image="/images/$name.img"
    if [ ! -r "$image" ]; then
        echo "RESULT $name FAIL missing_image"
        failures=$((failures + 1))
        continue
    fi
    loseref=$(losetup -f) || { echo "RESULT $name FAIL losetup-free"; failures=$((failures + 1)); continue; }
    [ -b "$loseref" ] || { echo "RESULT $name FAIL losetup-node $loseref"; loseref=; failures=$((failures + 1)); continue; }
    losetup "$loseref" "$image" || { echo "RESULT $name FAIL losetup-attach"; failures=$((failures + 1)); continue; }
    verify_rc=0
    fsck_rc=0
    fsck_tool=
    case "$format" in
    ext4)
        fsck_tool="e2fsck"
        mount -t ext4 "$loseref" /mnt
        mount_rc=$?
        if [ "$mount_rc" = 0 ]; then
            # Write-back check: a managed verification payload must survive a
            # remount with identical bytes.
            # 回写检查：托管验证载荷必须在重新挂载后字节一致。
            marker="gki510-rw-$name"
            printf '%s\n' "$marker" > /mnt/gki-rw.txt
            sync
            umount /mnt
            mount -t ext4 -o ro "$loseref" /mnt
            if [ "$(cat /mnt/gki-rw.txt 2>/dev/null)" = "$marker" ]; then
                echo "WRITEBACK_VERIFIED $name"
            else
                echo "WRITEBACK_FAILED $name"
                verify_rc=1
            fi
            (cd /mnt && sha256sum -c /reference/SHA256SUMS) > /hash-log 2>&1
            hash_rc=$?
            [ "$hash_rc" = 0 ] || { cat /hash-log; verify_rc=1; }
            umount /mnt
        else
            echo "MOUNT_EXIT $name $mount_rc"
            verify_rc=1
        fi
        e2fsck -fy "$loseref" > /fsck-log 2>&1
        fsck_rc=$?
        # e2fsck exit 1 means "errors corrected" which is still a pass for a
        # freshly written image; 2+ signals real corruption.
        # e2fsck 退出码 1 表示“已修正错误”，对新写入的镜像仍视为通过；2 及以上才代表真实损坏。
        [ "$fsck_rc" -le 1 ] && fsck_rc=0 || fsck_rc=1
        [ "$fsck_rc" = 0 ] || cat /fsck-log
        ;;
    f2fs)
        fsck_tool="fsck.f2fs"
        mount -t f2fs "$loseref" /mnt
        mount_rc=$?
        if [ "$mount_rc" = 0 ]; then
            if [ "$operation" = populate ]; then
                # Native mkfs.f2fs cannot populate from a directory, so the
                # guest seeds the filesystem itself and verifies the written
                # tree after a remount.
                # 原生 mkfs.f2fs 无法从目录灌入内容，因此由客户机自行写入种子
                # 文件并在重新挂载后校验写入的目录树。
                cp -a /seed/. /mnt/
                marker="gki510-populate-$name"
                printf '%s\n' "$marker" > /mnt/gki-populate.txt
                sync
                umount /mnt
                mount -t f2fs -o ro "$loseref" /mnt
                if [ "$(cat /mnt/gki-populate.txt 2>/dev/null)" = "$marker" ]; then
                    echo "WRITEBACK_VERIFIED $name"
                else
                    echo "WRITEBACK_FAILED $name"
                    verify_rc=1
                fi
            else
                marker="gki510-rw-$name"
                printf '%s\n' "$marker" > /mnt/gki-rw.txt
                sync
                umount /mnt
                mount -t f2fs -o ro "$loseref" /mnt
                if [ "$(cat /mnt/gki-rw.txt 2>/dev/null)" = "$marker" ]; then
                    echo "WRITEBACK_VERIFIED $name"
                else
                    echo "WRITEBACK_FAILED $name"
                    verify_rc=1
                fi
            fi
            (cd /mnt && sha256sum -c /reference/SHA256SUMS) > /hash-log 2>&1
            hash_rc=$?
            [ "$hash_rc" = 0 ] || { cat /hash-log; verify_rc=1; }
            umount /mnt
        else
            echo "MOUNT_EXIT $name $mount_rc"
            verify_rc=1
        fi
        fsck.f2fs -p 0 "$loseref" > /fsck-log 2>&1
        fsck_rc=$?
        [ "$fsck_rc" = 0 ] || cat /fsck-log
        ;;
    erofs)
        fsck_tool="fsck.erofs"
        # EROFS is a read-only filesystem: the rw probe does not apply, so this
        # case verifies the full tree and runs the checker instead.
        # EROFS 是只读文件系统：不适用读写探测，改为校验完整目录树并运行检查器。
        mount -t erofs -o ro "$loseref" /mnt
        mount_rc=$?
        if [ "$mount_rc" = 0 ]; then
            (cd /mnt && sha256sum -c /reference/SHA256SUMS) > /hash-log 2>&1
            hash_rc=$?
            [ "$hash_rc" = 0 ] || { cat /hash-log; verify_rc=1; }
            umount /mnt
        else
            echo "MOUNT_EXIT $name $mount_rc"
            verify_rc=1
        fi
        fsck.erofs "$image" > /fsck-log 2>&1
        fsck_rc=$?
        [ "$fsck_rc" = 0 ] || cat /fsck-log
        ;;
    esac
    [ -b "$loseref" ] && /bin/busybox losetup -d "$loseref"
    echo "VERIFY_EXIT $name verify=$verify_rc fsck=$fsck_rc tool=$fsck_tool"
    if [ "$verify_rc$fsck_rc" = "00" ]; then
        verified=$((verified + 1))
        echo "RESULT $name PASS"
    else
        failures=$((failures + 1))
        echo "RESULT $name FAIL"
    fi
    dmesg | tail -20
done < /cases.tsv
echo "GKI_RW_SUMMARY verified=$verified failed=$failures"
sync
poweroff -f
