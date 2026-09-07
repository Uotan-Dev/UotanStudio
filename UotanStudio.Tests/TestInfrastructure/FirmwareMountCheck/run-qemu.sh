#!/bin/sh
set -eu
export LC_ALL=C
unavailable() { echo "UNAVAILABLE $*"; exit 77; }
[ "$#" -eq 1 ] || { echo "Usage: sh run-qemu.sh --probe|GENERATED_DIRECTORY" >&2; exit 2; }
runtime_root=${UOTAN_FIRMWARE_QEMU_ROOT:-/tmp/uotan-firmware-mount-qemu/root}
for tool in realpath dirname basename find sort tail sed grep cp ln chmod mknod cpio gzip timeout sha256sum cut diff mktemp mkdir rm id cat; do
    command -v "$tool" >/dev/null 2>&1 || unavailable "Missing host tool: $tool"
done
[ "$(id -u)" = 0 ] || unavailable "Root is required to create initramfs device nodes."
capabilities=$(sed -n 's/^CapEff:[[:space:]]*//p' /proc/self/status)
[ -n "$capabilities" ] && [ $((0x$capabilities & 0x08000000)) -ne 0 ] || unavailable "CAP_MKNOD is unavailable."
[ -d "$runtime_root" ] || unavailable "QEMU runtime directory is missing: $runtime_root"
runtime_root=$(realpath "$runtime_root")
for file in lib/ld-musl-x86_64.so.1 usr/bin/qemu-system-x86_64 bin/busybox.static boot/vmlinuz-virt; do
    [ -r "$runtime_root/$file" ] || unavailable "Missing QEMU runtime file: $file"
done
kernel_config=$(find "$runtime_root/boot" -name 'config-*-virt' | sort | tail -1)
[ -n "$kernel_config" ] || unavailable "Guest kernel configuration is missing."
kernel_version=$(basename "$kernel_config" | sed 's/^config-//')
[ -r "$runtime_root/lib/modules/$kernel_version/modules.dep" ] || unavailable "Guest kernel modules are missing: $kernel_version"
[ -d "$runtime_root/usr/share/qemu" ] || unavailable "QEMU firmware directory is missing."
qemu() {
    "$runtime_root/lib/ld-musl-x86_64.so.1" --library-path "$runtime_root/lib:$runtime_root/usr/lib" "$runtime_root/usr/bin/qemu-system-x86_64" "$@"
}
qemu --version >/dev/null 2>&1 || unavailable "QEMU or its runtime libraries cannot execute."
"$runtime_root/bin/busybox.static" true || unavailable "Static BusyBox cannot execute."
applets=$("$runtime_root/bin/busybox.static" --list)
for applet in sh mount umount modprobe mkdir uname cat grep sleep blockdev basename dirname find sort diff sha256sum wc dmesg tail sync poweroff; do
    printf '%s\n' "$applets" | grep -qx "$applet" || unavailable "Static BusyBox applet is missing: $applet"
done
if [ "$1" = --probe ]; then
    echo "KERNEL_VERSION $kernel_version"
    grep '^CONFIG_' "$kernel_config"
    for module in virtio_pci virtio_blk ext4 erofs f2fs; do
        if [ -n "$(find "$runtime_root/lib/modules/$kernel_version" -name "$module.ko*" -print -quit)" ]; then
            echo "MODULE $module"
        fi
    done
    exit 0
