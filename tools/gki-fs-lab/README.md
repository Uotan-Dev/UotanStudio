# GKI 文件系统往返夹具

用 WSL2 Alpine、`wslc` 和 QEMU 验证 UotanStudio 的 EXT4 / EROFS / F2FS 写入结果。主线工具生成原始镜像，托管实现解包、缓存并重打包，客户机内核负责挂载、fsck 和写回。

## 前置条件

- Windows x64，WSL2 与 `wslc` 可用。
- Alpine 或等价 WSL 发行版能运行 `wslc`。
- 优先准备 `/dev/kvm`；没有时自动降级到 TCG，并在报告里标注。
- 至少 80 GB 临时空间。内核源码与镜像都在 Linux 卷，不写在仓库工作区。

## 快速开始

```powershell
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 `
  -Kernels 5.10,6.6,6.18 `
  -Formats EXT4,EROFS,F2FS
```

分阶段运行：

```powershell
# 只编译/复用内核
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Kernel -Kernels 5.10,6.6,6.18

# 分步执行，避免单次命令超时
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Fixtures -RunId matrix -SkipImageBuild
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Managed  -RunId matrix -SkipImageBuild
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu     -RunId matrix -Kernels 5.10 -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu     -RunId matrix -Kernels 6.6  -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -Stage Qemu     -RunId matrix -Kernels 6.18 -SkipImageBuild -Accel tcg
pwsh -NoProfile -File tools/gki-fs-lab/aggregate.ps1 -RunId matrix

# 失败诊断：保留运行时镜像与容器工作目录
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -KeepWork

# 清理夹具缓存卷
pwsh -NoProfile -File tools/gki-fs-lab/run.ps1 -PruneCache
```

## 产物

```text
artifacts/gki-fs-lab/<run-id>/
  report.md
  report.json
  logs/
  reports/managed-<format>.json
  manifests/<format>.tsv
  kernels/<version>-meta.json
```

成功运行只保留日志和报告；镜像、驱动临时目录和容器工作目录会删除。`-KeepWork` 会保留现场，便于排查失败。QEMU 启动前会把 EXT4 副本扩到 128 MiB，给写入探针留出余量；EROFS 保持只读，重建镜像可能因布局元数据比源镜像略大。

## 常见问题

- 基础镜像拉取失败：使用 `docker.m.daocloud.io/library/alpine:3.20`，或已有 `uotan-gki-fs-lab` 镜像。
- APK 拉取慢：Dockerfile 已切到 TUNA；失败时改 USTC。
- 内核源码拉取失败：脚本按 TUNA → USTC → BFSU 重试，不访问 AOSP 主站。
- 没有 `/dev/kvm`：确认 Windows 嵌套虚拟化；否则脚本用 TCG，报告会写 `acceleration=tcg`。
- 容器内不能 mount：这是设计边界；真实挂载只在 QEMU 客户机执行。
- `TryRestore` 返回 false：保留 `images/`、`reports/` 和日志，检查 preservation metadata；不要静默接受全新构建。

## 相关文档

- [DESIGN.md](DESIGN.md)
- [../../skills/gki-filesystem-fixture/SKILL.md](../../skills/gki-filesystem-fixture/SKILL.md)
