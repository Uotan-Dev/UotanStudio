# Firmware Kernel Mount Tests

The former standalone tools project is part of `UotanStudio.Tests`.
`FirmwareMountCheckTests` runs 14 QEMU theory cases and one native EROFS DEFLATE
extraction test. A class fixture builds only supported images and shares one QEMU
boot across the matrix.

## Run

Run the normal .NET 10 / xUnit v3 MTP suite from the repository root:

```powershell
dotnet test UotanStudio.slnx -c Debug
```

To run just this group after building:

```powershell
& './UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe' -class '*FirmwareMountCheckTests'
```

No network access or automatic setup is performed. Missing external dependencies
produce explicit xUnit skips before image generation. The tests never install
packages, edit repositories or sudoers, or load host kernel modules. Guest modules
are loaded only inside the disposable QEMU guest.

## Optional Local Infrastructure

| Environment variable | Default | Meaning |
| --- | --- | --- |
| `UOTAN_TEST_WSL_DISTRO` | `Alpine` | Existing WSL distribution used by the shared WSL helper, AVB and mount tests |
| `UOTAN_FIRMWARE_QEMU_ROOT` | `/tmp/uotan-firmware-mount-qemu/root` | Linux path to an existing extracted Alpine x86_64 QEMU/kernel runtime |
| `UOTAN_FIRMWARE_TEST_ARTIFACTS` | Temporary directory, removed after tests | Windows directory in which to retain each run's unique artifact directory |
| `UOTAN_SLOAD_F2FS` | `sload.f2fs` | Existing compression-enabled executable used by the native F2FS parser tests in their default WSL distribution |

The QEMU runtime directory must contain:

- `lib/ld-musl-x86_64.so.1`, QEMU runtime libraries, and `usr/bin/qemu-system-x86_64`.
- `usr/share/qemu/` with the corresponding firmware files.
- `bin/busybox.static` with the filesystem, hash, mount and module applets.
- `boot/vmlinuz-virt`, matching `boot/config-*-virt`, and
  `lib/modules/<kernel-version>/`, including module dependencies.

The host needs standard shell utilities, `cpio`, `gzip`, `timeout`, root access
and `CAP_MKNOD` for initramfs device nodes. QEMU uses TCG software emulation with
1 GiB RAM and two CPUs; KVM and host filesystem drivers are unnecessary. The script
checks the existing runtime and the fixture checks guest configuration and modules
for each case. Metabox uses a conservative minimum of Linux 6.18, the oldest kernel
validated by this matrix, because it has no separate Kconfig switch.

The native DEFLATE check separately requires `fsck.erofs` with extraction support
and an erofs-utils build advertising DEFLATE in `mkfs.erofs --help`.
It can run even when QEMU or guest DEFLATE support is unavailable.

To keep images and logs:

```powershell
$env:UOTAN_FIRMWARE_TEST_ARTIFACTS = 'D:/Temp/firmware-mount-check'
& './UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe' -class '*FirmwareMountCheckTests'
```

The scripts also accept `--probe` to check prerequisites without generating images.
Exit code 77 means missing infrastructure only during this preflight. Any nonzero
exit after image generation is a test failure, including a dependency disappearing.

## Coverage And Failure Policy

Every image contains 608 regular files: a 5 MiB + 123 byte compressible file, random
data, sparse data, empty files, inline-sized data, 600 directory entries, nested empty
directories, spaces, and UTF-8 names.

| Filesystem | QEMU configurations |
| --- | --- |
| EXT4 | Default, journal disabled |
| EROFS | Flat, LZ4 compact indexes, LZMA, DEFLATE, ZSTD, LZ4 full indexes, fragments with deduplication, metabox |
| F2FS | Flat, LZ4, ZSTD, LZO |

Each image is attached as a read-only block device. EXT4 uses `ro,noload`,
F2FS uses `ro,norecovery`, and EROFS uses `ro`; all add `nosuid,nodev`.
The guest checks the exact file and directory lists, verifies every SHA-256, and
unmounts. The host verifies that image hashes are unchanged after QEMU exits.

Missing WSL, tools, runtime files, or declared guest capabilities skip only affected
checks. Missing bundled test scripts, corrupted images, mount errors (including
`EINVAL` and `EBADMSG`), content mismatches, incomplete results, timeouts after
preflight, and unmount errors fail. A native extraction pass does not count as a
kernel mount pass.

Retained artifacts include source files, images, `cases.tsv`, reference manifests,
guest console, per-case statuses, kernel configuration, kernel/module hashes, QEMU
version, and image hashes before and after mounting. Existing `WslMountTests` retain
their smaller host-kernel matrix and skip missing loaded drivers or compression
capabilities without attempting to load host modules.

These checks establish interoperability for the generated fixtures and selected
options. Writable operation, crash recovery, other block sizes, and hardware boot
are outside this matrix.
