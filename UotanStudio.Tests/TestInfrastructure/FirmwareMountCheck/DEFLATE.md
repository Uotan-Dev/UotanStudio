# EROFS DEFLATE Guest Module

The Alpine `linux-virt-6.18.48-r0` guest has `CONFIG_ZLIB_INFLATE=y` and
`CONFIG_EROFS_FS=m`, but disables `CONFIG_EROFS_FS_ZIP_DEFLATE`. Rebuilding its EROFS
module enables DEFLATE without replacing `vmlinuz-virt` or changing the WSL host
kernel. This is explicit infrastructure setup; normal tests do not download or
install anything.

## Fixed Inputs

- Guest and development package: `linux-virt[-dev]-6.18.48-r0`, release
  `6.18.48-0-virt`, Alpine x86_64.
- [Alpine package source](https://github.com/alpinelinux/aports/blob/44f00b361c88b2e136776ddbc7f59294be80729b/main/linux-lts/APKBUILD).
  Its patches do not modify `fs/erofs`.
- [Linux stable EROFS sources](https://github.com/gregkh/linux/tree/v6.18.48/fs/erofs).
  `erofs-v6.18.48.sha256` pins all 23 source files; downloads were also checked
  against the Git blob IDs returned by that tag's tree.
- GCC `15.2.0-r9`, matching the guest compiler family. The build uses the exact
  development package's generated headers, configuration, and `Module.symvers`.

`CONFIG_EROFS_FS_ZIP_DEFLATE=y` selects `decompressor_deflate.o` in Kbuild, while
`KCFLAGS=-DCONFIG_EROFS_FS_ZIP_DEFLATE=1` enables the corresponding C branches in
the external module. Other kernel configuration and symbol versions stay intact.
The recorded effective guest configuration includes this module option; the
original configuration is retained separately. A matching vermagic alone is not
the final check: run the mount matrix to verify module loading and decompression.

## Rebuild

Inside Alpine WSL as root, create an isolated build root using trusted package keys:

```sh
apk --root /tmp/uotan-qemu-deflate-build --initdb --no-scripts --no-cache \
  --keys-dir /etc/apk/keys \
  --repository https://dl-cdn.alpinelinux.org/alpine/edge/main \
  add build-base linux-virt-dev=6.18.48-r0
chroot /tmp/uotan-qemu-deflate-build /bin/busybox --install -s
mkdir -p /tmp/uotan-qemu-deflate-build/work/erofs
```

Put the 23 files listed in `erofs-v6.18.48.sha256` from the upstream tag into
`/tmp/uotan-qemu-deflate-build/work/erofs`. The build script verifies every SHA-256
before compiling. A mirror may transport the downloads, but must not alter them.
Then run, from this repository's `FirmwareMountCheck` directory:

```sh
sh build-erofs-deflate.sh /tmp/uotan-qemu-deflate-build \
  /tmp/uotan-firmware-mount-qemu/root /mnt/d/Temp/uotan-qemu-erofs-deflate
```

The output contains the raw/compressed module, original/effective configuration,
source files and checksums, compiler and package versions, build log, symbol table,
and vermagic. BTF generation is skipped because the development package lacks
`vmlinux`; this does not affect loading or filesystem operation.

Back up the original module and configuration before installing the two outputs:

```sh
mkdir -p /tmp/uotan-firmware-mount-qemu/deflate-backup
cp -n /tmp/uotan-firmware-mount-qemu/root/boot/config-6.18.48-0-virt \
  /tmp/uotan-firmware-mount-qemu/deflate-backup/kernel.config
cp -n /tmp/uotan-firmware-mount-qemu/root/lib/modules/6.18.48-0-virt/kernel/fs/erofs/erofs.ko.gz \
  /tmp/uotan-firmware-mount-qemu/deflate-backup/erofs.ko.gz
cp /mnt/d/Temp/uotan-qemu-erofs-deflate/erofs.ko.gz \
  /tmp/uotan-firmware-mount-qemu/root/lib/modules/6.18.48-0-virt/kernel/fs/erofs/erofs.ko.gz
cp /mnt/d/Temp/uotan-qemu-erofs-deflate/kernel.config \
  /tmp/uotan-firmware-mount-qemu/root/boot/config-6.18.48-0-virt
```

The module retains its original path and has no module dependencies, so the
existing `modules.dep` remains applicable. Reversing those last two copies using
the backup directory restores the stock runtime. Do not load this module into WSL:
it is built for the QEMU guest's release only.

From Windows, execute `UotanStudio.Tests.exe -class '*FirmwareMountCheckTests'`.
Success requires `RESULT erofs-deflate PASS`, all 14 guest results, complete file
hash verification, and unchanged image hashes. The independent native DEFLATE
extraction test is the fifteenth xUnit case.
