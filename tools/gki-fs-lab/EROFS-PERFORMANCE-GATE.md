# EROFS 性能基线与语义门

## 范围与状态

- 本文件记录 44.51 MiB 语料的已测性能基线，以及任何 EROFS 构建/解包优化必须先通过的语义门。
- 基线数字来自优化工作对话，本文没有重新测量，也没有运行 QEMU/GKI、`fsck.erofs`、基准、测试或解决方案构建。
- **本文不是“优化已通过”的证据。** 新实现必须重新跑完整语义门，并在同一台机器、同一语料、同一参数下重测性能。

## 实测基线（44.51 MiB 语料）

构建时间与输出比例：

| 路径 | 配置 | 时间 | 输出比例 |
| --- | --- | ---: | ---: |
| 托管 `ErofsBuilder` | 默认：LZ4 fast、4 KiB pcluster、完整索引 | 1.50 s | 79.2% |
| 托管 `ErofsBuilder` | LZ4HC、4 KiB pcluster | 8.17 s | 71.7% |
| 托管 `ErofsBuilder` | LZ4HC、64 KiB pcluster | 25.75 s | 44.4% |
| Rust 参考实现 | LZ4 | 0.58 s | 75.6% |
| Rust 参考实现 | LZ4HC、4 KiB pcluster | 2.99 s | 71.4% |
| Rust 参考实现 | LZ4HC、64 KiB pcluster | 4.29 s | 43.6% |

提取与处理器：

| 路径 | 配置 | 时间 |
| --- | --- | ---: |
| 托管 `ErofsParser.ExtractToDirectory` | 8 工作者 | 0.34 s |
| Rust 参考提取器 | 8 工作者 | 0.154–0.254 s |
| 托管 `ErofsFormatHandler.UnpackAsync` | 处理器/保留路径 | 1.16 s |

- “输出比例”= 镜像字节 ÷ 语料负载字节，越低越紧，不是 `1 - 压缩率`。
- 对话只按顺序记录 Rust 的三组数字；上表按 LZ4、LZ4HC 4K、LZ4HC 64K 对齐。
- 原始运行的 CPU、编译器和命令参数未记录在对话中；跨机器数字不可直接比较。
- 对话没有记录 LZ4HC 的压缩等级；新增基准把两个 HC 配置固定为等级 9，以便复现。

## 2026-10-23 优化后实测（同机、同 44.51 MiB 语料）

构建（Release）：

| 路径 | 配置 | 时间 | 输出比例 |
| --- | --- | ---: | ---: |
| 托管 `ErofsBuilder` | 默认等级 9：LZ4 fast、4 KiB、完整索引 | 0.358 s | 76.6% |
| 托管 `ErofsBuilder` | LZ4HC 9、4 KiB | 0.792 s | 71.7% |
| 托管 `ErofsBuilder` | LZ4HC 9、64 KiB | 1.516 s | 44.5% |
| 托管 `ErofsBuilder` | Zstd 3、4 KiB | 0.815 s | 70.0% |
| Rust 参考实现 | `-z lz4` | 0.58 s | 75.6% |
| Rust 参考实现 | 默认 LZ4HC、4 KiB | 2.99 s | 71.4% |
| Rust 参考实现 | LZ4HC、64 KiB | 4.29 s | 43.6% |

提取：

| 路径 | 配置 | 时间 |
| --- | --- | ---: |
| 托管 `ErofsParser.ExtractToDirectory` | 8 worker | 0.288 s |
| 托管 `ErofsParser.ExtractToDirectory` | 16 worker | 0.270 s |
| 托管 `ErofsFormatHandler.UnpackAsync` | 处理器/保留路径、8 worker | 0.535 s |

语义门证据（本轮运行）：

