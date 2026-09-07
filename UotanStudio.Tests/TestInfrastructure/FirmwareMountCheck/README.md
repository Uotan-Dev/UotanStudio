# Firmware Kernel Mount Tests

The former standalone tools project is part of `UotanStudio.Tests`.
`FirmwareMountCheckTests` runs 14 QEMU theory cases and one native EROFS DEFLATE
extraction test. A class fixture builds only supported images and shares one QEMU
boot across the matrix.

`NativeFileSystemInteropTests` also checks all 14 packed variants through managed
extraction, and reads independently generated EXT2/EXT3/EXT4, EROFS flat/LZ4/DEFLATE,
and F2FS images produced by the installed native tools. Both directions compare the
entire file tree, empty directories, lengths and SHA-256 hashes. Native EXT images
may additionally contain the empty `lost+found` recovery directory.

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
| `UOTAN_FIRMWARE_TEST_ARTIFACTS` | Temporary directory, removed after successful tests | Windows directory in which to retain each run's unique artifact directory; failed mount runs are always retained |
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

For the stock Alpine 6.18.48 guest, [DEFLATE.md](DEFLATE.md) documents the pinned
EROFS module rebuild that enables the missing DEFLATE option. It includes a
separate build root, original-module backup and runtime verification procedure.

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

Guest filesystem failures are assigned to their individual theory cases after a
complete run. Infrastructure failures, missing/duplicate results, or changed image
hashes fail the shared run. The host script exits zero when collection and identity
checks succeed and all guest cases pass. Exit 1 with a complete result set, summary
and `IMAGES_UNCHANGED` marker is handled per case by the C# fixture, which asserts
each case's `RESULT <name> PASS`. Missing completion markers remain shared failures.

Retained artifacts include source files, images, `cases.tsv`, reference manifests,
guest console, per-case statuses, kernel configuration, kernel/module hashes, QEMU
version, and image hashes before and after mounting. Existing `WslMountTests` retain
their smaller host-kernel matrix and skip missing loaded drivers or compression
capabilities without attempting to load host modules.

These checks establish interoperability for the generated fixtures and selected
options. Writable operation, crash recovery, other block sizes, and hardware boot
are outside this matrix.

## Upstream Definitions

The regression fixes follow these definitions rather than matching existing images:

- [Linux v6.18 EROFS on-disk structures](https://github.com/torvalds/linux/blob/v6.18/fs/erofs/erofs_fs.h):
  `z_erofs_lzma_cfgs` (14 bytes), `z_erofs_deflate_cfgs` and `z_erofs_zstd_cfgs`
  (6 bytes each), each preceded by its own little-endian length; no common format header.
- [Linux v6.18 EROFS block mapping](https://github.com/torvalds/linux/blob/v6.18/fs/erofs/data.c):
  `erofs_map_blocks` maps complete flat blocks separately from the final inline tail,
  including directory data.
- [e2fsprogs v1.47.0 checksums](https://github.com/tytso/e2fsprogs/blob/v1.47.0/lib/ext2fs/csum.c):
  `ext2fs_superblock_csum`, `ext2fs_dx_csum` and directory tails define the exact CRC
  regions; unused htree slots are excluded and the directory tail marker is `0xDE`.
- [e2fsprogs v1.47.0 disk structures](https://github.com/tytso/e2fsprogs/blob/v1.47.0/lib/ext2fs/ext2_fs.h):
  `s_reserved_gdt_blocks` is LE16 at `0xCE`, `s_def_hash_version` is at `0xFC`,
  and `s_checksum` is at `0x3FC`. Backup superblocks begin at their block-group
  boundary and carry their own group number and checksum.
- [Linux v6.18 F2FS disk structures](https://github.com/torvalds/linux/blob/v6.18/include/linux/f2fs_fs.h):
  `f2fs_inode.i_ctime` and `i_mtime` are LE64 seconds at offsets 40 and 48;
  the parser and builder must preserve values beyond 32 bits.

The native reverse tests additionally exercise zero indirect pointers in EXT2/EXT3
sparse files and multi-block EROFS inline directories. `FileSystemLayoutRegressionTests`
checks field offsets and F2FS timestamps beyond 2106. Native EXT4 backup-superblock
verification uses `e2fsck -fn -b 32768 -B 4096` on images containing 129 MiB files
(with and without a journal) and a 513 MiB file. These tests verify cross-group
allocation, external extent-tree checksums (`ext2fs_extent_block_csum`), and full
content hashes. The allocator splits extents at group metadata and reserves the
journal before file allocation; it never assumes a whole file fits in one group.
An additional near-full-group case verifies that replanning does not duplicate
logical runs when resize metadata requires another group.
