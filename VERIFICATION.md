# 验证状态与边界

本文件记录当前可复现的验证结果。每项测量标注日期与平台；常驻约束和命令见根 `AGENTS.md`，测试过滤器与环境门控见 `UotanStudio.Tests/AGENTS.md`。

## 门禁基线

| 平台 | 最近验证 | 结果 |
| --- | --- | --- |
| Windows x64，Debug | 2026-11-14 | 全量 9440 用例，9422 通过，0 失败，18 跳过；构建 0 警告 0 错误 |
| Windows x64，Debug | 2026-11-13 | 全量 9428 用例，9403 通过，0 失败，25 跳过；构建 0 警告 0 错误 |
| Linux ARM64，Debug | 2026-11-12 | 全量 9334 用例，9304 通过，0 失败，30 跳过 |
| RISC-V（SpacemiT X60），Debug | 2026-11-12 | 全量 9334 用例，9287 通过，11 失败，36 跳过；失败均为平台环境缺口 |

- Windows 的 `dotnet format whitespace` 与 `style --verify-no-changes` 均为零退出码；NativeAOT win-x64 发布成功，无本仓库程序集产生的裁剪/AOT 警告。剩余 IL2026 来自 Avalonia XAML、Dock 等第三方程序集与 SDK 的实验性 GC 提示。
- 本体目录收敛（2026-11-14）：`AppPaths` 统一按用户数据根（Windows `%APPDATA%`、macOS `~/Library/Application Support`、Linux XDG），`Program.Main` 首跑幂等迁移 `%LOCALAPPDATA%` 的 `apple-lockdown`/`delivery-tools` 与 macOS 旧 XDG 根；新建解决方案默认目录改为 `Documents\UotanStudio`；伴随 MCP 沙箱根改用持久化 `McpExposeRootDirectory`（空白回退不受限并关闭刷写门控），hdc `-cwd` 改为调用方注入本地文件目录。`InstallDirectoryBoundaryTests` 冻结 `AppContext.BaseDirectory` 只读例外与工作目录依赖清单；迁移幂等性由 `AppPathsMigrationTests` 覆盖。AOT 发布 39 条警告与既有基线一致。MSIX 打包、macOS .app 打包与打包态安装冒烟未验证；首跑迁移仅经单元测试，真实升级路径未实测。
- Linux ARM64 构建 0 警告 0 错误，失败清零后完成上述全量测试；SIMD 内核在 AdvSIMD、SVE、SVE2 硬件上执行，`Vector<T>` 宽度为 128 位。
- RISC-V 当前 SDK 的 Roslyn 编译存在多核/R2R 冻结缺陷。测试环境使用 `DOTNET_ReadyToRun=0`、`DOTNET_PROCESSOR_COUNT=4`、`DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`，并关闭构建期裁剪分析器与 Roslyn 分析器。11 个失败中 10 个来自 SkiaSharp 缺少 `linux-riscv64` 原生资产，1 个为单类复跑可通过的时序断言。
- Windows 真机已覆盖 EDL 设备识别、WinUSB 接管与取消接管往返、ADB 与初始 USB 快照；取消接管后设备恢复 `qcwdfserial`，Problem 0。Windows 串口 `COMn` 回退未验证。
- Linux aarch64 真机已验证 EDL 串口回退：`qcserial` 绑定 9008 后经 `EdlSerialPortFallback` 找到 `/dev/ttyUSB0`，完成 Sahara、programmer 上传与 Firehose 初始化。Firehose `power reset` 在 ACK 后约 1.0 秒断开、约 39 秒重新枚举为 05C6:90db。
- 全志整机烧录、RKDL 真机维护命令、macOS/HarmonyOS USB 后端与部分 HDC 文件收发没有真机通过记录。

## 性能

GC 测量（2026-11-07，Release，16 逻辑 CPU，合成分配负载）：