- Gate 0：`FilesystemFixture` 往返 452 条目，`preserved=true`，结构/内容/清单一致；源与重建字节布局可因未记录的编码等级不同而不逐字节相等。
- Gate 1：`fsck.erofs -p`、`fsck.erofs --extract` 与完整 `fsck.erofs` 均退出 0；独立解出树的 443 个常规文件 SHA-256 与源树一致。
- Gate 2：`lz4l9` 镜像 `compat=0x1`、`incompat=0x1`、原始算法字段 `0xFFFF`；`hc64` 镜像 `compat=0x1`、`incompat=0x3`、原始算法字段 `0x1`。
- Gate 3：GKI `5.10.269`、`6.1.177`、`6.6.143` 上 `lz4l9` 与 `hc64` 均只读挂载、452 条目读取、`fsck.erofs` 通过、写入被拒绝；日志出现 `RESULT erofs PASS` 与 `SUMMARY cases=1 failures=0`。证据见 `artifacts/gki-fs-lab/final-lz4l9/logs/` 与 `artifacts/gki-fs-lab/final-hc64/logs/`。
- 定向 EROFS 测试：448 个通过、2 个因 `UOTAN_EROFS_CORPUS` 跳过；全量测试宿主受并行架构改动阻塞，未完成。

## 可复现基准

新增文件：

```text
UotanStudio.Benchmarks/Firmware/ErofsBenchmarkCorpus.cs
UotanStudio.Benchmarks/Firmware/ErofsFillPlannerBenchmarks.cs
UotanStudio.Benchmarks/Firmware/ErofsExtractionThroughputBenchmarks.cs
```

当前没有公开的 fill-planner 类型；`ErofsFillPlannerBenchmarks` 度量它服务的公开 `ErofsBuilder.Build` 端到端路径，只覆盖公开 API，不访问内部布局类型。若后续需要单独度量规划阶段内部耗时，必须先提供公开的诊断/计时入口，不能在基准里反射或引用 internal 成员。

运行（Release；运行期间不要同时构建、测试或跑其它基准）：

```powershell
dotnet run -c Release --project UotanStudio.Benchmarks -- --filter '*ErofsBuildFillBenchmarks*'
dotnet run -c Release --project UotanStudio.Benchmarks -- --filter '*ErofsExtractionThroughputBenchmarks*'
dotnet run -c Release --project UotanStudio.Benchmarks -- --filter '*ErofsHandlerUnpackBenchmarks*'
```

构建配置矩阵（`BlockSize` 均为 4096，均使用完整/非紧凑索引并禁用 extent 索引）：

| 基准方法 | 编解码器 | pcluster 上限 | 压缩等级 | 底层格式 |
| --- | --- | ---: | ---: | --- |
| `Lz4FourKilobyteFullIndex` | LZ4 fast | 4 KiB | 1 | LZ4 |
| `Lz4HcFourKilobyte` | LZ4HC | 4 KiB | 9 | LZ4 |
| `Lz4HcSixtyFourKilobyte` | LZ4HC | 64 KiB | 9 | LZ4 |
| `ZstdFourKilobyte` | Zstandard | 4 KiB | 9 | Zstandard |

固定参数：`Uuid` 全零、`BuildTime=1700000000`、`VolumeName="uotan-bench"`；构建与提取语料的时间戳也固定到同一纪元。构建基准把 `FirmwareParallelism.Workers` 固定为 8；提取基准使用 `[Params(1, 8)]` 并在 `GlobalSetup` 设置、`Dispose` 还原；处理器基准固定为 8。

语料由固定常量生成，不读取时钟、随机源、区域性设置或机器标识：

| 目录/条目 | 内容 | 规模 |
| --- | --- | --- |
| `text/` | 12 个类日志文本文件 | 12 × 1 MiB |
| `structured/` | 8 个 JSON Lines 结构化文件 | 8 × 1 MiB |
| `random/` | 12 个 xorshift 高熵文件 | 12 × 1 MiB |
| `large/mixed-12m.bin` | 交替 64 KiB 随机块与文本块 | 12 MiB |
| `readme-link.log` | 符号链接 → `text/text-000.log` | — |
| `text-hardlink.log` | 硬链接 ↔ `text/text-001.log` | — |

