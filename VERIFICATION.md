# 验证状态与边界

本文件只记录当前状态：一条测量配一个日期，每跑完一道门禁就更新"门禁当前实测"。常驻约束与命令见根 `AGENTS.md`，测试过滤器与环境门控见 `UotanStudio.Tests/AGENTS.md`。

## 门禁当前实测

- 全量单轮（Release，同一 worktree 直接执行测试宿主，机器另有并行负载，时长波动）：8402 用例/14 跳过/0 失败，两次分别 105.2s 与 103.3s（2026-11-03）。
- 全量三轮连跑：8401 用例/14 跳过/0 失败，102.2–103.2s（2026-11-03，Release）。
- 全量门禁（2026-11-04，Debug）：构建 0 警告 0 错误 → format `whitespace`/`style` 双门零报告 → `dotnet test` 8658 用例/0 失败/25 跳过/1m51s；NativeAOT win-x64 发布成功，ILC 警告仍只来自第三方程序集（Avalonia XAML trampoline、Dock、YamlDotNet、Common.Logging、LTRData.Extensions），无本次改动引入的裁剪/AOT 警告。
- 烧录包多布局适配（2026-11-04，Debug）：全量构建 0 警告 0 错误 → format `whitespace`/`style` 全解零报告 → 全量 8655 用例/19 跳过/1 失败（`RemoteRomServiceTests.DownloadAsync_UsesBoundedParallelRangesAndBrowserHeaders` 的并发计时在并行构建负载下抖动，单跑该类 7/7）→ 架构门 23/23；布局用例 `FlashPackageLayoutCorpusTests` 6/6、`FlashBinPackageTests` 6/6、`FlashSpacemiTPackageTests` 4/4、`FlashAfterSalesPackageTests` 5/5；NativeAOT win-x64 发布成功（exe 约 172.9 MB，publish 目录 306 MB），IL 警告 38 条全部来自第三方（Avalonia/Dock/Ursa/Semi/YamlDotNet/Common.Logging/LTRData），本仓库程序集 0 条。语料门控 `UOTAN_FLASH_PACKAGE_CORPUS=D:\Firmware` 覆盖 Bianbu K1/K3、orangepirv2、ferrari、LE2110、RMX3121 六个真实包。
- 分层与架构门：`UotanStudio.Tests.Architecture` 23/23（2026-11-03，Debug），Release 分层 18 项 1.3s。
- 本地化键集门：`LocaleDictionaryTests` 6/6（生成索引、双语键集合与 resx 一致）。
- 烧录页日志与设备信息的本地化补全（2026-11-06，Debug）：`FlashLogText`、`FastbootDeviceInfoService`、`FlashViewModel` 与 `FlashService`/`BackupService` 引用的 159 个 `Flash_*` 键此前未写入 `Locale/Common.resx` 及其 `zh-CN` 变体，界面因此直接显示键名（`Flash_SpacemiTPackageLoaded`、`Flash_SpacemiTBootChainRunning`、设备信息栏的 `Flash_FastbootInfo_Stage` 等）。补入后中英键集与 `LocaleKeySets.Common` 三方一致（resx 3893 键、索引 3893 项、双向零差异），代码中已无无法解析的 `Flash_*` 键。`LocaleDictionaryTests` 6/6、`UotanStudio.Tests.Flash` 171/171、全量 9018 用例 0 失败（44 跳过）、构建 0 警告 0 错误、format `whitespace` 零报告。界面观感未人工走查。
- 固件与协议门：`UotanStudio.Tests.Firmware` 4516 用例/4503 通过/13 跳过、`UotanStudio.Tests.Protocols` 408/408、`AllwinnerImageWtyTests` 26/26、`AllwinnerRc6Tests` 8/8（预言为参考实现的 6 条 RC6 官方向量，逐字节一致）、`SunxiEfexProtocolTests` 50/50、`SunxiFlashTests` 28/28（2026-11-05：`AWUSBFEX` 身份、阶段头、擦除标志、存储/安全查询、DRAM 0/1/>1 轮询与超时、DTB/sys_config/board_config 槽位、U-Boot work mode、FES 切换重试、MBR/`sys_partition` 映射、稀疏展开、Flash tag、flash on/off、boot 变体选择与 CRC 策略）；`SunxiDiscoveryTests` 6/6；全志整机烧录尚未上真机，`SetFlash`/`FlashSwitch` 的内联类型字形状与工具模式 `(Normal, action)` 的固件行为仍待真机确认。
- 全量门禁（2026-11-05，Debug）：构建 0 警告 0 错误 → format `whitespace`/`style` 双门零报告 → `dotnet test --solution` 8823 用例/8796 通过/27 跳过/0 失败/1m18s（同条件另一次运行出现 1 个并发计时瞬时失败，立即复跑全绿）；NativeAOT win-x64 发布成功，ILC 警告仍只来自第三方程序集（Avalonia XAML trampoline、Dock、YamlDotNet、Common.Logging、LTRData.Extensions），无全志代码引入的裁剪/AOT 警告。
- 全量四轮连跑（2026-11-05，Debug）：增量构建 0 警告 0 错误后以 `--no-build` 连跑四轮，均 8918 用例/8874 通过/44 跳过/0 失败，耗时 1m23s、1m24s、1m35s、1m41s。此前一轮全量暴露的 9 例 `EdlSerialBundleServiceTests`、1 例 `FlashControlGateTests` 与随后复现的 2 例 `SunxiFlashTests` 已修复，修复点见“已知不稳定用例与环境门控”。
- NativeAOT win-x64（2026-11-05，Release）：发布成功，exe 167.5 MB；`warning` 共 12 条，全部来自第三方程序集（Avalonia DataGrid、Dock、Ursa、Semi、YamlDotNet、Common.Logging、LTRData.Extensions）与 SDK 的 `Satori GC is experimental` 提示，本仓库程序集 0 条，无新增裁剪/AOT 告警。
- format 双门：`whitespace` 与 `style` 全解 `--verify-no-changes` 零报告（2026-11-03）。
- NativeAOT win-x64（2026-11-03）：发布成功，产物约 172.3 MB；ILC 警告 38–39 条全部来自第三方（Avalonia XAML trampoline/Dock、YamlDotNet、Common.Logging、LTRData.Extensions、Makaretu.Dns 的 Tmds.LibC 提示）与 SDK 预期的 `Satori GC is experimental` 提示。
- `LNK1181 无法打开输入文件"Synchronization.lib"`：未设置 `LIB`（MSVC 与 Windows SDK 的 `lib\x64` 路径）时原生链接失败，属环境事实，非代码或裁剪问题。
- Satori GC：`riscv64`/`loongarch64` 显式 `UseSatoriGC=false` 回落默认 GC；framework-dependent 运行 `GC.GetConfigurationVariables()` 不含 `SatoriGC`（32 变量），自包含发布产物含该键（33 变量），Satori GC 仅存在于自包含发布。
- 完整门禁序列（restore → 构建 → format 双门 → `dotnet test --solution`（Debug）→ AOT 发布（Release））本机一次跑通：8224 用例/8202 通过/22 跳过（全部环境门控）/0 失败，1m38s（2026-11-01）。

