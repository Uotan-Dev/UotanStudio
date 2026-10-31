# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## 真机 RKDL loader 下载阻塞的定位实测（2026-10-31，Windows x64，Debug，共享工作树）

- 现象与分层定位：真机 `USB\VID_2207&PID_350B\40000030`（rockusb.sys 5.14，oem2.inf）在 GUI 中读取芯片信息报 `数据错误(循环冗余检查)`，loader 下载报 `信号灯超时时间已到`。经 `UOTAN_RKDL_E2E=1` + `UOTAN_RKDL_LOADER=D:\Download\MiniLoaderAll.bin` 的临时真机探针（已还原）逐层测得：CBW bulk 写 0 ms 即报 Win32 23（CRC 错误）、管道复位（IOCTL 0x22008 单管道与复位全部管道）与 PnP 禁用/启用后同样 0 ms 失败；rockusb.sys 内部 5 秒控制超时（位移常量 0x1388）在更早的 loader 下载里表现为 Win32 121（信号灯超时）。
- 对照证据（rkdeveloptool 与驱动反汇编）：`RkdlFrame` CBW/CSW、CRC-16/CCITT-FALSE（无反转、高字节在前）与 4096 分块 + 4094 填充字节 + 4095 补齐规则逐项与 `reference/rkdeveloptool/RKComm.cpp` 一致；`rockusb.sys` v5.14 vendor IOCTL 0x8000A000/A004 处理器把输入缓冲原样转为 URB_FUNCTION_VENDOR_DEVICE（Function 0x17、Request 0x0C、Index 0x471/0x472、方向 H2D），与 libusb 路径等价，本仓库 `RockusbDevice.ControlTransfer` 的 IOCTL 用法经反汇编逐字段核对无误。
- 结论：协议栈与驱动接口层均无缺陷；设备在"进入 Maskrom"操作后进入 USB 总线级僵死（所有传输 0 ms 报 CRC 错误，主机会话软件无法清除，管道复位与 PnP 重枚举无效），需要物理重新插拔设备（或换 USB 口/断电重启）后再试。重新插拔后若同样现象复现，应改用 `DriverAssistant` 的 WinUSB 换绑验证一次（同为 0x0C vendor 请求）。
- 一次性诊断物：探针源码未保留（临时改动已全部还原），rockusb.sys 反汇编脚本在 `%TEMP%\rkfix\`（会话产物，仓库外）。
## Satori GC 发布替换与设置页 GC 展示实测（2026-10-31，Windows x64，Debug，共享工作树）

- GC 替换：`UotanStudio.Desktop.csproj` 顶部以 `<Sdk Name="PublishWithSatoriGC" Version="10.0.15-satori.40994390316.1" />` 引入发布 SDK，`UseSatoriGC=true` 默认开启；publish 阶段把 ILC SDK 与 ILC 工具链替换为 Satori 构建（AotSdk/AotTools 包随 restore 进入 `project.assets.json`），仅自包含发布生效，CI 六个 RID 均自包含。SDK 版本跟随上游开发更新，升级只改 csproj 版本号；发布时会有一条预期的 `Satori GC is experimental` 警告。
- 设置页展示：设置页渲染区块后新增只读「垃圾回收」区块（GC 实现、服务器 GC、并发 GC、延迟模式）；GC 实现经 `GC.GetConfigurationVariables()` 是否含 `SatoriGC` 键判定，服务器/并发状态取 `GCSettings.IsServerGC` 与 `System.GC.Concurrent` 配置，语言切换时随 `OnLanguageChanged` 刷新。
- 文档与脚本同步：`README.md`/`README-EN.md` 特性列表、`UotanStudio.Desktop/AGENTS.md` 发布节、`.github/workflows/release.yml` 发布步骤注释。
- 门禁：`dotnet restore UotanStudio.slnx -p:Configuration=Debug` 成功；`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；触碰文件的 `dotnet format whitespace` 校验干净（全解 whitespace 校验被共享工作树既有的 `RkdlSerialReader.cs` FINALNEWLINE 挡住，与本任务无关）；`LocaleDictionaryTests` 6/6。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 172,613,632 B，二进制内可检索到 `SatoriGC` 配置变量键，证实 Satori GC 已进入发布镜像；输出仅含既有的 Ursa/Dock/LTRData/YamlDotNet/Common.Logging IL2104/IL3053 与 Avalonia XAML 反射绑定警告，加一条 SDK 预期的 `Satori GC is experimental`。
- 未验证边界：Satori GC 在发布产物内的运行时行为（GC.GetConfigurationVariables 实测值、延迟与吞吐）未在真实 GUI 会话里核验；设置页新区块的界面布局只经编译绑定与 LocaleDictionaryTests 验证，没有人工目视；其余 RID 发布未执行。

## 瑞芯微 loader 行与 parameter 行的勾选检查实测（2026-11-01，Windows x64，Debug，共享工作树）

