# 验证状态与边界

本文件记录当前可复现的验证结果与未验证边界。测试过滤器与环境门控见 `UotanStudio.Tests/AGENTS.md`。

## 门禁基线

| 平台 | 最近验证 | 结果 |
| --- | --- | --- |
| Windows x64，Debug / Release | 2026-11-16 | 全量 9562 用例，9532 通过，0 失败，30 跳过；两配置构建 0 警告 0 错误；format whitespace/style 均零退出码 |
| Windows x64，Debug（发射内核批次） | 2026-11-21 | 全量 6421 用例（测试集精简后），6415 通过，5 失败（`AirPlayReceiverSessionTests` 全部为 Windows 保留端口段撞上 `RtspPort=0` 临时端口的 bind WSAEACCES，环境问题非回归），1 跳过；format whitespace/style 均零退出码；AOT 发布通过且 `UotanStudio.Hash` 零新告警 |
| Linux ARM64，Debug | 2026-11-12 | 全量 9334 用例，9304 通过，0 失败，30 跳过；构建 0 警告 0 错误 |
| RISC-V（SpacemiT X60），Debug | 2026-11-12 | 全量 9334 用例，9287 通过，11 失败，36 跳过；失败均为平台环境缺口 |

- NativeAOT win-x64 发布在 2026-11-21 批次已可完成（此前记录的 `ilc` NullReferenceException 不再复现）；发布告警仅第三方 IL2104/IL3053 基线与 SATORI002，`UotanStudio.Hash` 未产生新的裁剪/AOT 警告。
- Linux ARM64 的 SIMD 内核在 AdvSIMD、SVE、SVE2 硬件上执行，`Vector<T>` 宽度为 128 位。
- RISC-V 当前 SDK 的 Roslyn 编译存在多核/R2R 冻结缺陷。测试环境使用 `DOTNET_ReadyToRun=0`、`DOTNET_PROCESSOR_COUNT=4`、`DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1`，并关闭构建期裁剪分析器与 Roslyn 分析器。11 个失败中 10 个疑与 SkiaSharp 相关；`skiasharp.nativeassets.linux` 自 3.119.0 起即含 `runtimes/linux-riscv64/native/libSkiaSharp.so`（4.157.0 要求 `GLIBC_2.38`），故这 10 个失败的根因需按新版依赖重跑复核。

## 真机验证覆盖