## 性能实测

测试套分层（2026-11-03，Release，同一 worktree 直接执行测试宿主）：

- `UotanStudio.Tests.Firmware*` 4595 项 35.2–38.0s；`UotanStudio.Tests.Services*` 1972 项 52.1–81.5s（属性缓存未启用的一次测得 130.8s）；`UotanStudio.Tests.Protocols*` 291 项 3.3s（`FirehoseResponseBomTests` 在脚本界限未压低时合计 94.1s）；`UotanStudio.Tests.Architecture*` 18 项 1.3s；`UotanStudio.Tests.Compare*` 63 项 0.6s。
- 慢点：`FirehoseResponseBomTests` 三个 `PowerOff_*` 各 30.0s（`IsConnected` 恒为 true 时断开观察循环把 `PowerResetVerifyTimeoutMs` 的 30s 预算整段等满）、5 个 `Configure_*` 各 1s（`ConfigureRetryIntervalMs`）；脚本配置把这两个界限压到 50ms 与 0，响应序列与断言不变。
- MCP 目录形态用例每次重建整张工具表约 4.1s（三个类共 10 次），`McpToolCatalogCache` 缓存后按刷写门控各构建一次；对 5521 个用例的方法体去重扫描未发现逐字相同或同类近重复用例，冗余在构建不在用例。

设备链路（真机）：