- 行内文件选择器：`OnChoosePartitionImageClick` 去掉 `entry.Command == LoaderCommand` 守卫，`Loader` 行的「选择文件」链接与其它行一致地打开 `StorageProvider` 单文件选择器；选中后写 `FilePath`/`FileName`/`Size`、把 `ApplyRkLoaderPath` 指向新文件（`EffectiveRkLoaderPath` 与 `HasRockchipLoader` 随之重算并立即重发门控），并调用 `UpdateSpecialFileRowStatus` 对新文件做一次检查。此前该行的链接点开后无反应，只能改配置文件重新导入。
- 可勾选范围：`ApplyRkConfigRows` 不再强制 loader 行不可勾选，`CanSelect` 改为「文件存在或未指定文件」，因此 `Loader` 行与 `parameter` 行都能勾选；导入的行照旧默认不勾选。`FlashPartitionEntry.Command` 与 `FilePath` 变化时同样触发重新检查，用户把某行命令改成 `loader`/`parameter` 后无需重新导入。
- 勾选即检查：`OnEntryPropertyChanged` 在 `IsSelected` 变为真时转 `OnEntrySelected` → `UpdateSpecialFileRowStatus`。loader 行走 `ValidateRkLoaderFileAsync`：`FirmwareFormatRegistry.RegisterAll()` 后按 `FormatDetector.Detect` 的注册名 `Rockchip Loader Image` 取处理器（`RockchipLoaderFormatHandler` 与 `RockchipLoaderImage` 是固件库内部类型，宿主不改其可见性），`ReadMetadataAsync` 读 102 字节头部、三张条目表与末尾 CRC-32，只有 `ChecksumStatus=Verified` 才算有效；文件缺失、非 loader、结构非法、校验和不符分别落到 `Flash_RkLoaderMissing`/`Flash_RkLoaderNotLoader`/`Flash_RkLoaderInvalid`/`Flash_RkLoaderChecksumMismatch`，行内状态是 `Flash_RkLoaderValid` 或 `Flash_RkLoaderInvalidStatus`。parameter 行走 `RockchipParameterParser.Parse`，有效时行内显示 `Flash_RkParameterValid`（分区数），失败落到 `Flash_RkParameterInvalidStatus`。
- 发布时间与线程：整幅 loader 会被读取（末尾 CRC-32 覆盖它之前的每个字节），因此检查经 `Task.Run` 在调用线程之外完成，回到调用线程后才写日志与行状态；未完成的检查挂在 `FlashViewModel.SpecialFileRowCheck` 上供调用方等待，页面不阻塞也不用 `async void` 吞异常（异常经 `AppendError` 记录并把行标为无效）。
- 写入路径排除：新增 `FlashViewModel.IsWritableEntry`（`Command` 不是 `loader`/`parameter`），`RunSelectedAsync`、`RunRkEraseSelectedAsync`、`OnEdlEraseClick`、`OnEdlReadbackClick`、`HasSelectedEdlEntries` 一律经它过滤，因此勾选 loader 行不会把 `Step` 为空的配置行送进 `FastbootStepService.ExecuteAsync`（原先只排除 `loader`，parameter 行仍会走进最后的 `else` 分支并抛空引用），也不会让这些行进入擦除/回读的分区列表。loader 仍只经 `EffectiveRkLoaderPath` 交给 RKDL 刷写路径。
- 本地化：中英双份 resx 与 `LocaleKeySets` 同步新增 12 个键（`Flash_RkLoaderChecking/Valid/InvalidStatus/NotLoader/Invalid/ChecksumMismatch/Checked`、`Flash_RkParameterValid/InvalidStatus/Checked/Invalid`、`Flash_CommandNotRunnable`）。
- 定向测试：新增 `UotanStudio.Tests.Flash.FlashRockchipFileRowTests` 7/7（有效 loader 行可勾选且被判有效、非 loader 内容与篡改末 4 字节校验和的镜像都被判无效、parameter 行按 `CMDLINE: mtdparts=` 数出 2 个分区、非分区表被判无效、文件被替换后重新勾选给出新结论、`IsWritableEntry` 对 `flash`/`erase`/空命令为真而对 `loader`/`parameter` 为假）；`FlashGlobalLogRoutingTests` 的 loader 行断言从 `CanSelect=false` 改为 `true`；`UotanStudio.Tests.Flash` 命名空间 53/53；`LocaleDictionaryTests` 6/6。
- 编辑后的行可执行性：`RunSelectedAsync` 的末段分支从 `entry.Step!` 改为 `entry.Step is { } step`，命令被改成 `flash` 但仍无步骤的解析行不再抛空引用，而是写 `Flash_CommandNotRunnable`、把该行标失败并停止后续行，与文件缺失的处理一致。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 全解 `--verify-no-changes` 均零报告（`insert_final_newline=false`，本轮顺手去掉几处误加的行尾换行）。
- 全量测试：8121 总数 / 8098 通过 / 21 跳过 / 2 失败（1m53s）。两条失败都与本次改动无关且都是共享工作树既有状态：`HostLayeringGuardTests.Views_DoNotImportApplicationServiceNamespacesOutsideTheFrozenAllowList`（`AvbToolWindow.axaml.cs` 的冻结豁免已不匹配任何 import，该文件本轮未改）与 `RockusbPipeSelectionTests.SelectPipes_OutEndpointDeclaredFirst_StillMapsInToZeroAndOutToOne`（并行会话新增的未跟踪用例）。`ErofsRebuildFeatureReplayTests` 在一轮里出过 1 条失败，单跑该类 20/20 通过，末轮全量未再复现。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 `UotanStudio.DesktopinRelease
et10.0win-x64publishUotanStudio.Desktop.exe` 172,645,376 B；输出只有既有的 Avalonia XAML 反射绑定与 LTRData/YamlDotNet/Common.Logging 的 IL2026/IL2104/IL3053，没有指向 `UotanStudio.*` 的裁剪/AOT 警告。
- 未验证边界：本机没有 GUI 目视——行内「选择文件」链接的真实点击热区、`StorageProvider` 选择器在 loader 行打开后的实际返回值、行状态文字在表格里的换行与截断都只有视图模型层断言与编译通过，没有截图或人工点击记录；`StatusText` 与进度条同列，长摘要可能被列宽截断，本机未实测。检查读的是调用瞬间的磁盘内容，文件被选中后再次被替换不会自动重查，需要重新勾选或重新导入。真机 loader 加载与 RKDL 写入未执行。

## 瑞芯微 loader 门控、配置行按钮与通讯插桩实测（2026-10-31，Windows x64，Debug，共享工作树）