- LoongArch64/RiscV64 运行时发射向量内核（2026-11-21）：SpacemiT X60（OrangePi RV2，`rv64imafdcv_zbc`，VLEN=256）上以 .NET 11 社区 SDK（filipnavara/dotnet-riscv 11.0.100）运行 `SimdProbe`：`SimdCapabilities.Describe()` 报 `supported=Scalar|RiscVVector;active=RiscVVector;width=256;native=ready`、`hardwareAccelerated=True`；四个分派原语（Xor/XorConstant/SumBytes/XorFoldWords）在 32 个边界长度（0–300000）与强制回退开关下逐位等于标量预言。同机 C 探针（gcc 14.2，同一机器码）内核等价 PASS、memfd 双映射发射页执行一致、`getauxval(AT_HWCAP)` V 位与 vlenb=32 确认；发射机器码与 GNU as 汇编产物逐字节一致（sha256 `c343a389af030751`）。
- LoongArch64 侧（3A6000，Features 含 lsx lasx crc32）：**.NET 管线级验证通过**——`SimdCapabilities.Describe()` 报 `supported=Scalar|LoongArchLsx|LoongArchLasx;active=LoongArchLasx;width=32;native=ready`、`hardwareAccelerated=True`、四原语 dispatch 等价 PASS、CRC32C 查表路径正确。C 探针同字节验证 kernels=PASS。期间发现并修复三处内核缺陷：循环曾为 do-while 结构（长度 0 越界执行一轮）；`xvilvl/vilvh` 操作数顺序与假设相反（sum 结果恰为 `(真和 mod 256) << 24`）；fold 守卫单位误用字节（应为 16/8 词，尾块被丢弃）。机器码由真机 GNU as 与 Alpine 交叉 binutils 2.45 双重汇编逐字节一致；memfd 双映射发射页执行一致（fresh/热页/flush 三态 OK）。
- Windows 真机已覆盖 EDL 设备识别、WinUSB 接管与取消接管往返、ADB 与初始 USB 快照；取消接管后设备恢复 `qcwdfserial`，Problem 0。Windows 串口 `COMn` 回退未验证。
- Linux aarch64 真机已验证 EDL 串口回退：`qcserial` 绑定 9008 后经 `EdlSerialPortFallback` 找到 `/dev/ttyUSB0`，完成 Sahara、programmer 上传与 Firehose 初始化。Firehose `power reset` 在 ACK 后约 1.0 秒断开、约 39 秒重新枚举为 05C6:90db。
- SpacemiT 整包烧录已在 OrangePi RV2/K1 完成；勾选子集烧录、LE2110/ferrari 的 EDL 整包烧录与通用 fastboot 子集路径未上真机。
- 全志整机烧录、RKDL 真机维护命令、macOS/HarmonyOS USB 后端与部分 HDC 文件收发没有真机通过记录。
- 紫光展锐（Unisoc）深刷协议只有线格式与模拟传输证据：BootROM/FDL1/FDL2 阶段识别各线路、两级加载器上传、原生分区表、GPT 读取、分区读写擦、禁转义与 Chip UID 未上真机。帧校验和按大端 16 位字求和、折叠两次取反、奇数尾字节按原值累加，收发两个方向同值且无方向性字节交换；该字序由向量测试覆盖，未上真机。
- 紫光展锐刷写计划的选取范围已用真实 PAC 包核对：包内目录条目由 `type=1` 镜像、XML、FDL 与零长度操作条目组成；scheme 的 `Flag` 区分有负载的镜像步骤与无负载的操作步骤，scheme 选取的镜像集合与包内 `type=1` 条目集合相同，仅顺序不同，因此刷写计划按 scheme 声明顺序驱动，包目录顺序不参与。`flash_use` 在多数条目非零，不具选取力，未用于过滤。以上是文件级证据，写入设备仍未上真机。
- 上述方案的语义已逐条落到本仓库实现：`Flag` 按第 0 位掩码判断是否需要负载，无负载步骤是擦除或检查操作，不携带镜像；`CheckFlag` 暴露为 `SprdPacSchemeStep.IsMandatory`，表示全选也不能清除，默认仍全选。scheme 由产品名经 `/BMAConfig/ProductList/Product[@name]/SchemeName` 选取，产品名取自 PAC 头的 `szPrdName`；仓库按 `PacImage.ProductName` 精确匹配，产品名缺失或匹配不唯一时回退到第一个携带 FDL 角色的 scheme。`Flag=0` 的操作步骤中以 `EraseFlash` 开头的展开为擦除计划条目与界面行，`PhaseCheck` 等检查类步骤不列入；擦除行不参与全选，勾选时先弹确认框，执行时按分区名擦除并沿用 NV 分区保护。
- 紫光展锐厂商驱动包 `drivers/unisoc-spreadtrum`（13 个负载：`sprdadb`/`sprdvcom`/`sprdvmdm`/`rdavcom` 的 INF+CAT、`amd64__sprdvcom.sys`/`amd64__sprdvmdm.sys`/`amd64__rdavcom.sys`、`amd64__WdfCoInstaller01009.dll`、`amd64__winusbcoinstaller2.dll`）已随包，`compress-embedded-assets.cs --check` 报告 `manifest.tsv is up to date`。USB 树接线由 `UsbDriverInstallWiringTests` 覆盖（紫光下载口 `VID_1782&PID_4D00` 与 ADB 复合口 `VID_1782&PID_5A5B&MI_01` 经 `DriverPackageCatalog.FindMatch` 解析到该包并给出 `CanInstallDriver`，未匹配 ID、已绑定驱动、非 Windows 驱动信息三种情况保持隐藏）。本机无紫光实机，`sprdadb.inf`/`sprdvcom.inf` 的暂存与绑定未在真机验证。
- 紫光展锐主网格的列数据来自两个独立来源，均已用真实 PAC 核对：`Product/FlashTypeID`（该包为 `1`，按官方 XML 头注释即 Nand flash）提供存储介质；scheme 每个 `File` 的 `IDAlias`/`Type`/`Block@id`/`Block/Base`/`Block/Size` 提供文件标签、命令、分区、偏移与大小，与官方 `MainPage.cpp` 的 `m_lstProductInfo` 填充一致（`FileID`←`szIDAlias`、`Base`←`arrBlock[j].llBase`、`Size`←`llSize`、`FileType`←`szType`，列名见 `DLoader.rc:818` 的 `IDS_PRODUCT_INFO_COLUMN`）。PAC 目录条目的 `Addr`/`AddrNum` 在真实包中全为 `0x0`，因此偏移只能取自 scheme。该包实测：`FlashTypeID=1`，39 个 `File` 各恰有 1 个 `Block`，`Base` 除两个加载器外全为 `0x0`，`Block/Size` 仅 `ProdNV=0xA00000` 与 `PhaseCheck=0x100000` 非零。镜像字节仍从 PAC 流式读取，不做解压；文件列显示 `PAC:<包内文件名>`，用户可为单行改选外部文件，运行时只替换该行的镜像来源。界面列显示未做人工验证（本机无紫光实机，且该页依赖真实设备端点）。
- 紫光展锐主网格「偏移量」列按官方行为取分区名而不是字节偏移：`MainPage.cpp:611-617` 在 scheme 声明了分区表且 `Block@id` 非空时用 `szRepID` 覆盖 `llBase` 的显示，因为真实偏移由设备分区表决定、只有接入设备后才存在。真实包实测 39 个 `File` 中 `Block/Base` 仅两个加载器非零（`FDL=0x00005500`、`FDL2=0x9EFFFE00`），37 个为 `0x0`，而这些条目的 `Block@id` 全部有值，按官方规则代入后该列 39/39 行均有显示；PAC 目录条目的 `Addr` 全为 `0x0`，不参与。`Block/Size` 的 `0x0` 在官方列表里置空，故实现取 `Block/Size` 非零值、否则回退负载长度。设备侧真实分区偏移经 `UnisocFlasher.ResolvePartitionsAsync` 的 GPT/原生表解析，与本列显示无关。此后「读分区表」按钮在已加载 PAC 时不再整表替换，而是按 scheme 的 `Block@id` 与设备分区名匹配后只回填偏移列（大小列保留包内值），使偏移列显示设备真实字节偏移；未匹配的行保留 scheme 原值。匹配必须按分区名而非行名，因为负载行名是 scheme 标识（如 `BOOT`）而设备分区名为 `boot`。该回填要求设备已连 FDL2（`RequireStorage` 对非 FDL2 抛错），本机无紫光实机故未做端到端验证。 偏移量改为按包内分区表推导：XML `<Partitions>` 的 42 条 `Partition` 只有 `id` 与 `size`（注释写明 MB），无 offset 字段，官方 `PARTITION_T`（`BMAGlobal.h:145-151`）也只有 `szID`/`llSize`/`llGap`；官方把该表现场拼成 SPPT（`SettingsSheet.cpp:1193`，头 `"par:"`=0x3A726170、项 `"tbl:"`=0x3A6C6274）并以 `BSL_CMD_REPARTITION` 整表下发（`BootModeOpr.cpp:1138`），由设备重建后自行分配位置，因此 scheme 的 `Block/Base` 对镜像全为 0、官方界面用分区名覆盖该列。实现按声明顺序累加声明大小推导偏移，`0xFFFFFFFF`/`-1` 记为占满剩余空间且其后分区无可推导位置。真实包推导出 41 个有界分区合计 4,821,352,448 字节（4.49 GB，与 4 GiB 级 eMMC 相符）。该偏移仅用于界面显示，烧录仍按分区名寻址、不强制位置。重建逻辑本身未实现，未上真机。

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

