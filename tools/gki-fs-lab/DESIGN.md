# GKI 文件系统夹具设计

这套夹具验证托管实现写出的 EXT4、EROFS、F2FS 镜像能否被真实 Linux 内核挂载、读取、fsck，并在可写文件系统上完成写回。

## 分层

```text
Windows PowerShell
  └─ wslc 容器（Alpine + QEMU + mkfs/fsck + 内核编译）
       └─ QEMU 客户机（GKI 内核，唯一执行 mount/fsck 的位置）
            └─ 重打包镜像
```

容器没有 `CAP_SYS_ADMIN`，不能 loop mount。容器负责生成镜像、编译内核和启动 QEMU；真实挂载放在客户机。托管驱动以自包含 linux-musl 形式运行在容器内。

## 内核

源码按分支从 TUNA、USTC、BFSU 镜像浅克隆：

| 版本 | GKI 分支 |
| --- | --- |
| 5.10 | `android13-5.10` |
| 6.6 | `android15-6.6` |
| 6.18 | `android17-6.18` |

以 `arch/x86/configs/gki_defconfig` 为基线，合并 `configs/gki-lab.fragment`。增量打开 virtio-blk、loop、devtmpfs、串口和三种文件系统，关闭 DEBUG_INFO/BTF。5.10 用 gcc，6.6/6.18 用 clang/lld。编译结果放在 `/lab/cache/kernels/<version>-<commit>-<fragment-hash>/`，源码和构建目录在成功后删除。

内核可以单独构建，避免一次命令超时：

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Kernel -Kernels 5.10 -Jobs 5 -SkipImageBuild -RunId kernel-5.10
```

## 原始镜像

`make-fixtures.sh` 在 Linux 卷里创建源树，避免 NTFS 丢失权限和 xattr：

- 常规文件、空文件、子目录、Unicode 文件名、硬链接、符号链接、setuid 位、`user.uotan` xattr；
- 可压缩文本、不可压缩随机数据、稀疏文件；
- ext4 用 `mke2fs -d` 直接灌入目录；
- EROFS 用 `mkfs.erofs -zlz4`；
- F2FS 用 `mkfs.f2fs` 加 `sload.f2fs` 灌入。

每个原始镜像先跑对应 fsck。

## 托管往返

`UotanStudio.FilesystemFixture` 对每种格式执行：

1. `UnpackAsync` 解包并生成 preservation metadata；metadata 就是缓存。
2. 用公开 parser 生成清单：路径、类型、mode、uid/gid、大小、内容 SHA-256、符号链接、硬链接和 xattr。EROFS 用 `EnumerateEntries` 保留目录。
3. `FileSystemPreservation.TryRestore` 重打包；默认拒绝静默退化为全新构建。
4. 用 parser 读取重打包镜像，逐项比较清单。
5. 输出客户机清单与 JSON 报告。

`EquivalentBytes` 通常为 false，结构、内容和容量必须为 true。EROFS 重建镜像可能比源镜像多出布局元数据，`FitsSourceFilesystemSize` 不保证为 true。

## 客户机

`run-matrix.sh` 生成 initramfs，内含 busybox、e2fsck、fsck.f2fs、fsck.erofs、getfattr 和清单。每个内核启动一个 QEMU，挂三块 virtio-blk：

- ext4：只读挂载 → 清单校验 → `e2fsck -fn` → 读写挂载 → 写 512 B → 卸载 → fsck → 重新只读挂载校验。
- f2fs：同上，fsck 用 `--dry-run --no-kernel-check`。
- EROFS：只读挂载 → 清单校验 → `fsck.erofs` → 尝试写入，必须被拒绝。

为了给 ext4 写探针留出余量，`run-matrix.sh` 在启动前把重建 ext4 副本扩到 128 MiB 并运行 `resize2fs`。这一步不改变文件内容，但改变了重建镜像容量；报告中单独注明。

## 分步执行

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Fixtures -RunId matrix -SkipImageBuild
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Managed  -RunId matrix -SkipImageBuild
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu -RunId matrix -Kernels 5.10 -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu -RunId matrix -Kernels 6.6  -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu -RunId matrix -Kernels 6.18 -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/aggregate.ps1 -RunId matrix
```

每个 QEMU 调用只跑一个内核，避免单次命令超时。`aggregate.ps1` 解析三份串口日志，生成 9 用例 `report.json` 和 `report.md`。

## 边界

- 只验证 x86_64 QEMU 客户机，不证明真实 Android 设备或厂商内核。
- 没有 KVM 时用 TCG，结果仍有效，但不能写成硬件加速。
- GKI 配置片段关闭调试信息并打开 virtio，与 stock GKI 有差异。
- EXT4 写入探针前扩容，容量结论以扩容后的副本为准；EROFS 只验证只读拒绝。