- 读取芯片信息的分阶段：`RunRkReadInfoAsync` 在页面未声明 loader 时只发送 maskrom 能应答的命令（CAPABILITY、READ_FLASH_ID），并在 `session.IsMaskromMode` 时输出 `Flash_RkStorageNeedsLoader` 而不是发送 READ_STORAGE；声明了 loader 时先 `ReadAllBytesAsync` 再经 `RunRkdlLoaderTransitionAsync` 在 maskrom 租约上 `DownloadBootLoaderAsync`、释放租约、等重新枚举后用新会话读存储与芯片信息，loader 已是 loader 阶段则跳过下载（回调返回 `null`，不重建会话）。这修掉了 `读取芯片信息: 10000030 (bootrom)` 后紧跟 `数据错误(循环冗余检查)。` 的那条路径——存储命令被送进 maskrom。
- 门控第二维：`FlashDeviceUiPolicy.RockchipLoaderControls`（`ReadInfoButton`、`ReadbackSelectedButton`、`FlashSelectedButton`、`EraseSelectedButton`、`RkPane`）与 `IsAvailable(name, mode, hasRockchipLoader)`；`SetControlEnabled`/`RefreshDeviceUiGate` 传 `HasRockchipLoader`（`EffectiveRkLoaderPath is not null`），loader 行增删都会经 `RefreshRockchipLoaderGate` 立即重算。无 loader 的瑞芯微设备因此拿不到任何会向 maskrom 发存储命令的按钮，通用功能的烧录、获取信息与瑞芯微二级菜单一并关闭。
- 配置行按钮失效：`ApplyRkConfigRows` 由 `Partitions.Apply(Partitions.Count, rows)` 改为 `Partitions.Replace(rows)`，与 `RefreshVisiblePartitions` 的 `Replace` 语义对齐；导入时同时跳过路径指向另一份 `.cfg` 的行（`RockchipConfigFile.LooksLike`），避免把配置文件本身当作镜像行发布到网格。
- 全局日志：`FlashViewModel.AppendLog` 逐行写 `LogService`（页面日志仍按 32 行/4096 字符/100 ms 合批），46 处 `catch` 从 `AppendLog(ex.Message)` 改为 `AppendError(ex)`，输出 `{异常类型}: {消息}` 并带内层异常。`LogService.ApplyDebugMode` 同时驱动 `CommTrace.Level`/`CaptureRawPayloads` 与 `UsbTrace.IsEnabled`/`CaptureFrames`；RKDL 侧在会话打开/关闭、CBW 出帧与 CSW 回帧、DATA 收发、厂商请求分块、loader 下载前后、maskrom 拒绝存储命令处插桩，raw data 经 `CommTrace.Payload` 在调试模式下输出。
- 视图桥：`FlashViewModel.Surface` 的 5 处 `is null` 守卫改为读 `_surface` 字段（原先守卫调用的是会抛异常的属性，未附加页面时必然抛 `InvalidOperationException`），`Field<T>` 未附加时返回分离的虚拟控件——两者共同让未附加页面的视图模型可以安全读写页面状态。
- 定向测试：`FlashDeviceUiPolicyTests` 10/10（新增 loader 维度 3 条），`FlashGlobalLogRoutingTests` 4/4（调试模式开关驱动 Comm 诊断、`AppendLog`/`AppendError` 落到全局日志对应级别、loader 行只在文件存在时生效、指向另一份 cfg 的行不进网格且 loader 行命令为 `loader` 且不可勾选），`UotanStudio.Tests.Flash` 命名空间 47/47，`UotanStudio.Tests.Protocols` 命名空间 175/175（含 `RkdlModeClassificationTests` 32/32）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 全解 `--verify-no-changes` 均零报告；`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 `UotanStudio.Desktop.exe` 172,622,848 B，输出只有既有的 Avalonia XAML 反射绑定与 Ursa/Dock/YamlDotNet/Common.Logging 裁剪提示。
- 全量测试：8115 总数 / 8092 通过 / 21 跳过 / 2 失败（1m54s）。两条失败都不在本次改动的文件上：`HostLayeringGuardTests.Views_DoNotImportApplicationServiceNamespacesOutsideTheFrozenAllowList`（`AvbToolWindow.axaml.cs` 的冻结豁免 `UotanStudio.Services.Keys` 已不再匹配任何 import，该文件本轮未改）与 `RockusbPipeSelectionTests.SelectPipes_OutEndpointDeclaredFirst_StillMapsInToZeroAndOutToOne`（并行会话新增的未跟踪用例，配合同会话重写的 `RockusbDevice.cs`）。`ErofsInodeAdviseAcceptanceRegressionTests` 在首轮全量里报过 1 条失败，单独跑该类 8/8 通过，复跑全量未再复现。
- 未验证边界：本机没有瑞芯微真机，maskrom→loader 重新枚举后的存储读取与 `Flash_RkStorageNeedsLoader` 的实际界面提示只在合成设备与单元测试上验证；`FlashView` 配置行「选择文件路径」按钮、loader 门控的启用/禁用、全局日志窗口的实时输出都属于真实界面行为，本机未启动 GUI 目视，只有视图模型层断言与编译通过。

## 瑞芯微 loader 元数据实测（2026-10-30，Windows x64，Debug，共享工作树）

- `UotanStudio.Firmware/VendorFirmware/Rockchip/` 新增只读格式处理器 `Rockchip Loader Image`：按 102 字节 `STRUCT_RKBOOT_HEAD`（`BOOT`/`LDR ` 标签、版本、合并版本、发布时间、芯片代码、签名与 RC4 标志、三张条目表几何）、57 字节 `STRUCT_RKBOOT_ENTRY`（类型码 1/2/4、20 字符 UTF-16 名称、数据偏移/长度/延时）与末尾 Rockchip CRC-32 解析 RKBOOT/RKLOADER 镜像。识别只依赖内容（`BOOT`/`LDR ` 标签加自洽的头部长度与条目步长），不声明 `.bin` 扩展名；参考实现在 `reference/rkdeveloptool` 的 `RKBoot.h`/`boot_merger.h`、`reference/rkbin` 的 RKBOOT ini 与 `reference/rkbin/tools/boot_merger`。
- 字段解码依据：loader 版本取打包字的高字节与低字节（`RKBOOT/RK3588MINIALL.ini` 的 `MAJOR=1 MINOR=11` 对应实测 0x010B → `1.11`）；芯片代码按高位到低位的 ASCII 打包还原（`RK3588MINIALL.ini` 的 `NAME=RK3588`、`RK3399MINIALL.ini` 的 `NAME=RK330C` 分别对应实测 `3588`、`330C`）；RC4 标志非零即 RC4 关闭（与 `boot_merger.h` 的 `hdr->rc4Flag = 1` 及 `[FLAG] RC4_OFF=true` 一致）；发布时间只在日历字段落在有效范围时上报。
- 真实样本（未入库，仓库外路径）：`D:\Download\MiniLoaderAll.bin`（500,160 B，标签 `LDR`、版本 `1.11`、芯片 `3588`、RC4 关闭、471/472/loader 条目数 2/1/3、CRC-32 `0x9B01F152` 校验一致）；`D:\Download\rk3399_loader_v1.27.126.bin`（465,230 B，标签 `BOOT`、版本 `1.26`、芯片 `330C`、签名与 RC4 关闭均为否、条目数 1/1/2、CRC-32 `0x8F637255` 校验一致）。两条镜像的 6 个与 4 个条目全部解出：`UsbHead` 载荷以 `RKNS` 开头，rk3399 的 `471_0`（DDR）与 `loader_0`（`FlashData`）首字节相同，与 ini 里 `FlashData=ddr bin` 的装配关系吻合。
- 定向测试：`UotanStudio.Tests.Firmware.RockchipLoaderTests` 16/16（内容识别与 `BOOT`/`RKFW` 拒绝、`.bin` 经内容识别路由到固件信息面板、头部字段与条目表组件名及长度、校验和不一致仍上报结构、截断与越界负载判失败、解包字节、按名选择解包、单条目上限与校验和拒绝且不落文件、真实样本门控用例）；`UotanStudio.Tests.Services.DocumentFormatPolicyTests` 23/23；`LocaleDictionaryTests` 6/6（中英双份 resx 与 `LocaleKeySets` 同步新增 10 个键）。
- 全 Firmware 命名空间：4486 总数 / 4473 通过 / 13 跳过（`UOTAN_EROFS_CORPUS`、`UOTAN_RK_LOADER_SAMPLE` 等环境门控）/ 0 失败，单轮 41s。
- 门禁：`dotnet build UotanStudio.Firmware/UotanStudio.Firmware.csproj -c Debug` 与 `dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Debug` 均 0 错误 0 警告；全量 `dotnet build UotanStudio.slnx -c Debug` 因本机正运行 `UotanStudio.Desktop`（进程 12304/10180 锁定 `UotanStudio.Desktop\bin\Debug\net10.0\*.dll`）无法写默认输出目录，改用 `--artifacts-path %TEMP%\uotan-slnx-build` 完成全量构建：0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 两项 `--verify-no-changes` 全解零报告。
- 全量测试：`dotnet test UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 8077 总数 / 8055 通过 / 21 跳过 / 1 失败（3m 级）；唯一失败是 `UotanStudio.Tests.Devices.DebugBridgeRouteTests.RequireDirectUsb_AutoWithoutServer_AllowsDebugInterface`，`HdcServerClient.ExecuteAsync` 等满 10 秒超时——本机有 HDC server 在跑，与本次改动无关（未触碰 `UotanStudio.Comm`）。用 `--artifacts-path` 输出目录跑全量测试会另报 22 条失败，全部是 `TryFindRepositoryRoot`/`SolutionMembership`/`NamespaceOwnership` 一类依赖「测试宿主位于检出目录内」的守卫，属构建输出路径副作用；默认路径下这些守卫全通过。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true --artifacts-path %TEMP%\uotan-aot-rkloader` 成功，产物 `UotanStudio.Desktop.exe` 172,612,608 B；输出只有第三方既有的 IL2026/IL2104/IL3053，没有指向 `UotanStudio.*` 的裁剪/AOT 警告；临时产物已删除。
- 未验证边界：共享工作树里并行会话的未跟踪文件 `UotanStudio.Tests/Flash/FlashDeviceUiPolicyTests.cs` 有 `CS0051`（`FlashDeviceMode` 可访问性）编译错误，测试工程按原样无法构建；本轮用 `%TEMP%` 下的一次性 props 经 `CustomAfterMicrosoftCommonTargets` 排除该文件完成构建与测试，没有改动该文件，其对应测试也未跑。固件信息面板与解包对话框未启动 GUI 目视；`UotanStudio.Comm` 的 RKDL 下载路径另有一份面向设备的 loader 解析器，未与本解析器做同镜像联机对照。重打包未实现，loader 保持只读。

## RKDL 模式识别改为描述符位与 Windows 集线器侧读取实测（2026-10-30，Windows x64，Debug，共享工作树）

- 判定依据：瑞芯微自家 RKDevTool v2.96 在枚举阶段从集线器端口数据读设备描述符，用 `bcdUSB` 的 bit 0 判定阶段（为 1 是 loader，为 0 是 maskrom），端口记录里同时读 `CurrentConfigurationValue`；产品 ID 只用于识别 boot ROM 区间，不区分阶段。本仓库此前的 `KnownLoaderProductIds` 表把 maskrom 与 loader 的 PID 混在一张表里，凡命中即标 `loader`，这是把 `2207:350B` 显示成 `loader` 而 RKDevTool 显示 MASKROM 的原因。
- 描述符贯通：`UsbDeviceInfo`/`UsbDevice` 新增 `BcdUsb`，走 `UsbDeviceInfoFactory`、`UsbInfoCopy`、`DeviceEndpoint` 的相等与哈希；Windows 由 WinUSB 句柄、Linux 由 sysfs 描述符、libusb 由 `DeviceDescriptor.Usb`、macOS 两个后端由控制传输、HarmonyOS 由配置描述符填充。
- Windows 受限驱动路径：设备被 `rockusb.sys` 等厂商驱动占用时 `WinUsb_Initialize` 一定失败，因此新增 `UsbHubDescriptorReader`，按实例 ID→父集线器端口号（`DEVPKEY_Device_Address`）→集线器接口路径→`IOCTL_USB_GET_NODE_CONNECTION_INFORMATION` 读取描述符，并用 VID/PID 与枚举结果互校；结果进 `UsbSerialCache`（沿用 30 分钟/2 秒节流），只对瑞芯微 VID 的节点尝试。`Exact USB endpoint is unavailable.` 换成可执行的消息：区分「设备已拔出」与「仍在位但没有 WinUSB 句柄（接口被其他内核驱动占用）」，并给出 VID:PID、接口路径与替换驱动的指引。
- 阶段判定：新增 `RkdlDeviceMode` 与 `RkdlConstants.ClassifyMode(productId, bcdUsb)`；`KnownLoaderProductIds` 更名 `KnownBootromProductIds` 并明确它只表达 boot ROM 区间；`RkdlSession.Mode`、`IsRockusbMode`、`IsMaskromMode` 与宿主的设备列表标签都改由该位推导，`bcdUSB` 缺失时报未知而不是猜 loader。maskrom 只应答设备信息与 SDRAM 命令，因此 `WriteCbw` 在 maskrom 下拒绝存储类命令（0x04/0x05/0x06/0x0B/0x14/0x15/0x16/0x21/0x22/0x25/0x2A/0x2B），避免等满超时后报 CSW 校验失败。
- 定向测试：`RkdlModeClassificationTests` 32/32（新增，含 maskrom 拒绝存储命令而放行信息命令、无描述符位时不猜阶段、芯片名 ASCII 标签解码），`UsbSerialCacheTests` 10/10（新增 4 条描述符缓存用例），`RkdlDeviceE2ETests` 随门控跳过。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 全解零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 8076 总数 / 8055 通过 / 21 跳过 / 0 失败（2m07s）；Release NativeAOT `dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，警告仍是既有的 Avalonia XAML 反射绑定与 Ursa/Dock/YamlDotNet/Common.Logging 裁剪提示。
- 未验证边界：本机没有瑞芯微真机，maskrom/loader 阶段判定、集线器侧描述符读取、`rockusb.sys` 占用下的打开失败消息、loader 下载后重新枚举都只在合成设备上验证；maskrom 对 0x1A/0x1B/0xAA 的实际应答与存储命令（0x2B）的拒绝范围需要真机确认；`UOTAN_RKDL_E2E=1` 闭环未执行。