- eMMC 整盘写入 8,791,261,184 B 耗时 5 min 28 s（约 27 MB/s，单次测量，未区分 USB 链路与 eMMC 瓶颈），回读前 1 MiB 一致（2026-11-03，RK3588 usbplug）。
- SPINOR 写入 `rkspi_loader.img`（4 MiB，LBA 0）全量回读逐字节一致，用例 35s（2026-11-03）。
- SPINOR 末块（LBA 0x7F80、128 扇区）经 `EraseRange` 后回读全 0xFF，LBA 0 的 loader 镜像首字节未变；`WaitForLoaderReadyAsync` 同板 6.6s 内就绪（2026-11-03）。
- SPI NOR 上整段 `ERASE_LBA`（8192 扇区）被设备以 CSW status 1 拒绝，bulk 管道随后进入总线级僵死（后续传输 0ms 内以 Win32 中止失败，须物理重插拔）；按块 `ERASE_FORCE` 一次通过。对已在运行 loader 的 usbplug 端点发 `0x0471` 以 `ERROR_SEM_TIMEOUT` 失败，由阶段门控挡住（2026-11-03）。
- RKDL loader 下载 `Win32 121`/CRC 错误定位为 USB 总线级僵死：CBW bulk 写 0ms 报 Win32 23（CRC 错误），rockusb.sys 内部 5 秒控制超时表现为 Win32 121，管道复位与 PnP 重枚举无效，须物理重新插拔；协议帧、CRC-16 分块与 rockusb.sys vendor IOCTL 经反汇编与官方逐项一致（2026-10-31，对齐复核 2026-11-01）。
- HDC 详情 11 条查询合并同一直连会话后 8106ms → 约 1215ms（2026-11-03，HarmonyOS 真机）。
- ADB 详情 9 条查询合并为一次带标记 shell（八段齐全才采信，否则回退逐条）+ 一次无参 `getprop` 整表；真机实测 getprop 2770ms/13,616 B、8 段合并 1047ms、按稳定键开租约 51ms、CNXN 握手 39ms（2026-11-03，sdm6150/Android 9/kernel 4.14.83）。
- ADB 静态属性缓存（键为物理身份，brand/model/codename/kernel/bl 齐全才写入）：命中后完整读取 3829ms → 650ms（-83%），13 个静态键与完整读取逐字相同；缓存跨 adb/fastboot 模式共享，端点移除时失效，手动刷新只重置 USB 枚举缓存（2026-11-03）。
- Windows USB 扫描全量 32–34ms、增量 13–18ms，端点 48 且两次增量之间集合逐项相同；281 个在位节点中 47 个来自 `USB` 枚举器，节点遍历保持全 USB 节点不收窄；fastboot 序列号在扫描内只探测一次（2026-11-03）。
- ADB server 路由：僵尸监听行（`OwningProcess 0`）存在时 `IsListening` 首次约 2s 内判定 False、之后 0–1ms，`AdbServerOwns` 0–1ms；`host:version` 60s 内复用，一次目标枚举只连一次 server（2026-11-03）。
- USB 探针（Qualcomm ADB 真机，接口 MI_05）：`interfaceNumber=5`、class FF/42/01、`resetReenumerates=False`；ADB 握手后 `echo` 返回 `__PROBE_OK__`；`WatchUsb(fireInitialSnapshot: true)` 5s 内投递 48 条初始 Added（2026-11-03）。
- 日志定容预算（`MaxQueueCharacters` 默认 64 Mi 字符、`MaxQueueSize` 默认 250,000 条，超限从队首淘汰，调试模式跳过预算）与默认落盘（`%AppData%/UotanStudio/logs` 的 JSON Lines 单会话文件，不滚动）经 `LogServiceRetentionTests`/`LogViewModelRetentionTests`/`LogFileSinkTests`/`LogServiceFileLoggingTests` 验证（2026-11-03）。

## 已知不稳定用例与环境门控