fi
run_root=$(realpath "$1")
script_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
mkdir -p "$run_root/logs"
cp "$kernel_config" "$run_root/logs/kernel.config"
sha256sum "$runtime_root/boot/vmlinuz-virt" "$kernel_config" > "$run_root/logs/kernel.sha256"
find "$runtime_root/lib/modules/$kernel_version" -type f -name '*.ko*' -exec sha256sum '{}' + > "$run_root/logs/modules.sha256"
stage=$(mktemp -d /tmp/uotan-mount-initramfs.XXXXXX)
trap 'rm -rf "$stage"' EXIT
trap 'exit 1' INT TERM
mkdir -p "$stage/root/bin" "$stage/root/sbin" "$stage/root/usr/bin" "$stage/root/usr/sbin" "$stage/root/dev" "$stage/root/proc" "$stage/root/sys" "$stage/root/lib/modules" "$stage/root/reference"
cp "$runtime_root/bin/busybox.static" "$stage/root/bin/busybox"
ln -s busybox "$stage/root/bin/sh"
cp -a "$runtime_root/lib/modules/$kernel_version" "$stage/root/lib/modules/"
cp "$run_root/reference/"* "$stage/root/reference/"
cp "$run_root/cases.tsv" "$stage/root/reference/cases.tsv"
cp "$kernel_config" "$stage/root/reference/kernel.config"
cp "$script_root/guest-init.sh" "$stage/root/init"
chmod +x "$stage/root/init"
mknod "$stage/root/dev/console" c 5 1
mknod "$stage/root/dev/null" c 1 3
(cd "$stage/root" && find . -print | cpio -o -H newc) > "$stage/initramfs.cpio"
gzip -1 "$stage/initramfs.cpio"
set -- -machine q35 -accel tcg -cpu max -m 1024 -smp 2 -nographic -nodefaults -serial stdio -monitor none -no-reboot \
    -L "$runtime_root/usr/share/qemu" -kernel "$runtime_root/boot/vmlinuz-virt" -initrd "$stage/initramfs.cpio.gz" \
    -append 'console=ttyS0,115200 panic=-1 nokaslr'
index=0
: > "$run_root/logs/images.before.sha256"
while IFS="$(printf '\t')" read -r name format options hash size; do
    actual_hash=$(sha256sum "$run_root/images/$name.img" | cut -d ' ' -f 1)
    [ "$actual_hash" = "$hash" ] || { echo "Image identity mismatch: $name" >&2; exit 1; }
    printf '%s  %s.img\n' "$actual_hash" "$name" >> "$run_root/logs/images.before.sha256"
    # QEMU escapes literal commas in key/value option strings by doubling them.
    image_path=$(printf '%s' "$run_root/images/$name.img" | sed 's/,/,,/g')
    set -- "$@" -drive "file=$image_path,format=raw,if=none,id=disk$index,readonly=on" \
        -device "virtio-blk-pci,drive=disk$index,serial=uotan$index"
    index=$((index + 1))
done < "$run_root/cases.tsv"
qemu --version > "$run_root/logs/qemu-version.txt"
qemu_rc=0
timeout 240 "$runtime_root/lib/ld-musl-x86_64.so.1" --library-path "$runtime_root/lib:$runtime_root/usr/lib" \
    "$runtime_root/usr/bin/qemu-system-x86_64" "$@" > "$run_root/logs/qemu-console.log" 2>&1 || qemu_rc=$?
if [ "$qemu_rc" -ne 0 ]; then cat "$run_root/logs/qemu-console.log"; exit "$qemu_rc"; fi
grep -E '^(RESULT|MOUNT_SUMMARY|MOUNT_EXIT|VERIFY_EXIT)' "$run_root/logs/qemu-console.log"
[ "$(grep -c '^RESULT ' "$run_root/logs/qemu-console.log")" = "$index" ] || { echo "Incomplete mount results" >&2; exit 1; }
: > "$run_root/logs/images.after.sha256"
while IFS="$(printf '\t')" read -r name format options hash size; do
    actual_hash=$(sha256sum "$run_root/images/$name.img" | cut -d ' ' -f 1)
    [ "$actual_hash" = "$hash" ] || { echo "Image changed during mount: $name" >&2; exit 1; }
    printf '%s  %s.img\n' "$actual_hash" "$name" >> "$run_root/logs/images.after.sha256"
done < "$run_root/cases.tsv"
diff -u "$run_root/logs/images.before.sha256" "$run_root/logs/images.after.sha256"
echo IMAGES_UNCHANGED
grep -q '^MOUNT_SUMMARY .* failed=0' "$run_root/logs/qemu-console.log"