| 模式 | churn / survival / loh | survival p99 / p999 | 累计暂停占比 | 稳态堆 |
| --- | --- | --- | --- | --- |
| Satori Interactive | 1.70–1.81 / 2.57–2.66 / 2.64–2.66 s | 54 µs / 424–441 µs | 3.65–4.00% | 124–126 MB |
| Satori LowLatency | 与 Interactive 相差 ±3% 内 | 50–52 µs / 181–182 µs | 1.60–2.08% | 252–256 MB |
| 默认工作站 GC | 分配吞吐与 Satori 持平 | 2.3 ms / 4.1 ms | 30.1–31.1% | 151–208 MB |

默认 GC 的 survival 完成时间为 11.8–12.2 s，约为 Satori 的 4.5 倍。以上为分配压测，不代表应用混合负载。

包与构建性能：

- ZLinq 同作业对照：`PclusterBlockSum` 46.10→10.51 µs，分配 64 KB→0；`FieldDuplicateGuard` 476.30→162.14 µs，分配 1.55 MB→0.11 MB；`InterfaceRelocation` 耗时 318.93→441.46 µs，分配 1.216 MB→0.480 MB。
- EROFS 打包写数据并行化后，1408 个 4 KB 文件加 1 MB 大文件的 zstd 构建 2587.7–2810.6 ms→1668.8–1789.6 ms，约 1.55×；同规模 lz4 2923.7→1837.6 ms。六次构建镜像 SHA-256 一致。
- 当前 HotPath 基线：`ErofsCompressedExtract` 2649.4 µs/8.32 MB、`Ext4MetadataWalk` 244.8 µs/938 KB、`DeliveryShaChecksumLoop` 44572.9 µs/1.08 MB、`ByteEquivalenceScan` 8202.1 µs/64.5 KB。

设备链路（真机）：

- eMMC 整盘写入 8,791,261,184 B 耗时 5 min 28 s（约 27 MB/s），回读前 1 MiB 一致；整盘写入后的启动未验证。
- SPINOR 4 MiB loader 全量回读一致，用例 35 s；末块擦除后回读全为 `0xFF`。
- HDC 详情 11 条查询合并会话后 8106 ms→约 1215 ms；ADB 详情合并查询与静态属性缓存使完整读取 3829 ms→650 ms。
- Windows USB 全量扫描 32–34 ms，增量 13–18 ms；48 个端点，连续增量集合一致。

## 已知不稳定项

- 环境变量未设置时按门控跳过：`UOTAN_MAGISKBOOT_ORACLE`、`UOTAN_SELINUX_POLICY_SAMPLE`、`UOTAN_SAMSUNG_*`、`UOTAN_EROFS_CORPUS`、`UOTAN_HDC_MIRROR_DIRECT`、`UOTAN_DEVICE_SHELL_TARGET`、`UOTAN_QCN_SAMPLE`；RKDL 真机与未随包的 scrcpy jar 同样跳过。
- `FileMagicDifferentialTests` 依赖 GNU `file`。`PATH` 指向 WinuxCmd 等替代实现时，XZ/Zstandard/bzip2/cpio 夹具会被报成 `data`/`ASCII text`，导致 4 例失败。
- `ErofsRebuildOptimizerTests`、`ErofsCaptureFidelityTests` 与 `PackingOptionPropagationTests` 共用进程级 `ErofsBuildDefaults.RebuildOptimization`，必须在 `[Collection("Workspace state")]` 中串行。`NodeTypeRegistry`/`PluginRegistry.Shared` 的写入方必须在 `[Collection("Node type registry")]` 中串行。
- `HdcServerTransferTests` 在并行负载下出现并发计时抖动，单类复跑通过；全量失败不能直接归为协议缺陷。
- 架构门存在已知缺口：`FormatProbeBoundaryGuardTests` 只匹配字面拼写；`ContainerBoundaryGuardTests.AllowedPaths` 为空；分层门未覆盖 `Views/Editors/ScreenMirrorPixelBuffer.cs` 对 `UotanStudio.Media.Decoding` 的导入；`ProjectReferenceDirectionGuardTests` 跳过 `UotanStudio.Tests`。
- EROFS 用例断言 inode 形态、advise 位或镜像字节时必须固定 `BuildTime` 与源 mtime。EXT4 默认日志超级块 UUID 随机，默认参数下不保证逐字节相同。
- 测试宿主输出目录必须留在仓库内；测试临时文件必须写入 `%TEMP%` 并在结束后删除。