- 环境门控跳过：`UOTAN_MAGISKBOOT_ORACLE`、`UOTAN_SELINUX_POLICY_SAMPLE`、`UOTAN_SAMSUNG_*`、`UOTAN_EROFS_CORPUS`、`UOTAN_HDC_MIRROR_DIRECT`、`UOTAN_DEVICE_SHELL_TARGET`、`UOTAN_QCN_SAMPLE` 等未设置即跳过；RKDL 真机用例默认跳过；`UotanStudio.Tests.Services` 的 1 条跳过是未随包的 scrcpy jar。
- 已加固：`AdbTlsTransportTests.UpgradeToTls_*` 原为 15s 握手超时，机器高负载下 TLS 握手抖动即 EOF 误判；超时预算改到 120s，握手逻辑与断言未变（2026-11-05）。
- 已加固：`RemoteRomServiceTests.DownloadAsync_UsesBoundedParallelRangesAndBrowserHeaders` 原用 `Task.Delay` 碰运气制造分块重叠，高负载下 `MaximumConcurrentRequests > 1` 失手；改为分块请求显式等待第二个请求到达（`TaskCompletionSource` + 500ms 上界）后才放行，不再依赖调度时序（2026-11-05）。
- 已加固：`SunxiFlashTests.Flasher_Execute_*` 曾在全量运行中 `Assert.Contains` 失配，根因是 `Progress<T>` 的回调经线程池异步投递、测试侧用非线程安全 `List` 收集；改用加锁收集器（`System.Threading.Lock`）后连续多轮不再复现（2026-11-05）。
- 进程级静态状态改为集合串行：`ErofsRebuildOptimizerTests` 会写 `ErofsBuildDefaults.RebuildOptimization`，与 `ErofsCaptureFidelityTests`、`PackingOptionPropagationTests` 同属 `[Collection("Workspace state")]`；`NodeTypeRegistry`/`PluginRegistry.Shared` 的写入方（`PatchGateTests`、`ScriptNodeViewModelTests`、`NodeFactoryRegistrarTests`）同属 `[Collection("Node type registry")]`（2026-11-05）。
- `UsbSerialCacheTests` 以与生产一致的 `Environment.TickCount64` 作时间基准（实例字段，断言全部为相对偏移），静态缓存不与任何真实设备枚举交互，CI（无可枚举 USB 总线）与本机行为一致。
- `FlashGlobalLogRoutingTests.ImportRkConfigAsync_*` 的 %TEMP% 文件锁时序按删除前后各 `await SpecialFileRowCheck` 处理；"校验期间用户删不掉镜像"的生产侧风险保留。
- 部分未闭合：`ErofsCaptureFidelityTests` 与 `PackingOptionPropagationTests` 曾在整套并行运行中偶发失败（`Capture_SingleLinkedFiles_RecordTheirOwnFormatWord` 期望 2 实得 1、`Pack_PreservedErofsWithDotOmittedEnabledKey_Succeeds`），单独跑该类或 `UotanStudio.Tests.Firmware*` 均绿。已确认的耦合方是进程级静态开关 `ErofsBuildDefaults.RebuildOptimization`（`ErofsRebuildOptimizerTests` 写入），三者已并入 `[Collection("Workspace state")]` 串行；串行化属约束而非根因消除——`Application.Current` 为 null 时的懒加载路径与 `BuildTime` 未固定的构建是否另有贡献仍未逐一证实。
- 门控缺口（架构门已知不严）：`FormatProbeBoundaryGuardTests` 只匹配字面拼写，保护性 MBR 签名的十进制/下划线写法可绕过；`ContainerBoundaryGuardTests` 的 `AllowedPaths` 恒为空，是死代码；分层门只覆盖应用服务命名空间（`Views/Editors/ScreenMirrorPixelBuffer.cs` 直接导入 `UotanStudio.Media.Decoding` 不在射程）；`ProjectReferenceDirectionGuardTests` 整体跳过 `UotanStudio.Tests`。
- 复核保留的时序上界：`AdbRemoteProtocolTests` 的 5s、`FifoGateTests`/`PayloadStreamingTests` 的 5–10s、`UsbmuxdClientTests` 的 2s accept 等待，均为宽松上界，压测未见失败。
- 镜像构建确定性：断言 inode 形态/advise 位/镜像字节的 EROFS 用例显式固定 `BuildTime`，布局不随墙钟变化；EXT4/F2FS/FAT 的时间字段取自固定常量与源元数据，同一源树重复构建字节相同；EXT4 默认开启日志，日志超级块 UUID 每次构建随机，默认参数下两次构建不逐字节相同。测试宿主不创建 Avalonia 应用，Compare 树懒加载在 `Application.Current` 为 null 时就地执行集合改动。

## 未验证边界

**固件格式与文件系统**