## Magisk v20.0–v31.1 全 Release 语料实测与两处修补修复（2026-10-30，Windows x64，Debug，共享工作树）

- 语料实测：用临时工程（%TEMP%，跑完即删）经 `RootPatchCatalog` 之外的公共 API 分页拉取 topjohnwu/Magisk 全部 Release（共 39 个 tag，v20.0→v31.1），以测速排序的候选源（探针胜出为 github.com 直连，github.boki.moe 镜像承担部分交付）下载全部 apk/zip 资产共约 52 个并逐一 `BootPatchAnalyzer.AnalyzeAsync`，同时列出 ZIP 内 lib 布局与修补目标标记。所有 v29+ 资产的 GitHub digest 校验全部通过（更早版本无 digest）。
- 布局实测结论：v22.0–v31.1 的正式 APK 全部识别为 Magisk、MAGISK_VER 正确、四 ABI 齐全；真实布局分界在 v27/v28——v24–v27 为 magisk32/magisk64 分体库（无 libmagisk.so、无 init-ld），v28+ 为 libmagisk.so 合并布局（含 init-ld）。v20.0–v21.4 仅发布 flashable/uninstaller ZIP（无 lib/ 布局，二进制在 arm/、x86/ 目录），两类 ZIP 均识别为 Unknown，修补流程不支持属预期边界。
- 缺陷修复一（资产挑选器）：v24.0–v26.1 的 Release 同时发布 28 KB 的 `stub-release.apk` 壳，与正式 APK 同为「发行版标记 APK」评分且 API 顺序靠前，挑选器选中空壳导致这些版本在修补窗口被判「不受支持」；`Rank` 现将名称含 `stub` 的资产排除（与 debug/sources 同级）。
- 缺陷修复二（分体库版本门槛）：`IsMagiskRuntimeEntry`/`MagiskTarget` 原以 `version.StartsWith("27.")` 决定是否安装 magisk32/magisk64 分体库，实测 v24–v27 均为分体布局，v24.0–v26.4 的修补产物会缺失 magisk 主体；门槛改为 `UsesSplitNativeLibraries`（解析 MAGISK_VER 主版本号落在 24–27），`magisk64` 目标映射同步修正，MAGISK_VER 带 `:代码` 后缀的取值正确解析主版本。
- 定向测试：`RootPatchReleaseClientTests` 22/22（新增 `SelectPackageAsset_StubAppShell_LosesToTheManagerApk`），`BootPatchModelsTests` 31/31（新增 `PatchAsync_MagiskV24SplitLayout_InstallsTheSplitNativeLibraries`，断言 magisk32.xz/magisk64.xz 以 XZ 载荷写入 overlay.d/sbin 且 init 为 magiskinit 接管），Firmware 命名空间 4425 通过/2 环境跳过，Net 命名空间 57/57。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 全解零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 7922 总数 / 7914 通过 / 8 跳过 / 0 失败（2m34s）。
- 未验证边界：v24.0–v26.4 修复后只经合成包集成测试验证，未用真实历史 APK 逐一跑完整 PatchAsync（语料在检测后已按临时产物纪律删除）；v20.0–v21.4 flashable ZIP 的支持缺口未列入实现计划；Magisk Alpha（vvb2060）与 Canary 渠道未做同样语料实测；修补产物的真机启动验证仍缺失。