## 已知不稳定项与门控

- 环境变量未设置时按门控跳过：`UOTAN_MAGISKBOOT_ORACLE`、`UOTAN_SELINUX_POLICY_SAMPLE`、`UOTAN_SAMSUNG_*`、`UOTAN_EROFS_CORPUS`、`UOTAN_HDC_MIRROR_DIRECT`、`UOTAN_DEVICE_SHELL_TARGET`、`UOTAN_QCN_SAMPLE`、`UOTAN_APK_CORPUS`、`UOTAN_HAP_SIGN_TOOL_JAR`；RKDL 真机与未随包的 scrcpy jar 同样跳过。
- `UOTAN_HAP_JAVA` 需指向真实存在的 java；缺失时按 `Assert.Skip` 跳过而非失败。
- `UOTAN_APK_CORPUS` 指向真实 APK 目录后 `ApkCorpusTests` 覆盖真实 APK 语料；ARSC 解析支持 AOSP `FLAG_WEAK`（0x0004）条目，smali 汇编器支持数组类型作为成员所有者，`Repair` 断言结构等价而非字节长度不变（真实 DEX 存在 map 之后的未映射尾部载荷）。
- HAP 官方差分用例对 `hap-sign-tool.jar` 无 `-permSign` 参数的构建先用 `-h` 探测能力，仅在支持时才追加该参数与权限子块断言；签名属性比较使用规范化属性列表而非逐字节比较（ECDSA DER 签名长度每次不同会带动外层长度变化）。
- `FileMagicDifferentialTests` 依赖 GNU `file`。`PATH` 指向 WinuxCmd 等替代实现时，XZ/Zstandard/bzip2/cpio 夹具会被报成 `data`/`ASCII text`，导致 4 例失败。
- `ErofsRebuildOptimizerTests`、`ErofsCaptureFidelityTests` 与 `PackingOptionPropagationTests` 共用进程级 `ErofsBuildDefaults.RebuildOptimization`，必须在 `[Collection("Workspace state")]` 中串行。`NodeTypeRegistry`/`PluginRegistry.Shared` 的写入方必须在 `[Collection("Node type registry")]` 中串行。
- `HdcServerTransferTests` 在并行负载下出现并发计时抖动，单类复跑通过；全量失败不能直接归为协议缺陷。
- EROFS 用例断言 inode 形态、advise 位或镜像字节时必须固定 `BuildTime` 与源 mtime。EXT4 默认日志超级块 UUID 随机，默认参数下不保证逐字节相同。
- 测试宿主输出目录必须留在仓库内；测试临时文件必须写入 `%TEMP%` 并在结束后删除。
- 架构门存在已知缺口：`FormatProbeBoundaryGuardTests` 只匹配字面拼写；`ContainerBoundaryGuardTests.AllowedPaths` 为空；分层门未覆盖 `Views/Editors/ScreenMirrorPixelBuffer.cs` 对 `UotanStudio.Media.Decoding` 的导入；`ProjectReferenceDirectionGuardTests` 跳过 `UotanStudio.Tests`。
- 终端包装命令拦截：会话的 ESC/CSI/OSC 状态机已实现（协议序列原样转发、不进镜像、不置失步；仅方向键、Home/End/Delete、Tab 等行编辑序列交还 shell），包装命令定向测试 71/71 通过。真实 GUI 人工键盘输入的端到端复核未执行，属未验证边界。