- 仓库内没有真实 IMAGEWTY 镜像，夹具按参考结构合成；真实固件的 V1 镜像在野比例、`original_length` 语义与加密容器实际密钥是否为参考常量没有实测。`original_length` 小于 `stored_length` 时按参考截断，等于 0 时保留负载（参考实现截成空文件）。
- IMAGEWTY 的 `checksum` 字段只报告不校验（两份参考均未定义其覆盖范围与算法）；Twofish 解密未实现（参考读取路径没有调用点）。
- 多片选 NAND/NOR 的整片擦除未在双片设备上核对（块数推导只在"上报值即单片容量"时与官方一致）。
- eMMC 的 ERASE_LBA 擦除分支未在真机执行；SD、UFS、PCIE、SATA 的按地址写入与擦除未上机。
- `prm` 命令在参考实现中只有 usage 文本、没有实现，无法逐行对照。
- eMMC 整盘镜像写入后的启动未验证（无显示器与串口，仅确认回读一致）。

**设备协议与真机**

- 没有全志设备：FEL/FES 握手时序、DRAM 轮询、组件槽位上传与 FES 整机烧录未上机。
- 全志已接入发现、会话工厂、页面与烧录计划；仍待真机确认的边界：FES 服务对 `Flash` tag 与 4 字节 flash 类型内联负载的接受度、稀疏分区展开后写入的介质等价性、`(Normal, action)` 工具模式的固件行为、NAND/SPI NAND boot0 大小查询与直读的实际应答。
- RKDL 维护与开发者命令（`WIPE` 门、子码返回值、坏块位图内容、SDRAM 执行后重枚举）只有线格式与官方二进制证据，未上板。
- maskrom→usbplug 阶段判定与无 loader 烧录的真机链路、发送引导后的重枚举重捕获端到端待复测。
- `bcdUSB` bit 0 阶段证据路径未在真机观测到（rockusb.sys 端点通常无描述符，实际生效的是 `ROCKCHIP` 序列号证据）。
- SPINOR/SPINAND/SATA/PCIE/UFS/RVD 六介质的存储切换只有 eMMC/SD 可对照官方工具；READ_STORAGE 掩码多位置位（如 0x0003）的设备语义未观测。
- Linux、libusb、macOS、HarmonyOS USB 后端没有运行环境，只有编译与静态核对；并发 URB 路由、复位后重新申请接口、macOS ObjC 选择器与 IOKit 路径需对应平台真机确认。
- Windows 真机覆盖枚举、WinUSB 打开、ADB 与初始快照；alt setting 切换与 Legacy/Rockusb 分支未覆盖。
- HDC 文件收发、ADB 通道打开与调试模式逐帧 trace 未用真实设备执行（只验证字节等价与日志分派）；HDC 缓存复用分支未在 HarmonyOS 真机跑第二轮。
- "拿到完整信息前拔出设备"只在代码路径与既有取消机制上闭合，未做真机拔插实测。
- RKDL 存储回填的真实介质几何与过滤结果未上板核对。

**界面观感**