合计 44 MiB 负载。符号链接在 Windows 上没有 Developer Mode 权限时可能创建失败，硬链接在同卷非 NTFS 上可能失败；基准会打印 `symlink=False`/`hardlink=False` 后继续，失败时不得把该次运行当作包含链接的语料。所有语料、镜像和解包目录位于 `%TEMP%/uotan-bench-erofs-*`，在 `Dispose` 中删除。

注意：44.51 MiB 的实测基线使用另一套语料；合成语料只用于本仓库内的 A/B 对比。不要用本语料的绝对秒数去覆盖上表，也不要把一次噪声波动写成稳定改进。

## 语义门（必须全部通过）

### Gate 0：托管往返

```powershell
dotnet run --project UotanStudio.FilesystemFixture/UotanStudio.FilesystemFixture.csproj -c Debug -- roundtrip `
  --format EROFS --source <original.erofs> --work <work-dir> `
  --repacked <rebuilt.erofs> --manifest <manifest.tsv> --report <report.json>
```

- 退出码 0；`TryRestore == true`，`EquivalentStructure == true`，`EquivalentContent == true`。
- `EquivalentBytes` 可以为 `false`；容量按重建布局单独判断。
- 清单覆盖路径、类型、mode、uid/gid、大小、内容 SHA-256、符号链接、硬链接与 xattr；不允许用全新目录构建替代保留式重建。

### Gate 1：主机 `fsck.erofs`

```text
fsck.erofs -p <rebuilt.erofs>
fsck.erofs --extract=<extract-dir> <rebuilt.erofs>
fsck.erofs <rebuilt.erofs>
```

- `-p`：打印超级块/文件系统信息，退出码必须为 0。
- `--extract=<dir>`：实际解码每个文件并写出，退出码必须为 0；把解出目录与 Gate 0 清单逐项比较，任一内容、链接或 xattr 差异即失败。
- 第三行执行完整检查，是当前 `tools/gki-fs-lab` 对原始镜像和客户机镜像使用的形式。
- 现有 `lab/make-fixtures.sh` 与 `lab/guest-init.sh` 只调用普通 `fsck.erofs <image>`；`-p` 与 `--extract` 需要在同一容器/客户机内额外执行并记录日志，不能因为脚本里没有这两条就跳过。

### Gate 2：特性字逐位一致

在 lab 容器内运行 `dump.erofs -s <image>`（或读取 `fsck.erofs -p` 的超级块输出）。也可以用公开 API `ErofsFormatHandler.ReadMetadataAsync` 的 `FeatureCompat`、`FeatureIncompat`、`AvailableCompressionAlgorithms` 作交叉核对。

- 保留式重建：`s_feature_compat`、`s_feature_incompat`、`s_available_compr_algs` 必须与源镜像逐位相等。
- 全新构建：按下方推导值核对，多一位或少一位都失败。

| 基准配置 | `s_feature_incompat` | `s_feature_compat` | 压缩算法字段（原始 → 生效） |
| --- | --- | --- | --- |
| LZ4 fast、4K、完整索引 | `0x0001` `lz4_0padding` | `0x0001` `sb_csum` | `0xFFFF`（LZ4 最大距离）→ LZ4 |
| LZ4HC、4K、完整索引 | `0x0001` `lz4_0padding` | `0x0001` `sb_csum` | `0xFFFF`（LZ4 最大距离）→ LZ4 |
| LZ4HC、64K、完整索引 | `0x0003` `lz4_0padding + compr_cfgs/big_pcluster` | `0x0001` `sb_csum` | `0x0001` → LZ4 |
| Zstd、4K、完整索引 | `0x0002` `compr_cfgs` | `0x0001` `sb_csum` | `0x0008` → Zstandard |

没有压缩配置记录的 LZ4 镜像把 `s_available_compr_algs` 解释为 LZ4 最大匹配距离（默认 `0xFFFF`），不是算法位图；带 `compr_cfgs`/`big_pcluster` 的镜像才按位图解释。公开 API 的 `AvailableCompressionAlgorithms` 已经返回生效算法，因此逐位比较时应同时记录原始字段与生效算法，避免把距离值误读成 LZ4 位。