## 修补包镜像测速先行与清单镜像化实测（2026-10-30，Windows x64，Debug，共享工作树）

- 预热流程重排：修补 Boot 窗口与烧录面板 Root 区共用的 `RootPatchPackageService.WarmUpAsync` 先对 Release API 探针（Magisk 仓库 `per_page=1` 的 API 地址）做全镜像测速并固定最快源，随后五个补丁包清单同时并行拉取；每份清单按「固定源优先、源站兜底、其余镜像在后」的候选序逐源尝试，切换来源或加载更多版本都复用该顺序读缓存或请求。镜像列表被设置禁用（`patchPackage.useMirrorList=false`）时跳过测速，清单仅走源站。
- Net 层支持：`HttpDownload.IsGitHubUrl` 纳入 `api.github.com`，API 地址经 marker 前缀改写枚举镜像候选（不支持 API 代理的镜像在探针中失败、排到末位，天然回落源站）；`GitHubReleaseClient.ListPageAsync`/`RootPatchReleaseClient.ListPageAsync` 新增可选排序源列表参数，逐源尝试（传输/状态/JSON 错误换下一源，全部超限时聚合上报；全部「响应过大」时仍走数量减半重试）。
- 测速缺陷修复：`SelectSourcesBySpeedAsync` 的有界读取循环在响应流短于 256 KiB 时不终止、空转至 5 秒探针超时——测速目标换成小体积 API 响应后每轮必现；已改为读到流尾即退出，探针在小响应上即时完成。
- 窗口交互：下载进行中锁定补丁包类别与版本选择（版本准备与开始修补的按需下载都覆盖）；点击「获取更多」时同步锁定类别；下载中在进度条右侧显示传输速度与预计剩余时间（0.25s 采样、0.6/0.4 平滑、按包字节数估算，总大小未知时只显速度，完成或失败后隐藏）。
- 其他：修补窗口的 SukiSU 超级密钥默认改为留空（KPM 模式需非空才能开始；闪刷面板无密钥输入，内部仍以 `KernelPatchDefaultSuperKey` 作为自动修补键）；日志中「Root patch catalog … skipped」等条目对以中文句末标点结尾的异常消息去除传输层追加的英文句点，不再出现 "。." 收尾（`DescribeLogDetail`）。
- 定向测试：`UotanStudio.Tests.Services.RootPatchPackageTests` 37/37（新增镜像供单并固定、镜像禁用仅源站、日志句点去重；预热测试固定源断言更新为 `https://api.github.com`），`UotanStudio.Tests.Net` 命名空间 56/56（新增 API 地址镜像枚举与改写形状、多源回退/首选源跳过/全失败聚合），`LocaleDictionaryTests` 6/6（新增 `BootPatch_TimeRemaining`）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 全解零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 7919 总数 / 7911 通过 / 8 跳过 / 0 失败（1m33s）。
- 未验证边界：速度与剩余时间的实际显示、下载锁定的观感需人工在 GUI 走一遍；真实镜像站对 `api.github.com` 的代理支持参差，不支持者依赖探针失败回落，未对全部 33 个镜像逐一实测；清单改由镜像交付后，版本号/长度/摘要本身出自镜像（载荷仍按摘要校验，但镜像被篡改清单时校验基线随之变化）——信任模型变化未做对抗性验证；`net_io_readfailure` 双句点的修复仅由 `DescribeLogDetail` 单测覆盖，未复现真实断连日志。

## 修补 Boot 窗口 KernelSU 修补模式实测（2026-10-30，Windows x64，Debug，共享工作树）

- 修补模式自动选择：`BootPatchMode`（Auto/Lkm/Gki/Kpm）进入 `BootPatchOptions`，Auto 按包内容路由——KernelSU 系包含 libksud 或内置 .ko 走 LKM（kernelsu.ko 入 ramdisk，KMI 校验仅在 LKM 生效时运行）；携带 kpimg（`assets/kpimg` 或 `assets/kpm/kpimg`）的包走 KPM（KernelPatch 注入内核、ramdisk 原样、superkey 必填，KernelSu 类型包内也携带 kpimg 时 KPM 优先）；`BootPatchKind.GkiKernel`（新增类型）走 GKI——用包本身替换 boot 内核段。修补模式下拉框按要求移除，模式由检测结果自动决定。
- GkiKernel 识别：独立内核文件（0x38 处 ARM64 `ARMd` 魔数的原始 Image，或 gzip 压缩内核）与 AnyKernel3 风格压缩包（含 Image/Image.gz-dtb/Image.gz/Image-dtb/zImage/zImage-dtb/bzImage 条目且无 libksud/ko/kpimg/Magisk 标记）识别为 `GkiKernel`；`DetectKernelArchitecture` 新增 ARM64 头魔数直判，boot 内核架构检测同样受益。自选文件过滤器扩展 `*.img`/`*.gz`/`*` 以接纳扩展名内核镜像。
- GKI 替换细节：256MB 有界读取，gzip 载荷解压后按原内核压缩方式重压；v1 头镜像在替换内核不带 DTB 时回接原内核 DTB（变更列表报告 `re-attach the original kernel DTB`）；ramdisk 与 fstab/overlay 不做任何改动；显式 GKI 只接受 GkiKernel 包，显式 LKM 拒绝 KernelPatch 包。`RootPatchSources.Accepts` 让 KernelSU/SukiSU 来源额外接受 GkiKernel 资产（SukiSU 仍接受 KernelPatch）。
- 窗口 UI 随自动模式联动：GKI 模式隐藏预启动设备输入；KPM 模式把设备框变为超级密钥框（密码符 `*`，默认留空、非空才能开始）；LKM 模式显示 KMI 列表。Magisk 复选框文案 `BootPatch_ForceRootfs` 改为大小写规范的 "Legacy SAR"（中英一致）。
- 定向测试：`UotanStudio.Tests.Firmware.BootPatchModeTests` 10/10（KernelSu 包内 kpimg 检测、原始 Image 与 AnyKernel3 压缩包识别、原始内核与 AnyKernel3 替换 boot 内核并保留 ramdisk、v1 DTB 回接、KernelSu+kpimg 自动 KPM 注入且 ramdisk 不动、显式 LKM 拒绝 KernelPatch 包、显式 GKI 拒绝 KernelSu 包、无内核条目压缩包拒绝），`LocaleDictionaryTests` 6/6，`RootPatchPackageTests` 34/34，`FirmwareArchitectureGuardTests` 12/12，`FirmwareNamespaceFolderTests` 2/2。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告（宿主增量编译另有 3 条既有 CS8604，来自 NodeEditorView 手势代码）；`dotnet format` 的 `whitespace` 与 `style` 全解零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 7906 总数 / 7898 通过 / 8 跳过 / 0 失败（1m53s）。
- 未验证边界：修补模式自动选择在实际下载的 KernelSU/SukiSU Release 资产上的表现需联网走一遍 GUI（GKI 内核包与 KPM 补丁包的发布形态以社区现状为准）；GKI 替换内核与设备 KMI 的匹配性未校验（由使用者保证）；真机启动未验证；修补模式无人工下拉覆盖入口，异常包形态只能通过自选文件显式模式 API 覆盖。

