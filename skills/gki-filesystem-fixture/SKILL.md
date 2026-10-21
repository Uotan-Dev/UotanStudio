---
name: gki-filesystem-fixture
description: >-
  Run and accept UotanStudio's GKI filesystem round-trip fixture on WSL2 Alpine,
  wslc and QEMU. Use when validating managed EXT4, EROFS or F2FS unpack/cache/repack
  output against Google GKI kernels 5.10, 6.6 or 6.18, including mount, fsck,
  write-back (except EROFS) and cleanup evidence.
license: MIT
---

# GKI 文件系统往返夹具

本 skill 定义 UotanStudio 托管文件系统写入方的内核验收流程：用主线工具生成原始镜像，用项目托管实现解包、缓存并重打包，再在 QEMU 客户机中按 GKI 内核执行挂载、读取、fsck 与写入回读。它解决的是“托管清单自己一致”之外的最后一层互操作证据。

## 当前状态与入口

先确认夹具已经落地：

```powershell
Test-Path tools/gki-fs-lab/run.ps1
Test-Path UotanStudio.FilesystemFixture/UotanStudio.FilesystemFixture.csproj
```

- 两者都存在：按本 skill 运行并验收。
- 任一缺失：本 skill 是目标契约与实现规格，不得用旧日志、旧容器结果或托管的内部往返替代真实内核验收，也不得声称已通过。

约定入口：

```text
tools/gki-fs-lab/run.ps1                  Windows 总编排
tools/gki-fs-lab/lab/build-kernels.sh    内核源码拉取与编译
tools/gki-fs-lab/lab/make-fixtures.sh    主线工具原始镜像
tools/gki-fs-lab/lab/run-matrix.sh       QEMU 客户机矩阵
tools/gki-fs-lab/lab/guest-init.sh       客户机验收脚本
UotanStudio.FilesystemFixture             托管解包/缓存/重打包夹具
docs/gki-fs-lab.md                        技术文档
artifacts/gki-fs-lab/<run-id>/            仅日志与报告
```

## 何时使用

- 修改了 EXT4、EROFS 或 F2FS 的 writer，需要证明内核仍能挂载、读取与 fsck。
- 需要验证 `FileSystemPreservation.TryRestore` 的保留式重建在真实内核中成立。
- 需要 5.10、6.6、6.18 多内核版本的兼容性矩阵。
- 需要一份可复现的 WSL2 Alpine + wslc + QEMU 工装说明与验收报告。

## 何时不要使用

- 只做托管 parser/builder 的单元往返：先用 `UotanStudio.Tests` 的定向测试。
- 需要真实 Android 设备、AVB、Payload 或厂商分区验收：本夹具只证明文件系统内核路径。
- 没有 WSL2/wslc 或不允许运行 QEMU：改用仓库外已有的验收证据，不要伪造结果。
- 只是清理旧夹具或临时文件：按当前项目的清理规则执行，不要启动整套内核编译。

## 前置条件

| 项目 | 要求 |
| --- | --- |
| 宿主 | Windows x64，WSL2，`wslc` 可运行 |
| WSL 发行版 | Alpine；`wslc` 容器为 Alpine 3.20 或更新的工具镜像 |
| 虚拟化 | 优先 `/dev/kvm`；没有时允许 TCG，但报告必须写明 `acceleration=tcg` |
| 磁盘 | 至少 80 GB 临时空间；内核源码、对象文件和镜像都在 Linux 卷 |
| 网络 | AOSP 源码走 TUNA → USTC → BFSU；Docker 基础镜像走 daocloud 缓存；不要依赖 AOSP 主站 |
| 仓库 | 当前 `UotanStudio` 工作树；不得改动无关文件 |

KVM 需要 Windows 嵌套虚拟化。没有 `/dev/kvm` 时不要修改系统配置后不验证；按“KVM 不可用”降级并在报告中标注。WSL 重启会中断正在运行的容器，执行前确认没有其他重要任务。

## 夹具组成