EROFS 不记录 LZ4 是否使用 HC 编码器，因此两行 LZ4 的磁盘特性字必须相同；HC 只改变构建时间与密度，不能改变特性字。

### Gate 3：QEMU GKI 5.10 / 6.1 / 6.6

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10,6.1,6.6 -Formats EROFS -Stage All
```

- `run.ps1` 的默认内核是 5.10/6.6/6.18；本门要求显式传入 6.1。`build-kernels.sh` 已把 6.1 映射到 `android14-6.1`，必需符号见 `lab/configs/required-6.1.txt`。
- 分阶段执行可避免单次命令超时：

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10,6.1,6.6 -Formats EROFS -Stage Fixtures -RunId erofs-gate
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10,6.1,6.6 -Formats EROFS -Stage Managed  -RunId erofs-gate -SkipImageBuild
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 5.10 -Formats EROFS -Stage Qemu -RunId erofs-gate -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 6.1  -Formats EROFS -Stage Qemu -RunId erofs-gate -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Kernels 6.6  -Formats EROFS -Stage Qemu -RunId erofs-gate -SkipImageBuild -Accel tcg
```

分阶段运行时，每个内核的最终证据是该次运行重写的 `logs/qemu-<kernel>.log`；需要单一汇总报告时用 `-Stage All` 一次跑完，`run.ps1` 会在 `artifacts/gki-fs-lab/<run-id>/report.json` 写入 `kernels`、`formats`、`cases`（本门为 3 条 EROFS）与 `status`。`aggregate.ps1` 只适用于它硬编码的 5.10/6.6/6.18 × 三格式矩阵，不要用它汇总 EROFS-only 的 5.10/6.1/6.6 运行。

每个内核的 EROFS 用例必须满足：

1. `mount -t erofs -o ro <device> <mnt>` 成功。
2. 整树清单与读取校验通过（`verify_tree`，含符号链接与硬链接）。
3. `fsck.erofs <device>` 退出码 0。
4. 写探针必须被拒绝（`touch` 返回非零，只读文件系统）；不得通过可写挂载、`remount,rw` 或复制到其它文件系统绕过。
5. 卸载成功，客户机 `dmesg` 不出现文件系统损坏错误。

证据：`artifacts/gki-fs-lab/<run-id>/logs/qemu-<kernel>.log` 与 `report.json`。三个内核的日志都必须出现 `RESULT erofs PASS` 且 `SUMMARY cases=1 failures=0`；`-Stage All` 生成的 `report.json` 应显示 `cases` 含三条 EROFS、`status=pass`。缺少任一内核的日志都按失败处理。

## 通过清单

- [ ] Gate 0：`TryRestore=true`，结构/内容等价，清单逐项一致。
- [ ] Gate 1：`fsck.erofs -p`、`fsck.erofs --extract`、`fsck.erofs` 全部退出码 0，解出树与清单一致。
- [ ] Gate 2：源与重建的特性字逐位相等；全新构建的实际字与配置推导值一致。
- [ ] Gate 3：GKI 5.10、6.1、6.6 均只读挂载、清单读取、fsck 通过，写入被拒绝，日志齐全。
- [ ] 性能对比：同一台机器、同一语料、同一参数、无并发构建/测试；基线数字与本文件一致。
- [ ] 证据只保留日志、报告与必要产物；语料、镜像与解包目录已从 `%TEMP%`/工作区删除。

## 边界

- 本文只记录基线与门禁，不证明任何新优化已通过；基准耗时不能替代 Gate 0–3。
- `tools/gki-fs-lab` 只验证 x86_64 QEMU GKI 内核路径，不证明真实 Android 设备、厂商内核、AVB 或分区表。
- 没有 KVM 时使用 TCG，报告必须标注 `acceleration=tcg`，不能写成硬件加速。
- 新增 Zstd 基准只测构建；对话没有给出 Zstd 的实测基线，因此本文不为 Zstd 编造数字。