## 未验证边界

**固件与文件系统**

- 仓库内没有真实 IMAGEWTY 镜像；夹具按参考结构合成。真实 V1 镜像占比、`original_length` 语义与加密容器密钥未实测。`checksum` 只报告不校验，Twofish 解密未实现。
- 多片 NAND/NOR 整片擦除、eMMC `ERASE_LBA` 分支、SD/UFS/PCIE/SATA 按地址写入与擦除未上机；eMMC 整盘写入后的启动未验证。
- `prm` 命令没有实现，无法执行格式对照。

**设备与协议**

- 没有全志设备：FEL/FES 握手、DRAM 轮询、组件槽位上传与整机烧录未上机；`Flash` tag、4 字节 flash 类型内联负载、稀疏分区写入、`(Normal, action)` 工具模式与 NAND/SPI NAND boot0 查询待真机确认。
- RKDL `WIPE`、子码返回值、坏块位图与 SDRAM 重枚举只有线格式证据；maskrom→usbplug 端到端与无 loader 烧录未复测；`bcdUSB` 阶段证据未在真机观测。
- SPINOR/SPINAND/SATA/PCIE/UFS/RVD 的存储切换只有 eMMC/SD 可对照官方工具；READ_STORAGE 多位置位语义未观测。
- Windows 未覆盖 alt setting 切换与 Legacy/Rockusb 分支；Linux、libusb、macOS、HarmonyOS 后端只有编译与静态核对；macOS IOKit 与 HarmonyOS DDK 路径未上机。
- HDC 文件收发、ADB 通道逐帧 trace 与调试模式未用真实设备执行；HDC 缓存复用未在 HarmonyOS 真机跑完第二轮；运行中拔插设备只经代码路径验证。
- SpacemiT 整包烧录已在 OrangePi RV2/K1 完成；勾选子集烧录、LE2110/ferrari 的 EDL 整包烧录与通用 fastboot 子集路径未上真机。

**界面与平台**

- 日志窗口、比较窗、设备信息表、烧录页行状态与文件选择热区的视觉布局未逐项目视；桌面键鼠交互、弹层与设置页 GC 区块未人工实测。
- 长会话下的 64 Mi 字符日志预算、单文件磁盘占用与磁盘写满路径未压测；真实 `%AppData%/UotanStudio/logs` 会话未跑完。
- 除 win-x64 外的 RID 发布未执行。riscv64 的混合交叉（官方 x64 SDK + 社区托管树 + qemu-user 仅模拟 ilc）已于 2026-11-14 在本机以 hello world NativeAOT 闭环验证（产物 riscv64 ELF、qemu 运行通过；`UotanStudio.Desktop` 全量发布与 SkiaSharp riscv64 原生库缺口未解）；loongarch64 同构方案（`loongarch-publish.yml`）与 riscv64 大工程交叉耗时未实测。release.yml 新增的 musl 两个 RID（alpine 容器发布）与 package.yml 的 MSIX/DMG/deb/rpm/apk/pacman 打包链均未在 runner 上运行。
- CI `dotnet test --solution` 路径与 Runner 机器未实测；Satori GC 的延迟与吞吐特性只在合成负载测量，应用混合负载与长期运行未验证。
- `CommTrace.Write` 的零分配只在类型层面成立，未做分配量基准。