| 层 | 职责 |
| --- | --- |
| Windows/PowerShell | 编排、参数、退出码、报告汇总 |
| `wslc` 容器 | 主线 mkfs/fsck、内核编译、initramfs、QEMU |
| QEMU 客户机 | 唯一执行真实 mount/fsck/写回的位置 |
| `UotanStudio.FilesystemFixture` | 托管解包、preservation metadata 缓存、`TryRestore`、清单比对 |
| 内核 | `android13-5.10`、`android15-6.6`、`android17-6.18` 的 x86_64 GKI 配置派生内核 |

容器没有 `CAP_SYS_ADMIN`，不能 loop mount；不要把容器内 mount 结果当证据。客户机通过 virtio-blk 接收重打包镜像。

## 快速开始

在仓库根执行：

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 `
  -Kernels 5.10,6.6,6.18 `
  -Formats EXT4,EROFS,F2FS
```

分阶段运行：

```powershell
# 只编译/复用内核
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10,6.6,6.18 -Stage Kernel

# 用已有内核，重建镜像并跑完整矩阵
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10,6.6,6.18 -Stage All -ReuseKernels

# 失败诊断：保留镜像、源码树和容器工作卷
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -KeepWork

# 清理夹具缓存卷（先确认不再需要复用内核）
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -PruneCache
```

常用参数：

| 参数 | 含义 |
| --- | --- |
| `-Kernels` | 逗号分隔，默认 `5.10,6.6,6.18` |
| `-Formats` | 逗号分隔，默认 `EXT4,EROFS,F2FS` |
| `-Accel auto\|kvm\|tcg` | 默认 `auto`；`kvm` 不可用时显式失败或按约定降级 |
| `-ReuseKernels` | 复用缓存的内核，不重新拉源码/编译 |
| `-KeepWork` | 失败或调试时保留临时树；不作为通过证据 |
| `-PruneCache` | 删除夹具缓存卷；必须先确认 |

## 验收流程

### Gate 0：环境预检

检查并写入 `environment.json`：

- `wslc` 版本、WSL 内核、Alpine 版本。
- QEMU 版本、`/dev/kvm` 是否存在、最终 `acceleration`。
- 容器镜像 ID、命名卷、仓库绑定路径。
- CPU 核数、内存、可用磁盘。

通过条件：`wslc run` 能启动工具镜像；容器内 `qemu-system-x86_64 --version` 成功；仓库路径可读写；临时工作卷可用。

### Gate 1：内核编译

对每个版本：

1. 按镜像顺序拉取对应分支的浅克隆，记录实际 commit。
2. 以 `arch/x86/configs/gki_defconfig` 为基线，合并 `gki-lab.fragment`。
3. `olddefconfig` 后校验必需符号：`EXT4_FS`、`EROFS_FS`、`EROFS_FS_ZIP`、`F2FS_FS`、`VIRTIO_BLK`、`VIRTIO_PCI`、`BLK_DEV_LOOP`、`DEVTMPFS`、`SERIAL_8250`、`IKCONFIG_PROC`。
4. 编译 `bzImage`；5.10 用 gcc，6.6/6.18 用 clang/lld；测试片段关闭网络、声卡、DRM、USB 等与文件系统验收无关的子系统。
5. 记录 `kernelrelease`、配置 SHA-256、`bzImage` SHA-256、构建日志。

通过条件：三个版本都生成 `bzImage`；客户机启动后的 `uname -r` 属于对应主版本；`/proc/config.gz` 中文件系统与 virtio 驱动为 `=y`。编译失败时不得改用非 GKI 配置冒充。

### Gate 2：主线工具生成原始镜像

在 Linux 卷中创建源树并生成：

```text
mke2fs -q -F -t ext4 -b 4096 -d <tree> ext4.img
mkfs.erofs -zlz4 -b 4096 -T <fixed-time> --all-time <tree> erofs.img
mkfs.f2fs -f -O extra_attr <image>
sload.f2fs -f <tree> -P -t / <image>
```

每个原始镜像先跑对应 fsck：

- ext4：`e2fsck -fn`
- EROFS：`fsck.erofs`
- F2FS：`fsck.f2fs --dry-run --no-kernel-check`

通过条件：三条基线 fsck 退出码为 0；镜像摘要写入报告；源树不得留在仓库或盘符根目录。

### Gate 3：托管解包、缓存、重打包

对每种格式调用托管 API：

1. `Ext4FormatHandler` / `ErofsFormatHandler` / `F2fsFormatHandler` 的 `UnpackAsync`。
2. 确认输出目录中存在 `.uotan` preservation metadata；记录其路径、大小与 SHA-256。这就是缓存。
3. 调用 `FileSystemPreservation.TryRestore`，要求返回 `true`；不允许静默退化为全新目录构建。
4. 捕获等价报告键：`EquivalentStructure`、`EquivalentContent`、`EquivalentBytes`、`FitsSourceFilesystemSize`、`RebuiltSize`。
5. 用公开 parser 分别读取原始与重打包镜像，生成清单并逐项比较。

清单必须覆盖：路径、类型、mode、uid/gid、大小、内容 SHA-256、符号链接目标、xattr、硬链接规范路径。

通过条件：`TryRestore == true`；结构与内容等价为 `true`；原始/重打包清单一致；任何差异都保留现场并给出首个不一致路径。

### Gate 4：QEMU 客户机矩阵

每个内核启动一个 QEMU，挂载三块重打包镜像。每个格式执行：

- ext4：只读挂载 → 整树清单校验 → 卸载 → `e2fsck -fn` → 读写挂载 → 写入 4 KiB 文件 → `sync` → 卸载 → 再次 `e2fsck -fn` → 只读挂载校验写回文件与整树。
- f2fs：同上，fsck 用 `--dry-run --no-kernel-check`。
- EROFS：只读挂载 → 整树清单校验 → `fsck.erofs` → 写入必须被拒绝（只读文件系统），不得把 EROFS 当作可写格式。

通过条件：9 个用例全部出现 `RESULT <kernel> <format> PASS`；挂载、读取、fsck、写入/拒绝、回读与卸载步骤均有日志；客户机 `dmesg` 不出现文件系统损坏错误。

### Gate 5：报告与清理

生成：

```text
artifacts/gki-fs-lab/<run-id>/
  report.md
  report.json
  environment.json
  logs/build-<version>.log
  logs/managed-<format>.log
  logs/qemu-<version>.log
  reports/managed-<format>.json
  reports/guest-<version>.json
  kernels/<version>-meta.json