## 未验证边界

**固件与文件系统**

- 仓库内没有真实 IMAGEWTY 镜像；夹具按参考结构合成。真实 V1 镜像占比、`original_length` 语义与加密容器密钥未实测。`checksum` 只报告不校验，Twofish 解密未实现。
- 多片 NAND/NOR 整片擦除、eMMC `ERASE_LBA` 分支、SD/UFS/PCIE/SATA 按地址写入与擦除未上机；eMMC 整盘写入后的启动未验证。
- `prm` 命令没有实现，无法执行格式对照。

**哈希与 SIMD 发射内核**

- LoongArch `crcc.w.*.w` 指令族与 crc32c_le 寄存器约定不匹配（16 个 (seed, byte) 采样点与正常域/反射域共 32 种假设均不符），LoongArch64 上 CRC32C 维持查表路径；指令语义反推未完成，属开放项。
- RiscV64 的 CRC32C Zbc clmul 折叠未实现（反射域寄存器与多项式消息的映射推导未完成），维持 slice-by-8。

**设备与协议**

- 没有全志设备：FEL/FES 握手、DRAM 轮询、组件槽位上传与整机烧录未上机；`Flash` tag、4 字节 flash 类型内联负载、稀疏分区写入、`(Normal, action)` 工具模式与 NAND/SPI NAND boot0 查询待真机确认。
- 没有展锐下载设备：BSL 探测（CHECK_BAUD 单字节/CRC16 修正线/静默重试线）、FDL 上传 528 分块、原生表 0x4C 记录与 64 位长度布局（80/88 字节）、原生表容量单位、USB 直连批量路径与 512 字节端点 ZLP 特例、macOS 串口形态均未实测；`1782:4D00` 之外的产品 id 未纳入发现；`UOTAN_UNISOC_DL_DEVICE` 门控的实机用例缺省跳过。
- RKDL `WIPE`、子码返回值、坏块位图与 SDRAM 重枚举只有线格式证据；maskrom→usbplug 端到端与无 loader 烧录未复测；`bcdUSB` 阶段证据未在真机观测。
- SPINOR/SPINAND/SATA/PCIE/UFS/RVD 的存储切换只有 eMMC/SD 可对照官方工具；READ_STORAGE 多位置位语义未观测。
- Windows 未覆盖 alt setting 切换与 Legacy/Rockusb 分支；Linux、libusb、macOS、HarmonyOS 后端只有编译与静态核对；macOS IOKit 与 HarmonyOS DDK 路径未上机。
- NativeBin 启动自动下载链路（`NativeBinProvisioner.EnsureForCurrentProcessAsync` + 归档提取注册）只有单元测试覆盖；真实网络下载、镜像回退与提取后的 libusb 后端启用未实测。
- HDC 文件收发、ADB 通道逐帧 trace 与调试模式未用真实设备执行；HDC 缓存复用未在 HarmonyOS 真机跑完第二轮；运行中拔插设备只经代码路径验证。