## 密钥管理密码提示与删除联动实测（2026-10-30，Windows x64，Debug，共享工作树）

- APK 证书生成组：生成按钮在密钥库密码为空时由窗口层拦截——导出格式为 JKS/全部时弹出「需要密钥库密码」警示框并回焦密码框，不再静默失败到不可见的状态文本；导出 PKCS12 时弹出「不设密码导出？」确认框，确认后按无密码继续，取消则不生成；PEM 不使用密码，不拦截。拦截依赖 Avalonia `Button.OnClick` 的 `!e.Handled` 门（对 12.1.4 源码逐字核实），标记 Handled 后 Command 不会执行。
- 导出文件登记与删除联动：`ManagedKey` 新增可选 `LinkedFiles`（旧 key.json 反序列化为空列表），`GenerateApkCertificate` 导出成功后原子改写 key.json 登记本次导出的 jks/p12/pem/pk8 路径；`Delete` 同步删除登记文件（逐个 best-effort，仅捕获 IO/未授权异常）并返回未能删除的路径，`Remove` 进回收站路径的行为不变；`Equals`/`GetHashCode` 重写为六个同一性字段，`LinkedFiles` 不参与，保持与 JSON 往返的相等语义。
- 删除确认与状态栏：条目带登记文件时删除确认框改列将被一并删除的文件名，无登记文件时维持原文案；窗口底部新增状态栏（`Status`/`HasStatus`），生成时的导出路径与删除失败的登记路径均可见。
- 定向测试：`UotanStudio.Tests.Services.ApkKeystoreAndCertificateTests` 15/15（新增 `GenerateApkCertificate_RecordsLinkedFilesAndDeleteRemovesThem`、`Delete_MissingLinkedFile_SucceedsWithoutFailures`），`KeyLibraryTests` 10/10，`KeyManagerPurposeTests` 4/4，`LocaleDictionaryTests` 6/6，`DefaultSignatureTests` 8/8，`ProjectOperationsBoundaryTests` 23/23，`NodeCatalogReductionTests` 23/23。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告（宿主增量编译另有 3 条既有 CS8604，来自 NodeEditorView 手势代码）；`dotnet format` 的 `whitespace` 与 `style` 全解零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 7877 总数 / 7869 通过 / 8 跳过 / 0 失败（1m34s）。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功；警告仅 LTRData.Extensions/YamlDotNet/Common.Logging 既有 IL2104/IL3053 与 Makaretu.Dns 的 Tmds.LibC ILC 说明，无指向本轮新增宿主代码的裁剪/AOT 警告。
- 未验证边界：弹窗文案与按钮、密码框回焦、状态栏排版、删除确认文件列表的实际渲染需人工在 GUI 走一遍；登记文件被其他进程占用时删除失败提示的展示路径未在真实占用场景演练；「手动导出副本不受删除影响」仅由 `KeyLibraryTests` 覆盖，未做 GUI 演练。

## Root 清单预热与单次测速实测（2026-10-29，Windows x64，Debug，共享工作树）