- 日志窗口观感、滚动与导出保存对话框未目视；运行中从日志查看器打开活动日志文件（读取器与写入器共享模式冲突）未验证。
- 64 Mi 字符预算与调试模式无限保留在真实长会话下的内存占用未压测；单文件不滚动的磁盘占用与磁盘写满后的停用路径未验证。
- 默认文件落盘只在 %TEMP% 单元测试验证，未在真实 `%AppData%/UotanStudio/logs` 下跑完整会话。
- 比较窗单侧淡色背景、目录树展开、设备列删除/重命名/推拉与 RKDL 设备信息表格、存储下拉框观感未目视。
- 烧录页行内"选择文件"链接热区、行状态文字在表格中的换行与截断未目视。
- 烧录页门控改为“只禁用不隐藏”（2026-11-05，Debug）：`FlashControlGate` 移除可见性条件，切换设备只展开对应协议分类、不再收起其它分类；未选中设备时保留已持久化的展开状态；Root 选项组、AVB 选项组、自定义包入口与版本列表改为各自置灰常驻。`UotanStudio.Tests.Flash` 128/128（含新增“页面不隐藏任何控件”的源码门）通过；XAML 观感（分组常驻后的页面长度、置灰对比度、各协议分类并排展开的观感）未人工走查。
- 烧录页"选择文件夹"对六个真实包（Bianbu K1/K3、orangepirv2、ferrari、LE2110、RMX3121）的真实对话框选择、XAML 列渲染（LUN/偏移/大小）、勾选与 `ExtractSelected` 落盘未人工走查；仅以临时视图模型级探针（已删除）验证了目录分发、行构建、SpacemiT 按钮启用意图与 MTK ofp 条目映射。
- SpacemiT 整包烧录已在真机（OrangePi RV2 / K1，USB `361C:1001`）完成：FSBL 暂存 → `continue` → U-Boot `fastboot` → `u-boot.itb` 暂存 → `continue` → GPT 分区表 → 全部分区写入（含 1.47 GB 镜像的分块 gzip），脚本以 `done` 结束。勾选子集烧录、LE2110/ferrari 的 EDL 整包烧录仍未上真机；SpacemiT 行的通用 fastboot 路径仍会把勾选行当作 `flash <name> <file>` 发出。
- SpacemiT 的 BootROM FDL 与 U-Boot 两阶段区分（2026-11-06，Debug）：由描述符序列号 `DFU-DEVICE` 判 FDL、厂商 ID 对配合其它可用序列号判 U-Boot，实机会话上以 `getvar:version-brom` 定论；`SpacemiTBootStageTests` 12/12、`SpacemiTBootStageDisplayTests` 6/6、`UotanStudio.Tests.Devices` 508/508（17 跳过）通过。真机上的徽章显示（`SpacemiT · FDL` → `SpacemiT · U-Boot` 切换）未人工走查：测试设备已移除。
- SpacemiT 烧录拆成两步并删除其二级菜单（2026-11-06，Debug）：通用「发送引导程序」只执行包脚本最后一个 `continue` 之前的引导链段落（不写存储，因此不需要 `FLASH` 确认），通用「烧录选中」只执行其后的存储动作（`stage`/`continue` 一律不尝试，且设备仍在 BootROM FDL 时在写分区表之前拒绝）；进迭时空分类、专属包选择与烧录按钮已删除，包改由通用「选择文件夹」加载，U-Boot 阶段通用烧录按钮自动可用。`SpacemiTFlashTests` 61/61（含 4 个范围用例）、`UotanStudio.Tests.Flash` 132/132、全量 8943 用例 0 失败（43 跳过）。该页面的按钮位置与两步操作观感未人工走查：无测试设备且未开界面。
- SpacemiT 烧录行的存储介质改为按包内布局表推导（2026-11-06，Debug）：`mtd` 表（SPI NOR）与 `gpt` 表（块设备）都参与，同一分区被两张表收录时合并为一行并标注 `GPT + SPI`，只有一张表收录时为 `GPT` 或 `SPI`，无表证据时留空；厂商的 `partition_flash.json` 因每分区多镜像（含正则标记）被识别为“全量刷写描述表”而不作为布局。真实语料结果：Bianbu K1 的 bootinfo/fsbl/env/opensbi/uboot 为 `GPT + SPI`、bootfs/rootfs 为 `GPT`；Bianbu K3 的 env 等 6 项为 `GPT + SPI`、ESP/bootfs/rootfs 为 `GPT`；orangepirv2 全部为 `GPT`（该包无 mtd 表）。`FlashSpacemiTPackageTests` 6/6、语料门控 `UOTAN_FLASH_PACKAGE_CORPUS=D:\Firmware` 6/6、`UotanStudio.Tests.Flash` 131/131、`UotanStudio.Tests.Services` 2059/2059（1 跳过）。烧录页介质列的目视观感未人工走查。
- SpacemiT 烧录页的介质列、门控、日志与进度（2026-11-06，Debug）：介质列改为真实介质 `eMMC/SD`（`gpt` 块设备布局，包不指明是 eMMC 还是 SD）、`SPI NOR`（`mtd` 布局）及两者并列，不再显示表格式 `GPT`；偏移量为 0 的行显示 `0` 而不是空白。「发送引导程序」只在 FDL/阶段未知时可用、U-Boot 下关闭，「烧录选中」在 FDL 下保持关闭而形成两步流程；两者的启用意图改由页面在每次换设备与退出运行时重新声明，协议分支不再可能把它留在关闭状态；「烧录选中」在未勾选任何行时只给出提示而不烧写整包。运行日志只保留开始/完成/提示与错误，逐动作与百分比行全部移除；gzip 分块改为按压缩负载映射回原始区间上报字节进度，分区行与总进度条据此推进，未知总长的传输显示为不确定态。`SpacemiTFlashTests` 62/62（新增 gzip 进度用例）、`Tests.Flash` 132/132、`Tests.Services` 2059/2059（1 跳过）、全量 8944 用例 0 失败（43 跳过）。界面观感与真机两步操作未走查：测试设备已移除。
- 烧录页设备状态按设备缓存并在切换后刷新（2026-11-06，Debug）：`DeviceStatus` 的行按设备键缓存在 `_deviceStatusCache` 中，`ReplaceDeviceStatus(deviceKey, rows)` 发布的同时写入缓存（空结果删除该键，失败的读取不留行）；选择变化时 `RestoreDeviceStatus(currentDevice?.Key)` 换成该设备的行或空态，并 `BeginDeviceStatusRefresh()` 让仍在进行中的读取失效，避免它的结果落进新设备的表格。缓存只恢复已发布的读取，切换设备**不会**自动打开会话（唯一例外是原本就会在选中时自行查询的全志端点）。测试：`FlashFastbootDeviceStatusTests` 9/9（切走切回恢复、未读设备空态、在途读取失效、空结果清除缓存）；全量 8987 用例 0 失败（43 跳过，一次并行负载下 `AirPlayReceiverSessionTests` 的端口占用抖动，单跑 7/7）、format 零报告。真实切换设备的观感未走查：宿主进程在运行、输出目录被锁，本轮只构建了库与测试工程。
- fastboot 设备信息读取按分支拆分并填入设备信息表（2026-11-06，Debug）：新增 `FastbootDeviceInfoService`——通用 gadget 走标准分支（`getvar all` → `partition-size:*`），进迭时空 gadget 先做实时 `getvar:version-brom` 探测再分流；BootROM FDL 分支逐个询问 `version-brom`/`mtd-size`/`blk-size`/`product`/`serialno`（该阶段不实现 `getvar all`，也没有分区表），U-Boot 分支读变量转储并挑出 `version`/`product`/`bootloader`/`serialno`/存储容量与分区表。`getvar all` 被拒绝（真机报 `fastboot Fail: Variable not implemented`）不再让整次读取失败：改为逐变量探测，能答多少报多少，全无应答才重抛设备自身的错误。分支判据以线路为准：应答 `version-brom` 即为 FDL，无论 vid/pid 是否被识别。读取结果填进页面「设备状态」表（`FlashDeviceStatusRowKeys.Fastboot`，先写启动阶段再逐项事实），不再打印逐项日志；`Flash_FastbootInfo_FdlHint` 键与「此阶段没有分区表……」提示已删除，FDL 下也不再输出“0 个分区”。「读取设备信息」控件门控由 `SharedStorage` 改为新增的 `FlashControlGate.InfoRead`，使该按钮在 fastboot 上真正可用；ADB、HDC 与已证实 mask ROM 的瑞芯微端点仍关闭。`FastbootDeviceInfoServiceTests` 11/11、`Tests.Flash` 160/160、`LocaleDictionaryTests` 6/6。真机两个分支的实际应答与表格观感未走查。
- 「发送引导程序」的总进度条改为整段滚动（2026-11-06，Debug）：引导链段落只把 FSBL 与 U-Boot 暂存进内存并两次 `continue` 移交，两次传输没有共同分母，此前会显示一个在每个暂存文件上从零重来的百分比。现在该段落把 `IsOverallIndeterminate` 置于 `true`（两条总进度条都绑定它）并在整段保持，说明文本固定为 `Flash_SpacemiTBootChainRunning`，不再写入传输层的英文诊断行；失败或被取消时在 `finally` 停止滚动并复位，成功时照旧落到 100%。烧录段落仍按字节推进，行为不变。新增 `FlashSpacemiTProgressTests`（4 例，覆盖滚动段落的字节/无总分/阶段行上报与烧录段落仍按字节推进）；`Tests.Flash` 154/154、全量 8966 用例 0 失败（43 跳过）、构建与 format `whitespace`/`style` 零报告。滚动动画与两条进度条的观感未人工走查：宿主进程正在运行、输出目录被锁，本轮只构建了库与测试工程。
- SpacemiT 烧录行的介质只对“真的从包内文件写入”的行推导（2026-11-06，Debug）：`SpacemiTPackagePartition.StorageMedium` 现在要求条目给出具体镜像引用，分区表只列名称与位置而不给镜像的条目、以及只带厂商正则标记的条目都不再上报介质（这类内容由厂商工具在内存中生成，运行不写入存储；同一行本就因此不报命令与大小）。引用了镜像但磁盘缺失的行仍保留布局表给出的介质，因为它仍占有该位置、用户可补文件。真实语料核对：orangepirv2 的 `misc` 介质由 `eMMC/SD` 变为空，Bianbu K1/K3 各行不变；`FlashSpacemiTPackageTests` 6/6、语料门控 `UOTAN_FLASH_PACKAGE_CORPUS=D:\Firmware` 6/6、`Tests.Services` 2059/2059（语料开启时 1 跳过）、全量 8962 用例 0 失败（43 跳过）、构建与 format `whitespace`/`style` 零报告。
- SpacemiT 烧录页的行回填与选项门控（2026-11-06，Debug）：选包后的行按包内布局表回填分区名、真实介质、偏移与镜像名（镜像名取表格自身拼写，磁盘缺失也不改名），只列包内确有镜像的分区才写 `flash`，无镜像的行命令与大小都留空；脚本行显示包/脚本里的原始命令（`erase`/`delete`/`create`/`set_active`/`reboot`/`wipe_super`），不再统一显示 `flash`。文件列改由 `CanChooseFile`（= 操作需要文件且尚无文件）自门控：擦除、重启、切槽位与仅列分区的行不再出现“点击选择文件”提示，因此不会再有提示旁还带大小的空条目；`wipe_super` 因运行前校验 `super_empty` 镜像存在，与 `flash` 一同算作需要文件。六个复选框按读取它们的路径自门控：AVB/Root/清数据/删 COW 面板与「禁用 vbmeta」「添加 Root」「清空数据」「删除 COW」只在通用 fastboot 端点开启，进迭时空整包运行只读「重启」，故其余四项在该 gadget 上关闭；「物理分区」随设备信息读取（`SharedStorage`）。选包后的“下一步：先点发送引导程序……再点烧录选中”日志已移除，运行日志只保留开始/完成/提示与错误。新增 `FlashPartitionEntryFilePromptTests`（18 例）并扩写 `FlashPackageParsingTests` 的三条解析用例以锁定该判定；`Tests.Flash` 150/150、`Tests.Services` 2059/2059（7 跳过）、语料门控 `UOTAN_FLASH_PACKAGE_CORPUS=D:\Firmware` 6/6、全量 8962 用例 0 失败（43 跳过）、构建与 format `whitespace`/`style` 均零报告。界面观感未走查：测试设备已移除。
- SpacemiT 的 `partition_flash.json`/`partition_2M.json` 与 fastboot.yaml 中仅 stage 的文件不显示为行；QC 风格 ofp 若混有 `.img` 条目不会走 MTK scatter 回退。
- 设置页"垃圾回收"区块的界面布局只经编译绑定与 `LocaleDictionaryTests` 验证，未人工目视。
- 桌面键鼠交互、弹出提示呈现与视觉布局未人工实测。
- maskrom→usbplug 切换期间设备列表选中项不跳动未目视确认。
- 多文件引导加载（2026-11-05，Debug，纯逻辑）：`loader` 行与 EDL 自选 programmer 入口放宽为同时接受 `qsahara_device_programmer.xml`；`SaharaProgrammerManifest` 的有界读取、重复/缺失/结构非法拒绝、单镜像与清单归一化、`SaharaProtocol` 按 `ImageId` 多镜像上传共 19 例通过（`UotanStudio.Tests.Protocols.EdlProgrammerImageSetTests`），真实厂商清单 `D:\Firmware\firehose-8850\qsahara_device_programmer.xml`（8 个镜像，id 13/21/36/37/38/59/60/61）以 `UOTAN_SAHARA_MANIFEST_SAMPLE` 门控解析通过；宿主 `loader` 行清单结论 11 例通过（`UotanStudio.Tests.Flash.FlashLoaderCommandTests`）。本机无高通硬件：Sahara 多镜像上传只由脚本化传输回放，未在 9008 真机确认目标实际请求多个 `image_id`，也未确认清单中的 `multi_image_qti.mbn`/`multi_image.mbn` 在真机上被请求的顺序；选择器对话框与行内状态文字的观感未人工走查。

**AOT 与平台**

- 全部测试时长来自本机 Release 单进程运行；CI 的 `dotnet test --solution` 路径与 Runner 机器未实测。
- win-x64 以外的 RID 发布未执行；riscv64/loongarch64 的真实发布未执行（本机无对应交叉工具链），烘焙的 SDK 版本未在发布产物实测。
- Satori GC 的延迟与吞吐特性未测量。
- `CommTrace.Write` 的零分配只在类型层面成立，未做分配量基准。