**界面与平台**

- 日志窗口、比较窗、设备信息表、烧录页行状态与文件选择热区的视觉布局未逐项目视；桌面键鼠交互、弹层与设置页 GC 区块未人工实测。
- 长会话下的 64 Mi 字符日志预算、单文件磁盘占用与磁盘写满路径未压测；真实 `%AppData%/UotanStudio/logs` 会话未跑完。
- MSIX 打包、macOS .app 打包与打包态安装冒烟未验证；AppPaths 首跑迁移仅经单元测试覆盖，真实升级路径未实测。HAP 签名与 AGC 编排未经真实账号流量验证；真机装机链路未验证（交接文档 `HAP_SIGNING_HANDOFF.md`）。
- 除 win-x64 外的 RID 发布未在宿主全量上执行：riscv64 cross JIT 方案（官方 x64 `ILCompiler` 宿主包 + 自建 `libclrjit_unix_riscv64_x64.so`）尚未在 runner 上端到端跑通；`loongarch-publish.yml` 的 qemu 方案未执行过。release.yml 的 musl 两个 RID（alpine 容器发布）与 package.yml 的 MSIX/DMG/deb/rpm/apk/pacman 打包链均未在 runner 上运行。
- CI `dotnet test --solution` 路径与 Runner 机器未实测；Satori GC 的延迟与吞吐特性只在合成负载测量，应用混合负载与长期运行未验证。
- `CommTrace.Write` 的零分配只在类型层面成立，未做分配量基准。