```

通过条件：`report.json` 显示 `cases=9`、`failed=0`、`status=pass`；每个用例有独立证据；日志与报告之外的临时文件已删除。

## 通过标准

| 验收项 | 必须为真 |
| --- | --- |
| 内核版本 | 5.10、6.6、6.18 各至少一次客户机启动 |
| 内核配置 | 三格式与 virtio-blk 为内建 |
| 原始镜像 | 主线 mkfs + 基线 fsck 通过 |
| 托管重建 | `TryRestore == true` 且结构/内容等价 |
| 清单 | 原始与重打包逐项一致 |
| ext4 | 只读读、fsck、写入、回读、再 fsck |
| f2fs | 只读读、fsck、写入、回读、再 fsck |
| EROFS | 只读读、fsck、写入拒绝 |
| 证据 | 9/9 PASS，日志与 JSON 可追溯 |
| 清理 | 仅保留日志与报告 |

## 报告解读

`report.json` 至少包含：

```json
{
  "runId": "20261021T000000Z",
  "status": "pass",
  "environment": { "acceleration": "kvm", "qemu": "...", "wslc": "..." },
  "kernels": [{ "version": "5.10", "uname": "5.10.x-android13", "commit": "..." }],
  "cases": [
    {
      "kernel": "5.10",
      "format": "EXT4",
      "mount": "pass",
      "read": "pass",
      "fsckBefore": "pass",
      "write": "pass",
      "readBack": "pass",
      "fsckAfter": "pass",
      "result": "pass"
    }
  ],
  "summary": { "total": 9, "passed": 9, "failed": 0 }
}
```

- `EquivalentBytes` 为 `false` 不必然是失败：重打包布局/压缩可以不同；结构、内容与容量必须为真。
- `acceleration=tcg` 时不得写成“硬件加速通过”。
- 缺少任一 `RESULT ... PASS` 都按失败处理，不能用托管内部校验替代。

## 临时文件与清理

- 宿主临时文件：`%TEMP%\uotan-gki-fs-lab\<run-id>`，运行结束或失败退出时删除。
- 容器临时文件：`/lab/work`、`/lab/cases`；成功后删除，失败时可用 `-KeepWork` 保留现场。
- 只保留 `artifacts/gki-fs-lab/<run-id>/` 下的日志、报告和 kernel meta；镜像、源码树、构建目录不得留在仓库、盘符根目录或用户目录。
- 内核缓存卷属于夹具缓存，不属于运行输出；删除前必须确认，使用 `-PruneCache` 显式执行。
- 禁止删除 `D:\Firmware`、当前仓库源码、未知工程目录或其他任务的文件。

## 故障处置

| 现象 | 处置 |
| --- | --- |
| 容器镜像拉取失败 | 使用 `docker.m.daocloud.io/library/alpine:3.20` 或已有工具镜像；APK 源切 TUNA/USTC |
| 内核源码拉取慢/失败 | 按 TUNA → USTC → BFSU 重试；记录实际镜像与 commit；不要回退 AOSP 主站 |
| `gki_defconfig` 不存在 | 停止并报告分支差异；不得用普通 `x86_64_defconfig` 冒充 GKI |
| 5.10 编译失败 | 检查 gcc 兼容 wrapper 与 `-std` 问题；保留 build log |
| 6.6/6.18 编译失败 | 检查 clang/lld 版本、关闭 BTF/DEBUG_INFO 的片段是否生效；不要用 `NoWarn` 式掩盖 |
| 没有 `/dev/kvm` | 启用嵌套虚拟化后重启 WSL 验证；否则 TCG 并在报告注明 |
| 容器无法 mount | 预期行为；mount 只能在 QEMU 客户机执行 |
| `TryRestore` 返回 false | 保留原始/重打包镜像与 metadata；检查 preservation payload；不得静默接受全新构建 |
| fsck 失败 | 保留镜像与 fsck 日志；区分退出码 1（已修正）与真实损坏 |
| QEMU 挂死 | 使用超时与串口日志尾部；检查磁盘顺序、virtio 设备与内核命令行 |
| 9 个结果不全 | 按失败处理；检查 initramfs、`cases.tsv` 与每个客户机日志 |

## 扩展

- 增加内核：在 `run.ps1`/`build-kernels.sh` 增加分支、配置片段和必需符号文件，更新文档与报告矩阵；新增版本必须实际启动客户机。
- 增加格式：先补主线 mkfs/fsck 路径与托管 handler，再加入清单与客户机用例；不得只测托管内部往返。
- 性能调优：优先 `-j$(nproc)`、ccache、KVM、virtio 多队列；性能结论不能替代正确性验收。

## 边界

- 本夹具只验证 x86_64 QEMU 客户机中的文件系统内核路径，不证明真实 Android 设备、厂商内核、AVB 或分区表。
- GKI 配置片段会关闭 DEBUG_INFO/BTF 并开启 virtio；这些是测试环境改动，需在报告中与 stock GKI 区分。
- 容器无 `CAP_SYS_ADMIN` 是设计边界；不要把容器内“无法挂载”误判为镜像损坏。
- 当前项目规则要求测试与诊断临时文件落到主机 `%TEMP%`；生产代码的原子替换临时文件仍留在目标同目录/同卷，不适用本夹具的临时文件迁移。
