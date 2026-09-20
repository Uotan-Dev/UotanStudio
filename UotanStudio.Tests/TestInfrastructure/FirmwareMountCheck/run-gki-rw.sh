#!/bin/sh
# Host driver for the GKI 5.10 read-write mount matrix. Runs inside the Alpine
# WSL distribution as root. RUNDIR must already contain images/, cases.tsv,
# gki-cases.tsv and reference/ (produced by the C# fixture). The guest boots the
# official GKI 5.10 kernel with the matrix embedded in the initramfs because the
# GKI kernel has no virtio block driver; loop devices provide the block path.
# GKI 5.10 读写矩阵的宿主驱动。在 Alpine WSL 中以 root 运行。RUNDIR 须已含
# images/、cases.tsv、gki-cases.tsv 与 reference/（由 C# 夹具生成）。客户机启动
# 官方 GKI 5.10 内核并把矩阵嵌入 initramfs，因为 GKI 内核没有 virtio 块驱动，
# 块通路由 loop 设备提供。
set -eu
export LC_ALL=C
unavailable() { echo "UNAVAILABLE $*"; exit 77; }
[ "$#" -eq 1 ] || { echo "Usage: sh run-gki-rw.sh RUNDIR" >&2; exit 2; }
for tool in realpath dirname basename find cpio gzip timeout sha256sum cut grep mktemp mkdir rm id cat sed; do
    command -v "$tool" >/dev/null 2>&1 || unavailable "Missing host tool: $tool"
done
command -v qemu-system-x86_64 >/dev/null 2>&1 || unavailable "qemu-system-x86_64 is missing."
command -v e2fsck >/dev/null 2>&1 || unavailable "e2fsck is missing."
command -v fsck.f2fs >/dev/null 2>&1 || unavailable "fsck.f2fs is missing."
command -v fsck.erofs >/dev/null 2>&1 || unavailable "fsck.erofs is missing."
[ -r /bin/busybox.static ] || unavailable "busybox.static is missing (package busybox-static)."
[ "$(id -u)" = 0 ] || unavailable "Root is required to create initramfs device nodes."
capabilities=$(sed -n 's/^CapEff:[[:space:]]*//p' /proc/self/status)
[ -n "$capabilities" ] && [ $((0x$capabilities & 0x08000000)) -ne 0 ] || unavailable "CAP_MKNOD is unavailable."
kernel=${UOTAN_FIRMWARE_GKI_KERNEL:-/tmp/uotan-gki510/bzImage}
[ -r "$kernel" ] || unavailable "GKI kernel image is missing: $kernel"
kernel_config=${UOTAN_FIRMWARE_GKI_CONFIG:-$kernel.config}
[ -r "$kernel_config" ] || kernel_config=
run_root=$(realpath "$1")
[ -r "$run_root/gki-cases.tsv" ] || unavailable "gki-cases.tsv is missing under: $run_root"
[ -d "$run_root/images" ] || unavailable "images directory is missing under: $run_root"
[ -d "$run_root/reference" ] || unavailable "reference directory is missing under: $run_root"
script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
mkdir -p "$run_root/logs"
sha256sum "$kernel" > "$run_root/logs/kernel.sha256"
[ -z "$kernel_config" ] || cp "$kernel_config" "$run_root/logs/kernel.config"
qemu-system-x86_64 --version > "$run_root/logs/qemu-version.txt"
stage=$(mktemp -d /tmp/uotan-gki-initramfs.XXXXXX)
trap 'rm -rf "$stage"' EXIT
trap 'exit 1' INT TERM
mkdir -p "$stage/root/bin" "$stage/root/sbin" "$stage/root/usr/bin" "$stage/root/usr/sbin" "$stage/root/lib" "$stage/root/dev" "$stage/root/proc" "$stage/root/sys" "$stage/root/images" "$stage/root/reference"
cp /bin/busybox.static "$stage/root/bin/busybox"
ln -s busybox "$stage/root/bin/sh"
# The Alpine fsck tools are dynamically linked against musl; ship the loader and
# every NEEDED library so the guest can run them without an Alpine rootfs.
# Alpine 的 fsck 工具动态链接 musl；随包提供 loader 与全部 NEEDED 库，客户机无需完整根文件系统。
for tool_binary in /usr/sbin/e2fsck /usr/sbin/fsck.f2fs /usr/sbin/fsck.erofs; do
    [ -r "$tool_binary" ] || tool_binary=$(command -v "$(basename "$tool_binary")")
    cp "$tool_binary" "$stage/root/bin/"
    ldd "$tool_binary" 2>/dev/null | awk '{for (i = 1; i <= NF; i++) if ($i ~ /^\//) print $i}' | sort -u | while read -r library; do
        cp "$library" "$stage/root/lib/"
    done
done
[ -r "$stage/root/lib/ld-musl-x86_64.so.1" ] || cp /lib/ld-musl-x86_64.so.1 "$stage/root/lib/"
cp "$script_root/gki-rw-init.sh" "$stage/root/init"
chmod +x "$stage/root/init"
mknod "$stage/root/dev/console" c 5 1
mknod "$stage/root/dev/null" c 1 3
cp "$run_root"/images/*.img "$stage/root/images/"
[ ! -d "$run_root/seed" ] || cp -a "$run_root/seed" "$stage/root/seed"
cp "$run_root/gki-cases.tsv" "$stage/root/cases.tsv"
cp "$run_root/reference/"* "$stage/root/reference/"
(cd "$stage/root" && find . -print | cpio -o -H newc) | gzip -1 > "$stage/initramfs.cpio.gz"
qemu_rc=0
# GKI sets CONFIG_SERIAL_8250_RUNTIME_UARTS=0, so the legacy port must be
# registered explicitly before console=ttyS0 can attach.
# GKI 将 CONFIG_SERIAL_8250_RUNTIME_UARTS 设为 0，必须显式注册传统串口，
# console=ttyS0 才能生效。
timeout 420 qemu-system-x86_64 \
    -machine q35 -accel tcg -cpu max -m 2048 -smp 2 -nographic -nodefaults -serial stdio -monitor none -no-reboot \
    -kernel "$kernel" -initrd "$stage/initramfs.cpio.gz" \
    -append 'console=ttyS0,115200 8250.nr_uarts=1 panic=-1 nokaslr' \
    > "$run_root/logs/qemu-console.log" 2>&1 || qemu_rc=$?
if [ "$qemu_rc" -ne 0 ]; then tail -60 "$run_root/logs/qemu-console.log"; exit "$qemu_rc"; fi
grep -E '^(RESULT|WRITEBACK_VERIFIED|WRITEBACK_FAILED|MOUNT_EXIT|VERIFY_EXIT|GKI_RW_SUMMARY)' "$run_root/logs/qemu-console.log" || true
cases=$(grep -c -v '^$' "$run_root/gki-cases.tsv" || true)
results=$(grep -c '^RESULT ' "$run_root/logs/qemu-console.log" || true)
[ "$results" = "$cases" ] || { echo "Incomplete GKI results: expected $cases got $results" >&2; exit 1; }
grep -q '^GKI_RW_SUMMARY .* failed=0' "$run_root/logs/qemu-console.log" || { echo "GKI guest reported failures" >&2; exit 1; }
echo GKI_RW_PASS