- 清单缓存：`RootPatchPackageService.ListPackagesAsync` 对每个来源只读一次 Release 源并缓存结果，并发调用共享同一个在途任务，失败或不完整的读取不会留在缓存里；`CatalogLoaded`/`TryGetCatalog` 让界面判断是否需要显示「正在获取版本列表…」。
- 预热：`WarmUpAsync` 首次调用并行读取全部 5 个来源的清单，再用最新条目（Magisk 优先）测速一次；之后 `ProbeSourcesAsync` 直接返回已固定的站点部分，不再联网。烧录页在页面构造时调用一次预热，修补 Boot 窗口随后只读缓存。
- 日志与重复下载：烧录页仅在清单尚未缓存时输出「正在获取版本列表…」、仅在还没有固定源时输出「正在测速镜像源…」；版本预下载只在本地尚未存放该版本时输出「正在下载 X…」与「补丁包 X 已就绪。」，同一版本已准备好时再次触发选择不会重复下载或重复输出。开始烧录时的包解析命中缓存，不再产生第二组下载/就绪日志。
- 定向测试：`UotanStudio.Tests.Services.RootPatchPackageTests` 24/24（新增 `ListPackagesAsync_RepeatedCalls_ReadTheReleaseFeedOnce`、`WarmUpAsync_EverySource_CachesFiveCatalogsWithoutReadingTheFeedAgain`、`ProbeSourcesAsync_SecondCall_ReusesThePinnedSourceWithoutProbing`）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 对本轮改动文件零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug` 7830 总数 / 7822 通过 / 8 跳过 / 0 失败（1m38s）。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 `UotanStudio.Desktop.exe` 156,262,912 B，警告仅为 Avalonia/Dock/Ursa/Iciclecreek/LTRData/YamlDotNet/Common.Logging 既有的 IL2104/IL3053。
- 预热按来源容错：单个上游读取失败只记一条警告并跳过该来源，其余四个来源照常缓存并完成一次测速。
- 未验证边界：实际界面日志顺序、预热与窗口打开的相对时序、真实镜像的测速耗时仍需人工在 GUI 观察；本轮只有单元测试与编译门禁覆盖。

## Boot 修补后端与烧录页接线实测（2026-10-29，Windows x64，Debug，共享工作树）

- 版本清单：`UotanStudio.Net/Components/Root/RootPatchReleaseClient.cs` 只读各上游 GitHub 仓库的 `/releases?per_page=N`（默认 20，上限 100，1 MiB 上限），丢弃 draft、保留预发布，并按「正式标记的 APK > APK > 正式 ZIP > ZIP」选出安装包，debug/sources 资产被排除。清单来源为 magisk=topjohnwu/Magisk、magisk-alpha=vvb2060/Magisk、kernelsu=tiann/KernelSU、sukisu=SukiSU-Ultra/SukiSU-Ultra、apatch=bmax121/APatch；Alpha 按 Magisk、SukiSU 按 KernelSU 修补。
- 下载与校验：`UotanStudio/Services/PatchPackages/RootPatchPackageService` 把清单、下载、校验、缓存串成一条链路。元数据只读 `api.github.com`（镜像不代理该 API），载荷可以走镜像，但必须通过长度与公布的 SHA-256 后才原子发布；未公布摘要的 Release 只走源站。产物落在共享下载资源目录的 `<magisk|magisk-alpha|kernelsu|sukisu|apatch>/<version>/<asset>`，与 scrcpy 服务端目录同级。
- 测速：窗口打开先拉清单，再以最新资产的地址对源站与 27 个镜像各取 256 KiB（5 s 上限）排序，把最快的一个固定为后续下载的首选源；`DownloadAsync` 的 `sources` 参数复用该排序，避免每次下载重测。
- 修补 Boot 窗口与烧录页共用同一服务。烧录页的 boot 分区下拉框改为从烧录条目自动检测（分区名含 `boot`），不再固定 `boot`/`init_boot`/`vendor_boot`；勾选「添加 Root」时才异步拉清单、测速并回填版本下拉框，版本条目带「已下载」括号后缀；开始烧录时只有该分区同时在勾选列表里才修补，修补结果只写进 `prepared` 映射，行内 `FilePath` 与列表展示不变。烧录流程仍只接受 Magisk/KernelSU 家族（APatch 需要 SuperKey），选中 APatch 或类型不符的包会报「不受支持的包」。
- 定向测试：`UotanStudio.Tests.Flash.FlashRootPartitionTests` 6/6，`UotanStudio.Tests.Flash` 命名空间 22/22，`UotanStudio.Tests.Services.FlashRootImageTests` 6/6，`UotanStudio.Tests.Services.RootPatchPackageTests` 21/21，`UotanStudio.Tests.Services.LocaleDictionaryTests` 6/6，`UotanStudio.Tests.Architecture.HostLayeringGuardTests` 4/4。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 对本轮改动文件零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 7804 总数 / 7796 通过 / 8 跳过 / 0 失败，单轮 1m42s（共享工作树）。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 `UotanStudio.Desktop.exe` 156,161,024 B；警告只有 Avalonia/Dock/Ursa/Iciclecreek/LTRData/YamlDotNet/Common.Logging 既有的 IL2104/IL3053，没有指向本轮新增宿主代码的裁剪/AOT 警告。
- 未验证边界：窗口与烧录页的渲染、勾选后的真实测速与下载耗时、镜像源实际命中率都必须在 GUI 里人工走一遍；本轮只有编译、单元测试与静态门禁覆盖，不能记为界面已验证。烧录页的修补行为依赖真实设备与 boot 镜像，未在真机上执行。

## Hash/Compress 热点优化实测（2026-10-28，Windows x64，Debug，共享工作树）

- `Crc32Register` 零矩阵缓存改为单个不可变快照字段原子发布，并发不同长度续算不再可能把一个长度配上另一个长度的矩阵；`Crc32RegisterConcurrentZeroMatrixTests`（并发 5 种长度 × 2 万次/线程）通过。
- `RegisterContinuationBenchmarks` 增加 4 KiB/64 KiB/4 MiB 块长参数。x64 实测：4 KiB 硬件续算 1.944µs 对 slice-by-8 1.948µs（1.00×）、64 KiB 31.3µs 对 31.0µs（1.00×）、4 MiB 272.8µs 对 1 984.4µs（7.28×）。128 KiB 续算门槛在本机没有可回收的收益，维持不变。
- XZ 块头固定为 28 字节（含 CRC32，尺寸字节恒为 6，体部补零纳入 CRC），`CompressInPlace` 不再对 LZMA2 载荷做整段左移；`ArchiveBrowserTests` 34/34（含 .xz/.txz 列表与解包往返）。
- Sparse RAW 路径（`SparseReader.CopyRawExactly`/`AppendStream`、`SparseWriter` Raw 块）改为块内 `Crc32IeeeAccumulator` 累加、每块一次 `CombineIeee` 折叠；`SparseFillCrcBatchingTests` 4/4。
- 流式编解码（Zstd/Gzip/Brotli/LZ4/Zlib/Deflate/Bzip2/RawDeflate）的 `Stream.CopyTo` 换为池化 `ExactCopy.ToEnd`；`PayloadStreamingTests` 5/5、`LzopCompressionTests` 13/13。
- `ZstdBlockCodec.CompressorCache` 改为按级别多槽空闲列表，并行分块批间不再批量重建原生上下文。
- `CompressionService.DecompressWithOptions` 在预算为 `long.MaxValue` 且令牌不可取消时跳过 `BoundedWriteStream` 包装；`XzDecoderStream.ReadAsync(byte[])` 复用已完成的读取任务。
- Zstd 数组级 `Wrap/Unwrap` 返回精确尺寸的内部缓冲，外层 `ToArray` 是取得 `byte[]` 的必要拷贝，不构成可消除的冗余（行为探测确认每次调用返回新缓冲）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`UotanStudio.Tests.Firmware` 命名空间 4406 总数 / 4404 通过 / 2 跳过（未设 `UOTAN_EROFS_CORPUS`）/ 0 失败，单轮 33s；架构门 `UotanStudio.Tests.Architecture` 55/55。本轮 `dotnet format` 全解校验中 `FlashPackageParsingTests`/`CompoundFileWriterTests`/`DiagProtocolTests` 的报告来自并行会话的未跟踪文件，本次改动文件逐一定向校验为零报告。

## 当前实测（2026-10-25，Windows x64，Debug）

- `dotnet restore UotanStudio.slnx -p:Configuration=Debug` 与 `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误、0 警告（2026-10-27，共享工作树）。
- `dotnet format` 的 `whitespace` 与 `style`：全解零报告（2026-10-27，Release，`--no-restore --verify-no-changes`）。此前的两处 `UotanStudio` 报告已修：`ViewModels/Editors/ScreenMirrorViewModel.cs` 中 `}` 与下一个 `<summary>` 挤在同一行，`Views/Editors/MirrorTrace.cs` 结尾多出一个换行符。
- CI 等价门禁（2026-10-27，Release，共享工作树）：`dotnet build UotanStudio.slnx --no-restore --nologo -c Release` 0 错误 0 警告；两道 `dotnet format` 零报告；`dotnet test --solution UotanStudio.slnx --no-build -c Release --minimum-expected-tests 6000` 7572 总数 / 7565 通过 / 7 跳过 / 0 失败，单轮 1m31s。构建只剩两处需要修的产物：`Protocols/Adb/AdbSyncClient.V2.cs` 原先直接构造 `BrotliStream`，被 `CompressionImplementationBoundaryTests` 判为通用压缩越界，现改走 `CompressionService.Create{,De}compressionStream(..., CompressionFormat.Brotli)`，并用工装证明 `CompressionOptions { Level = 1 }` 与 `CompressionLevel.Fastest` 在同一 1 MiB 输入上产出逐字节相同的 493 B 帧；`Protocols/Apple/Lockdown/LockdownPairingRecord.cs` 原来对每张证书各读两次 `DateTimeOffset.UtcNow`，DER 的 `NotAfter` 只有秒级精度，子证书晚一秒铸造即超过签发者有效期而抛 `ArgumentException`，现由 `CreatePending` 计算一次有效期窗口并传给三份证书。
- 定向测试：`UotanStudio.Tests.Mirroring` 127/127（1 硬件跳过）、`UotanStudio.Tests.Media` 202/202（1 Linux 跳过）、`UotanStudio.Tests.Devices` 305/305、`UotanStudio.Tests.Protocols` 112/112、`UotanStudio.Tests.Architecture` 16/16。架构门覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员。
- 全量（2026-10-27，Release）：7610 总数 / 7603 通过 / 7 跳过 / 0 失败，单轮 2m12s。该轮在共享工作树里先得到 26 项 `McpHttpExposureTests`/`McpToolCallAuditTests`/`Workspace*` 失败,报的都是 `FileNotFoundException : Microsoft.Extensions.Logging.Abstractions`,即并行会话的构建把 `UotanStudio.Tests/bin/Release` 写坏;同一批字节在私有 worktree 里 restore+构建后,`dotnet build UotanStudio.slnx --no-restore --nologo -c Release` 0 错误 0 警告、两道 `dotnet format` 零报告、`dotnet test --solution UotanStudio.slnx --no-build -c Release --minimum-expected-tests 6000` 全绿,以下数字都取自该私有 worktree。
- 同一字节的前两轮分别报出 1 项与 2 项失败：`ErofsTests.Builder_WritesStandardCompressedIFormat`、`ErofsRebuildFeatureReplayTests.Build_TargetDeclaredToDefineTheAdviseBit_WritesItOnEverySingleLinkedFile` 与 `AdbRemoteProtocolTests.PairAsync_StatusFramedResult_IsAccepted`（15 秒 `Timeout` 取消）；三个用例单独复跑分别是 23/23、20/20、14/14 通过，失败轮次本机有并行构建在跑，故记为负载相关的既有抖动，未定位到具体成因，也不写成已通过稳定性验证。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，`UotanStudio.Desktop.exe` 154,245,120 B（147.1 MiB）；只有 Avalonia/Dock/Ursa/Lingua/YamlDotNet/Common.Logging 等第三方既有的 IL2026/IL2104/IL3053 警告，没有本项目新增的裁剪/AOT 警告，内嵌资源名都能在产物中检索到。
- `TreatWarningsAsErrors=true` 生效于 7 个项目：`UotanStudio.Comm`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash`、`UotanStudio.Media`、`UotanStudio.Net`、`UotanStudio.RegexInfer`；全解 0 警告同时意味着受管库的 `CS1591` 为 0。
- 生产项目之间没有 `InternalsVisibleTo` 授权；宿主与 MCP 需要的契约是公共 API，只保留 `UotanStudio.Tests`、`UotanStudio.Benchmarks`、`UotanStudio.DeliverySmoke`、`GpuMirrorSmoke`、`UotanStudio.ProjectLifecycleSmoke` 等脚手架授权。
- MCP 工具目录 `UotanStudio.Mcp/Tools/PluginToolCatalog.cs` 共 48 条描述符，其中 6 条 `FlashGated`，只在 `--allow-flash` 下注册。
- 公共属性的 `<value>` 注释仍有约 257 处缺口（2026-10-18 度量），不影响构建门。

本机每轮出现的跳过项（数量随环境变化，通常 6–7 条）：

| 用例 | 原因 |
| --- | --- |
| `Media.VideoDecoderFactoryTests.Linux_CreatesDeferredBackendWithoutClaimingGpuSupport` | Linux 专属能力边界 |
| `Media.ScreenMirrorTransportTests.OpenAdbLease_NetworkTargetWithoutServer_DoesNotOpenUsb` | 本机有 ADB server 在运行，"server 不存在"分支不可测 |
| `Mirroring.OhscrcpyHdcIntegrationTests.ControlForward_RealDevice_ConnectsAndInjectsTouch` | 未设 `UOTAN_HDC_MIRROR_DIRECT` |
| `Firmware.ErofsCorpusCompactIndexRebuildTests.PreservedRebuild_OfCorpusCompactIndexImage_ReExtractsEveryFileByteIdentical` | 未设 `UOTAN_EROFS_CORPUS` |
| `Firmware.ErofsInodeModelEquivalenceTests.Decode_CorpusImageInode_MatchesIndependentDecodeForEveryField` | 未设 `UOTAN_EROFS_CORPUS` |
| `Firmware.RockchipLoaderTests.ReadMetadataAsync_VendorLoaderSample_ReportsVerifiedChecksum` | 未设 `UOTAN_RK_LOADER_SAMPLE` |
| `Services.ScrcpyServerReleaseTests.BundledCatalog_DigestMatchesTheJarShippedWithTheApplication` | 测试宿主输出与检出仓库中都没有 `UotanStudio.Desktop/Assets/scrcpy-server.jar`，没有可哈希的出厂工件 |
| `Projects.ProjectBoundaryOptimizationTests.Package_RoundTripIncludesSolutionUnknownWorkflowAndImages` | `ProjectArchiveService.ExportPackageAsync` 拿到 solution 路径时只打包子项目目录，包内缺 solution 级 `nodes/`，`ProjectPackage.ImportAsync` 因而拒绝每个已注册节点工程 |

## HarmonyOS update.bin 实测（2026-10-26，Windows x64，Debug）

- `UotanStudio.Firmware/VendorFirmware/HarmonyOS/` 新增只读格式处理器 `HarmonyOS update.bin`，按 L1/L2 组件表（71/87 字节记录、16/32 字节名称字段）、16 字节保留区与 `0x06`/`0x08` 元数据 TLV 解析；`FirmwareFormatRegistry` 在 `UpdateApp` 之前注册，`.bin` 必须先通过布局字（`0x01`/`0x11`）与组件表边界探测。
- 定向测试：`UotanStudio.Tests.Firmware.HarmonyUpdateBinTests` 23/23（L2/L1、`board_list→BOARD.list`、type 0/1 输出名、SHA-256 校验与失败回滚、筛选、预算、覆盖、取消、截断、非法名称、重复名、未知 TLV、metadata-only）。
- 镜像文件管理：`ImageInputService` 新增 `ImageFormatKind.UpdateBin`，标签为 `update.bin`（`ParseFormatLabel` 可回读）；`ProbeFormat` 经已注册的 `HarmonyOS update.bin` 处理器识别，`ImageViewModel` 的解包模式扫描也登记该类型，导入/刷新/工程恢复后不再显示 `unknown`。`ImageInputServiceTests` 75/75（含 `DetectFormat_HarmonyUpdateBin_ReturnsUpdateBin` 与标签往返）；DataGrid 直接绑定该字符串，本轮未启动 GUI 目视渲染。
- 创建解决方案/子项目：`ContainerKind.UpdateBin` 接入 `ContainerStructureAnalyzer`（列出组件名、扩展名与大小），`ContainerEntryExtractor` 经处理器的 `UnpackComponentsAsync` 定向解出组件且不写 `metadata.json`；`NewProjectWindow` 的勾选分流把 update.bin 组件送入分区列表（ZIP 成员仍只属于 ZIP），`MenuViewModel` 的容器导入、选择性提取与完全解包复用既有路径。真实样例经临时工装：完整选择解出 4 个组件，SHA-256 与组件表一致；只选 `package_info`/`version` 时仅解出 `package_info.img`/`version.img`。`ContainerStructureTests` 23/23、`NewProjectSelectionTests` 3/3；新建对话框的渲染与文件选择器未启动 GUI 目视。
- 全 Firmware 命名空间：4357 总数 / 4355 通过 / 2 跳过（未设 `UOTAN_EROFS_CORPUS`）/ 0 失败，单轮 32s。
- 真实样例：`D:\Download\update_full_cust_HAD-W24_all_cn.zip`（SHA-256 `e1f28c16e77cfb05d73ab9c91dc0395f3f532b2e6daa2e071dcbc791ee197aed`）中的 `update.bin`（35,778 B，SHA-256 `264d8bf27b82f8f6bfabf7d3341a2c050644d1e8e30f4e741d554a23264ba14a`）经临时控制台工装解析：布局 L2、4 个组件、数据偏移 2,851、载荷 32,927 B、元数据 TLV `0x08`；`VerifyChecksums=true` 解出 `package_info.img`（121 B）、`BOARD.list`（20 B）、`ptable.img`（18 B）、`version.img`（32,768 B），四个输出 SHA-256 与组件表记录逐一相同；同时发布 `metadata.json`，`canPackDirectory=false`。样例未加入测试集、`TestData/` 或仓库。