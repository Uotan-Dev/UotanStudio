# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## RKDL 下载模式补全实测（P1/P2，2026-11-02，Windows x64，Debug，共享工作树）

- P1 高级维护（Comm）：新增 `RkdlSession.Maintenance.cs`，把 TEST_UNIT_READY 子码做成高层调用——`EraseSystemDiskBySubCode`（0xFE）、`LowerFormatBySubCode`（0xFD）、`EraseUserDataBySubCode`（0xFB）、`GetUserSectorMap`（0xF9）。前三个统一走 `WaitForMaintenanceSubCode`：按时长大端解码 CSW 数据余量（低 16 位总量、高 16 位当前），每秒轮询一次直至就绪，超时抛 `RkdlError.DeviceUnready`（新增 `RkdlError.DeviceUnready = 12`），进度越界（current > total）同样拒绝。三个擦除入口要求精确确认短语 `"WIPE"`，在任何一个字节上线之前以 `UnauthorizedAccessException` 拒绝。
- P2 进阶/开发者命令（Comm）：新增 `RkdlSession.Devices.cs`——`WriteSdram`/`ReadSdram`/`ExecuteSdram`（SDRAM 子码固定 0xAA，EXECUTE_SDRAM 用 0x0A 命令块长度并容忍重枚举导致的 CSW 读失败）、`SetResetFlag`、`WriteEfuse`/`ReadEfuse`、`ReadSpiFlash`/`WriteSpiFlash`、`WriteNewEfuse`/`ReadNewEfuse`、`TestBadBlock`、`ReadSector`。`RkdlConstants` 新增 `TestBadBlock = 0x03`/`WriteNewEfuse = 0x23`/`ReadNewEfuse = 0x24` 三个操作码、`MaxTestBlocksPerCommand = 512`、`TestBadBlockResultLength = 64`、`MaxReadSectorsPerCommand = 0x14`、`SdramVendorSubCode = 0xAA`。
- 边界对齐官方二进制：反编译 `RKDevTool v3.37` 的 `0x5BE4E0`（TEST_BAD_BLOCK）确认 `blockCount > 0x200` 与 `flashChip >= 4` 都返回 `-9`，坏块位图定长 64 字节，CSW 状态 1 是普通失败而非坏块标记；`0x5BD4A0`（READ_SECTOR）确认 `sectorCount > 0x14` 返回 `-9`、传输长度为 `528 * count`。`RKU_WriteSector` 的 32 扇区上限（`RKComm.cpp` 780 行）与 `RKU_ReadSector` 的 20 扇区上限因此不可合并。两个读入口额外拒绝零长度范围，因为空范围没有可返回的内容——这是本地收紧，比官方更严。
- 宿主接线：新增 `FlashViewModel.RockchipMaintenance.cs` 分部（「擦除系统盘」「低级格式化」「擦除用户数据」「用户扇区映射」四个动作），三个擦除走 `DialogService.ConfirmDangerousAsync` 并要求键入 `WIPE`，与 EDL/MTK 的危险门一致；`FlashView.axaml` 在瑞芯微分类补四个按钮、code-behind 补四个转发；`Common.resx`/`Common.zh-CN.resx` 与 `LocaleKeySets.Common` 同步新增 9 个键。
- 定向测试：新增 `RkdlMaintenanceAndDevicesTests` 26 项通过/0 失败，断言真实 CBW 线格式（byte15 操作码、byte16 子码、byte17-20 大端地址、byte13 芯片号、byte22-23 大端长度）、范围守卫、`WIPE` 门零写入拒绝与超时语义。`UotanStudio.Tests.Protocols` 273 通过/0 失败，`FlashDeviceUiPolicyTests` 21 通过/0 失败，`LocaleDictionaryTests` 6 通过/0 失败，`UotanStudio.Tests.Devices` 423 项中 1 项失败（见下）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 两项 `--verify-no-changes` 零报告；`dotnet publish UotanStudio.Desktop -c Release -r win-x64 --self-contained` 成功，新增代码未引入任何裁剪/AOT 警告（报告中的 IL2026/IL2104/IL3053 全部来自 Avalonia XAML、Dock、YamlDotNet 等既有第三方）。
- 环境边界：全解决方案测试单轮受 15 分钟上限约束，本轮被上限终止，改用命名空间与类定向跑；`UotanStudio.Tests.Devices` 唯一失败 `AdbTlsTransportTests.UpgradeToTls_DeviceRebuildsItsCertificateEachHandshake_CompletesBothHandshakes` 为既有 ADB TLS 偶发（单跑通过，该文件未改动）。共享工作树中并行会话仍在改 `FlashViewModel.*`，本轮多次遇到 `bin`/VBCSCompiler 文件锁，均以退避重建通过。
- 未验证：真实瑞芯微设备上的七个新命令与四个维护动作全部未上板执行——`WIPE` 门、子码返回值、坏块位图内容、SDRAM 执行后的重枚举行为都只有线格式与官方二进制层面的证据。P3（v3.37 独有操作码 0x26/0x27/0x28/0x2C/0x30/0xBB）按用户指示不做。

## RKDL 设备信息回填分行实测（2026-11-02，Windows x64，Debug，共享工作树）

- 表格行模型：`FlashDeviceStatusEntry` 新增 `Key` 分组键（`FlashDeviceStatusRowKeys`：`stage`/`chip`/`flashid`/`capability`/`storage`/`storage-probe`），视图按行绘制：空 `Name` 的取值行只在值列写内容，因此一个存储介质的容量、擦除块、页大小、闪存厂商各占一行而不是挤在同一行。
- 不再输出「不存在」：`AppendRkFlashRows` 遍历官方 12 介质表时，读回掩码不指向该介质的一律静默跳过（与日志侧 `ProbeRkStorages` 一致），表格只列设备真正应答的介质；当前介质优先成组输出，其余存在介质跟在后面，介质名与选择框同一编号（如 `5. SPINOR`）。`Flash_RkStorageProbeAbsent` 现已无引用，保留在键集与两份 resx 中。
- 几何格式化：`FlashViewModel.DescribeRkFlashGeometry` 改为一项一行，改为带占位符的完整文案键 `Flash_RkStorageSize`/`Flash_RkEraseBlockSize`/`Flash_RkPageSizeBytes`/`Flash_RkFlashVendor`（替换原 `Flash_RkEraseBlock`/`Flash_RkPageSize`/`Flash_RkFlashManufacturer` 三个标签键），设备未上报几何时由新键 `Flash_RkStorageNoGeometry` 说明。`FlashViewModel.Rockchip.cs` 的存储探测日志同样在介质行下逐行展开几何。
- 视图：`FlashView.axaml` 的设备状态表格去掉固定 `RowHeight` 与横向省略号，改为两列（标签 / 取值）按内容自动换行、行高自适应，`MaxHeight` 168→260；`OnDeviceStatusGridSizeChanged` 与失效的 `LoadingRow` 处理一并移除，表格不再在尺寸变化时改写列宽。
- 定向测试：新增 `FlashDeviceStatusGeometryTests` 2 项（SPINOR 四行几何、无块几何只报容量）通过；`UotanStudio.Tests.Flash` 命名空间 96 通过/0 失败，`LocaleDictionaryTests` 6 通过/0 失败（键集、双语键等同与 resx 索引一致）。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format UotanStudio.slnx whitespace/style --verify-no-changes` 零报告。
- 环境边界：本机 `UotanStudio.Tests\bin` 被既有 `UotanStudio.Tests` 宿主（MTP server 模式，PID 每轮不同）镜像锁占用，`UotanStudio.Tests` 无法就地重建；测试改用 `-p:OutputPath=bin/verify3/` 临时输出目录执行后已删除。`dotnet build UotanStudio.slnx` 因此在本机末一轮报 `MSB3027/MSB3021`（文件被该宿主锁定，非编译错误）；`UotanStudio/UotanStudio.csproj` 单独构建 0 警告 0 错误，临时输出目录的全解决方案编译（含全部库与测试）0 错误。
- 既有失败（非本次改动）：`UotanStudio.Tests.Architecture` 16 项中 2 项失败——`HostLayeringGuardTests.Views_DoNotImportApplicationServiceNamespacesOutsideTheFrozenAllowList` 报 `Views/Compare/CompareView.Transfer.cs` 导入 `UotanStudio.Services.Devices`（冻结白名单里只登记了 `UotanStudio.Services.Projects`），`FormatProbeBoundaryGuardTests.HostSources_DoNotCarryFormatMagicConstants` 报 `Services/Flash/FlashService.cs:1733` 的 `EFI PART` 字面量；两处均在本次未触碰的文件里。
- 未验证：真机上 usbplug 模式读取设备信息后的实际表格观感（需接瑞芯微板目视），本机未启动 GUI，未做窗口键鼠实测。

## 设备引导发送后的选择保持实测（2026-11-08，Windows x64，Debug，共享工作树）

- 根因：`DeviceViewModel.RefreshDevicesCoreAsync` 在每次扫描后按扫描前的 `SelectedDevice.Key` 找回选中项，找不到就回退 `Devices[0]`。瑞芯微 maskrom → usbplug 切换会改写上报序列号与操作系统容器身份，旧端点 `GroupId` 不再匹配，于是选择落到列表首位——正是无关设备。
- 修复：新增 `DeviceViewModel.BeginSelectionHold()`，在发送引导前开启静态等待（`SelectionHoldMilliseconds`，30 秒，与 `RkdlConstants.LoaderRelinkTimeoutMs` 同量级）。等待保存当时的端点键集合作为基线，此后每次扫描若选中的端点消失：基线内唯一新增端点被认作替代节点并接管选择；出现多个新增端点或尚无新增时保持原选择，不回退到其他设备；等待期满仍无替代端点才按原逻辑回退，节点真正掉线（无等待、无新增）的行为不变。
- 接线：`FlashViewModel.DownloadRkLoaderAsync` 在 `RunRkdlLoaderTransitionAsync` 之前调用 `_deviceViewModel?.BeginSelectionHold()`，覆盖「加载引导」与瑞芯微二级分类两条入口，因为它们共用这一条 loader 传输通道。用户主动点选设备（`SelectDevice`）会结束等待，避免保持卡住真实的手动切换。
- 时钟：`DeviceViewModel` 新增可选 `TimeProvider` 构造参数（默认 `TimeProvider.System`），使等待窗口可在测试中确定性推进。
- 定向测试：新增 `DeviceSelectionHoldTests` 8 项通过/0 失败，覆盖端点仍在、无等待时回退、等待中保持、唯一替代端点接管、多个新增继续等待、超时回退、手动点选结束等待、无选中项选首个；回归 `DeviceLinkDisplayTests` 33 通过/0 失败。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误；三个触碰文件的 `dotnet format` `whitespace` 与 `style` 校验零报告。
- 环境边界：本机 `UotanStudio.Tests\bin` 被既有 `UotanStudio.Tests` 宿主（PID 15804）镜像锁占用，测试改用 `-p:OutputPath=bin/selection-hold-check/` 临时输出目录执行后已删除；并行会话的未跟踪文件 `UotanStudio.Tests/Protocols/RkdlMaintenanceAndDevicesTests.cs` 有 `CS1503` 编译错误（非本次改动），定向测试经一次性 props 排除该文件完成，未改动该文件。`UotanStudio.Tests.Devices` 命名空间全量 409 项中 `HdcServerTransferTests.Install_HapPackage_StreamsAppCheckAndReturnsResult` 30 秒超时失败，属既有 HDC 传输测试的环境问题，与本改动无关。
- 未验证：真机上 maskrom → usbplug 切换期间设备列表选中项不跳动的实际观感，需接瑞芯微板发送引导程序确认；本机未启动 GUI 目视。

## 十六进制对比工具栏实测（2026-11-02，Windows x64）

- 二进制对比直接显示十六进制内容；工具栏没有「十六进制原始数据」切换按钮，宿主不保留该按钮的命令、可见性属性、元数据视图枚举或专用中英资源键。独立文件浏览所用的元数据解析服务保持可用。
- 构建：`dotnet build UotanStudio.slnx -c Debug --no-restore --nologo -p:OutputPath=bin/hex-compare-check/` 成功，0 错误，仅两条既有 Rockchip 测试 CS8604。全解决方案 `dotnet format` 的 `whitespace` 与 `style` 检查、`git diff --check` 均通过。
- 回归：`dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build -c Debug -p:OutputPath=bin/hex-compare-check/ --filter "FullyQualifiedName~CompareDocumentColumnsTests.Hex|FullyQualifiedName~LocaleDictionaryTests" --minimum-expected-tests 12 --timeout 2m`：12 通过、0 失败、0 跳过。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，仅有既有 Satori GC 实验提示与第三方依赖的 IL2026/IL2104/IL3053；没有本次改动新增的裁剪/AOT 警告。发布后 `dotnet restore UotanStudio.slnx -p:Configuration=Debug` 成功。
- 未验证边界：没有重新运行完整测试集或进行桌面窗口键鼠实测；目录测试的 UI Dispatcher 阻塞及既有架构白名单缺口见下节。

## 多列文件对比的十六进制编辑实测（2026-11-01，Windows x64）

- 对比两侧复用独立十六进制编辑器的 `HexViewer`，支持十六进制/文本列覆盖编辑、原有复制粘贴与偏移跳转；固定 16 字节行、差异字节高亮、同步滚动、上一处/下一处差异以及每侧保存按钮和 Ctrl+S。修改范围增量更新差异计数；二进制对比直接使用十六进制视图，不提供元数据切换入口。保存采用快照、同目录临时文件和原子替换，同时提交的两侧保存请求依次处理。
- 内存编辑上限为两侧文件合计 256 MiB；超限保留按需读取的只读行集合，并显示中英本地化提示。编辑沿用共享控件的覆盖语义，不插入或删除字节。
- 构建：`dotnet build UotanStudio.slnx -c Debug --no-restore --nologo -p:OutputPath=bin/hex-compare-check/` 成功；默认桌面输出目录被运行中的程序占用，验证使用各项目独立 bin 子目录。仅有 `DeviceControllerRockchipReconnectionTests.cs:150/159` 的两条既有 CS8604。
- 格式：全解决方案 `dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes` 与 `style` 检查通过；最后调整的文件再次定向检查通过；`git diff --check` 通过。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功。仅报告已有的 Satori GC 实验提示、第三方 Ursa/Dock/DataGrid/LTRData/YamlDotNet/Common.Logging 的 IL2104/IL3053、Avalonia XAML 反射绑定与命令 trampoline 的 IL2026，以及 Makaretu.Dns 的 Linux Tmds.LibC 说明；没有指向本次新增代码的裁剪/AOT 警告。发布后已执行 `dotnet restore UotanStudio.slnx -p:Configuration=Debug` 恢复调试所需的还原资产。
- 最终定向回归：50 项，49 通过、1 失败、0 跳过。新增 6 项覆盖独立保存、同时保存、保存失败保留数据、单侧文件、超限只读回退与差异区间合并/拆分；这 6 项及文本编辑、本地化检查均通过。唯一失败为 `HostLayeringGuardTests.Views_DoNotImportApplicationServiceNamespacesOutsideTheFrozenAllowList`：HEAD 中 `CompareView.Transfer.cs` 已导入 `UotanStudio.Services.Devices`，冻结白名单仅列 `UotanStudio.Services.Projects`，本次未改动这两个文件。
- 回归命令：`dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build -c Debug -p:OutputPath=bin/hex-compare-check/ --filter "FullyQualifiedName~CompareDocumentColumnsTests.Hex|FullyQualifiedName~CompareDocumentColumnsTests.TextComparison|FullyQualifiedName~CompareDocumentColumnsTests.BrowsingTextFile|FullyQualifiedName~ComparisonServiceTests|FullyQualifiedName~FolderCompareServiceTests|FullyQualifiedName~LocaleDictionaryTests|FullyQualifiedName~UotanStudio.Tests.Architecture" --minimum-expected-tests 20 --timeout 2m`。
- 未验证边界：完整测试集未运行；Compare 命名空间测试在目录浏览用例等待现有 `CompareTreeNodeViewModel.EnsureChildrenLoadedAsync` 的 UI Dispatcher 时阻塞（测试宿主没有 UI 消息循环），已结束本次阻塞进程并清理对应 TEMP 夹具。未执行桌面窗口的键鼠交互、剪贴板及视觉实测。

## 设备列表 RK3588 芯片名显示修正实测（2026-11-07，Windows x64，Debug，共享工作树）

- 现象对齐（IDA，RKDevTool v3.37，函数 `sub_4197E0`「读取芯片信息」）：官方工具对 `RKU_ReadChipInfo` 返回的前 4 字节只做 `Chip Tag:\t%02X %02X %02X %02X` 的逐字节十六进制打印，命中旧表才追加 `-RK27/-RK32/…` 一类家族标签，否则按线上字节序拼出四字符（`-RK`/`K` + 各字节）仅用于 `Image Chip Flag` 映像芯片比对；该路径没有把标签当型号显示的逻辑，因此 RK3588 的 `8853` 不会被 RKDevTool 当作型号。
- 代码改动：`RkChipInfo.GetChipName` 增加 `0x38383533`（标签 "8853"）→ `RK3588` 的映射，与已有的 `0x33353838` 同列；`ChipName` 与 `Tag` 的 XML 注释写清 `Tag` 是线上字节序，显示型号一律走 `ChipName`。
- 定向测试：`RkdlModeClassificationTests` 41 通过/0 失败（新增 `0x33353838`→RK3588、`0x38383533`→RK3588 两条 `GetChipName` 用例）。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj --no-restore --nologo -c Debug` 0 警告/0 错误；`UotanStudio.Comm` 与 `UotanStudio.Tests` 的 `dotnet format whitespace`/`style` `--verify-no-changes`（`--include` 两个触碰文件）零报告。
- 未验证：真实 RK3588 设备上的列表行与「读取芯片信息」输出未复测（需接板）；`UotanStudio.Tests` 构建另有两条既有 `CS8604`（`DeviceControllerRockchipReconnectionTests.cs:150/159`），本轮未改动该文件。

## 瑞芯微阶段命名按 Rockusb wiki 更正实测（2026-11-01，Windows x64，Debug，共享工作树）

- 依据 [Rockusb wiki](https://opensource.rock-chips.com/wiki_Rockusb)：Boot ROM 阶段叫 maskrom（Bootrom mode 别名），`db` 下载 usbplug 固件后 rockusb 驱动运行、DRAM 已初始化的阶段叫 usbplug mode；miniLoader/U-Boot rockusb 模式是 loader 的两种来源，但 USB 描述符层面同为一个已运行 rockusb 驱动的阶段。
- 设备扫描状态：`DeviceMonitorService.Discovery.RkdlState` 的 loader 档改名为 `usbplug`，端点排序档同步；maskrom 与 bootrom 回退档不变。
- 设备列表徽标：`DeviceInfo.RkdlBadge` 新增，按状态显示 `RKDL · MaskROM` / `RKDL · USBPlug` / `RKDL · BootROM`（徽章区分大小写），其他状态保持 `RKDL`；上报序列号为厂商阶段标记 `ROCKCHIP` 时即使描述符阶段回退也强制显示 `RKDL · USBPlug`；DeviceView 列表与详情的两处 RKDL 徽标改绑该属性。
- 烧录页文本：`Flash_LoadBootRkDone`（EN/CN）改为「设备已进入 usbplug 模式」，`DeviceMonitorService.Transactions` 的重枚举超时消息与设置页驱动覆盖说明（`DriverPackage_Rockchip_Coverage`）同步改用 usbplug 表述。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Debug` 0 警告/0 错误；`DeviceLinkDisplayTests` 33 通过/0 失败（新增 `RkdlBadge` 10 例，含大小写不敏感匹配与 ROCKCHIP 序列号标记）；`RkdlModeClassificationTests` 39 通过/0 失败；`LocaleDictionaryTests` 6 通过/0 失败。
- 未验证：真机上 maskrom → usbplug 徽标切换的实际观感（需 Orangepi 5b 重新进入 maskrom 后发送引导程序确认）。
## 完整 CI 发布门禁实测（2026-11-01，Windows x64，共享工作树）

- 门禁顺序与结果：`dotnet restore UotanStudio.slnx -p:Configuration=Debug` 成功；`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误（2 个既有 CS8604 位于 `UotanStudio.Tests/Devices/DeviceControllerRockchipReconnectionTests.cs`，非本次改动文件）；`dotnet format UotanStudio.slnx whitespace --verify-no-changes` 通过；`dotnet format UotanStudio.slnx style --verify-no-changes` 通过；`dotnet test --solution UotanStudio.slnx --no-build -c Debug` 8224 总计/8202 通过/0 失败/22 跳过（全部为环境门控：`UOTAN_HDC_MIRROR_DIRECT`、`UOTAN_EROFS_CORPUS` 等未设置），耗时 1m38s；`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功输出到 `UotanStudio.Desktop/bin/Release/net10.0/win-x64/publish/`。
- 顺带修复 whitespace 门禁的两处既有 FINALNEWLINE：`UotanStudio.Tests/Devices/RkdlDeviceE2ETests.cs` 与 `UotanStudio/Views/UiWindowService.cs` 删除文件末尾多余换行（`.editorconfig` 规定 `insert_final_newline = false`）。
- AOT 发布警告全部来自第三方依赖与 Avalonia 生成的 XAML trampoline：IL2104（LTRData.Extensions、Common.Logging）、IL3053（YamlDotNet 18.1.0）、IL2026（`CompiledAvaloniaXaml` Dock.Model trampoline 调用 `TypeUtilities.TryConvert`）与 Makaretu.Dns 的 Tmds.LibC 仅 Linux 提示；无本次改动文件（FlashViewModel、FlashDeviceUiPolicy、FlashView、Locale）新增的裁剪警告。
## 瑞芯微发送引导入口合并与槽位切换门控实测（2026-11-01，Windows x64，Debug，共享工作树）

- 链接 bootrom 模式 RKDL 设备（名为 ROCKCHIP）时「切换槽位」下拉按钮禁用：原 maskrom 专属清单 `RockchipMaskromControls` 改为 `RockchipUnsupportedControls`（`SwitchSlotButton`），三参 `IsAvailable` 对 RKDL 模式先拒绝该清单，再叠加 maskrom/loader 阶段拆分。
- 瑞芯微二级菜单删除「发送引导」按钮：`RkSendBootButton` 的 XAML 定义、点击接线、`FlashDeviceUiPolicy.Entries` 条目、`UpdateModeVisibility` 门控行与 `OnRkSendBootClick` 处理器一并移除，`OnLoadBootClick` 的 RKDL 路径成为唯一传输 loader 的入口。
- 通用「加载引导」按钮文本改为「发送引导程序」（EN "Send loader programmer"），不再按协议切换标签，`Flash_RkSendBoot` 资源与键集登记删除；`Flash_LoadBootNoDevice`、`Flash_LoadBootRkApplying`、`Flash_LoadBootUnsupported` 与 `FlashService` 的 maskrom 拒绝消息同步改用「发送引导程序」表述。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Debug` 0 警告/0 错误；`UotanStudio.Tests.Flash.FlashDeviceUiPolicyTests` 12 通过/0 失败；`UotanStudio.Tests.Services.LocaleDictionaryTests` 6 通过/0 失败；`dotnet format style` 干净，`whitespace` 仅余 2 个既有 FINALNEWLINE（`RkdlDeviceE2ETests.cs(48)`、`UiWindowService.cs(138)`，非本次改动）。
- 未验证：真机上 RKDL 设备选中态的「切换槽位」禁用与「发送引导程序」按钮观感（需连接 ROCKCHIP bootrom 设备人工确认）。
## 重捕获位置链随旧设备节点消失的修复实测（2026-11-01，Windows x64，Debug，共享工作树，Orangepi 5b）

- 现象：超时诊断（同协议端点清单）显示 ROCKCHIP loader 候选已在快照中（serial=ROCKCHIP，instance=USB\VID_2207&PID_350B\ROCKCHIP）但匹配仍失败。
- 根因：位置比较在等待期间从 `previousInfo` 重读 PnP 集线器链，而旧 maskrom 设备节点在阶段切换后已消失，`UsbLocationChain.TryRead(旧实例 ID)` 解析失败 → 旧链为 null；替代端点有真实链（如 1-x-x），两侧链不同时为 null 与非 null → 位置匹配失败；序列号尾部回退因 maskrom 数字串 ≠ ROCKCHIP 同样失败。
- 修复：`RunRkdlOnLeaseAsync` 租约存活期间经 `DeviceController.CaptureUsbLocationChain` 捕获旧节点集线器链，经 `RkdlLoaderTransitionOffer.StickyLocationChain` 传入 `WaitForUsbReconnectAsync(capturedLocationChain)`；位置比较优先使用捕获链。等待入口处旧节点已消失时的就地捕获仅作兜底。超时消息现在附带已捕获链与超时时同协议端点证据。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Debug` 0 错误；`UotanStudio.Tests.Devices` 命名空间 387 通过/0 失败/3 跳过（bin2 输出路径，测后删除）；`dotnet format whitespace` 仅余 2 个既有 FINALNEWLINE（`RkdlDeviceE2ETests.cs(48)`、`UiWindowService.cs(138)`）。
- 未验证：真机 loader 下载 → 重枚举 → 重捕获端到端（需板子重进 maskrom 后重试一次下载确认）。

## 引导发送后重枚举重捕获失败根因分析与修复实测（2026-11-01，Windows x64，Debug，共享工作树，Orangepi 5b）

- 现象：loader 传输与设备模式切换成功（设备管理器出现 ROCKCHIP bootrom rkdl 设备），但重捕获等待 30 秒超时，页面保持 busy 并报「设备未以 Rockchip loader 模式重枚举」。路径：`DownloadRkLoaderAsync` → `RunRkdlLoaderTransitionAsync` → `WaitForUsbReconnectAsync(RockchipLoader)` → 快照轮询（100ms）按位置链 + loader 阶段证据匹配替代端点。
- 根因：loader 阶段设备由 `rockusb.sys` 服务，发现快照无法用 WinUSB 探测读其 iSerial；序列号只能来自实例 ID 尾部或缓存/窥视回退。当实例尾部是位置串（`6&…&2`）或序列号解析失败时，快照的 `SerialNumber` 与 `BcdUsb`（hub 读取在 ASMedia 等集线器上可能失败）同时为空，`IsRockchipLoaderStage` 两条证据全失 → 候选被拒 → 超时。设备管理器显示的 ROCKCHIP 恰恰证明实例 ID 尾部已携带该名。
- 修复（`DeviceController.Reconnection.cs`）：`IsRockchipLoaderStage` 增加第三条证据——设备实例 ID 尾部为 `ROCKCHIP`（无 `&`、不区分大小写），Windows 把上报的 iSerial 编入实例路径，rockusb 设备即使序列号解析失败也仍在该处携带它；`bcdUSB` bit 0 与 `SerialNumber` 两条证据保持不变。
- 诊断（下一轮真机若再失败可定位）：等待循环对消失后出现的同协议同后端候选，逐个以 `CommTrace.Debug("reconnect", …)` 记录两侧的位置链、序列号、bcdUSB 与实例 ID，直接点名被拒绝的证据。
- 顺带完成上一轮遗留：`LoadBootButton` 文本按协议切换——RKDL 设备显示 `Flash_RkSendBoot`（发送引导），其他协议保持 `Flash_LoadBoot`（加载引导），AutomationProperties.SetName 同步（Avalonia 12 的 `AutomationProperties` 静态 API）。功能上 RKDL 的「加载引导/发送引导」均已实现完整发送 + 重枚举等待。
- 门禁：Comm/宿主/Desktop 构建 0 错误；`DeviceControllerRockchipReconnectionTests` 14/14（新增实例尾部证据 3 例 + 完整证据匹配 1 例）；Devices.Rkdl + Protocols + Flash + Locale 304 总数 / 302 通过 / 2 硬件跳过（中途一次失败为 %TEMP% 清理瞬时文件锁，复跑通过）；`dotnet format` 仅剩既有 2 条 FINALNEWLINE。
- 未验证边界：实例尾部证据路径对真实 rockusb 设备的覆盖待真机复测确认；若真机仍超时，全局日志的 `reconnect` 诊断行会给出被拒原因。

## 加载引导恢复完整发送流程实测（2026-11-01，Windows x64，Debug，共享工作树）

- 修复：通用「加载引导」的 RKDL 路径恢复为完整流程——校验 loader 行、应用、经 `DownloadRkLoaderAsync` 传输到设备并等待 ROCKCHIP loader 阶段重枚举（与官方「下载引导」一致）；上一轮的“只应用不发送”拆分被用户实测否决后撤销。专用的「发送引导」按钮保留，仍按 loader 阶段门控（ROCKCHIP 序列号设备禁用）。
- 上一轮引入的 `Flash_LoadBootRkApplied` 键随流程合并失去调用点，已从 EN/CN resx 与 LocaleKeySets 同步移除（并保持 key 集合一致）。
- 门禁：`UotanStudio.csproj`、Tests、`UotanStudio.Desktop.csproj` 全部构建 0 错误（Desktop 输出目录锁已随用户停止调试会话解除）；Flash + LocaleDictionaryTests 64/64 通过；`dotnet format` 仅剩既有 `UiWindowService.cs` FINALNEWLINE。
- 未验证边界：真机上「加载引导 → ROCKCHIP 重枚举 → 存储命令」的完整链路仍待用户复测。

## 加载引导应用流程去重与完成提示实测（2026-11-01，Windows x64，Debug，共享工作树）

- 修复：通用「加载引导」（RKDL 路径）与「发送引导」在校验通过后都把已算出的结论直接发布到 loader 行（`UpdateSpecialFileRowStatus`/`UpdateRkLoaderRowStatusAsync` 新增 `knownValid` 通道），消除同一文件被 `ValidateRkLoaderFileAsync` 读两遍并写两条相同「瑞芯微 loader 有效」日志的问题；勾选行与导入配置时的重查路径保持原样（那里的重复是真实的重新检查）。
- 「加载引导」应用完成后输出新键 `Flash_LoadBootRkApplied`（EN/CN 同步，LocaleKeySets 登记；顺带修复了 `Flash_RkSendBoot` 在键集中缺失缩进的行），使流程可见地结束并指向「发送引导」；校验失败时不应用、行状态显示失败结论。
- 门禁：`UotanStudio.csproj` 与 Tests 构建 0 错误；`FlashGlobalLogRoutingTests` 全类通过（此前一次 `ImportRkConfigAsync…` 因 `%TEMP%` 清理时 `MiniLoaderAll.bin` 仍被占用的瞬时文件锁失败，单独复跑通过）；`dotnet format` 仅剩既有 `UiWindowService.cs` FINALNEWLINE。
- 未验证边界：Desktop 输出目录本轮被正在运行的调试实例（`UotanStudio.Desktop` + VS 调试适配器）锁定，`UotanStudio.Desktop.csproj` 的最终复制与真机复测需在用户停止调试会话后进行。

## 存储切换与能力位按官方实现修正实测（2026-11-01，Windows x64，Debug，共享工作树，Orangepi 5b）

- 官方实现还原（IDA，RKDevTool）：CHANGE_STORAGE 发送的是介质位位置而不是选择框序号——位表 FLASH=0、EMMC=1、SD=2、SD1=3、SPINOR=9、SPINAND=8、RAM=6、USB=7、SATA=10、PCIE=11、UFS=12、RVD=13（12 项与官方下拉一致）；READ_STORAGE 返回 4 字节小端掩码，官方以 `mask == 1 << wireBit` 整体相等判定当前介质。此前发送序号 5（SPINOR 实际位 9）表现为 CSW 成功但介质不变，且 `TrailingZeroCount`+序号解码把 EMMC 掩码 0x0002 误报为 FLASH，与本轮真机日志一致。
- `RkdlStorage`（Comm，公开）：官方顺序的 12 个介质名与线上位表、`IndexOfMask`/`DescribeMask` 精确匹配辅助；`ReadStorageMask()` 取代 `ReadStorage()` 返回原始掩码；`RkCapability.Describe()` 按官方 `GetCapabilityProc` 字符串表输出每一位 `名称=Enable/Disable`（首字节 DirectLBA、VendorStorage、First4M、ReadLBA、ReadComLog、ReadIDBConfig、ReadSecureMode，次字节 NewIDB、SwitchStorage、LBAparity、ReadOTPchip、SwitchUsb3，未命名位报告 `BitN`）；`RkChipInfo`/`RkFlashInfo` 与 `ReadChipInfo`/`ReadFlashInfo` 转公开。
- 宿主：选择框改为官方 12 项 `1..12`（条目序号仅作显示，发送走 `RkdlStorage.WireBits`）；「读取芯片信息」输出能力字逐位描述与 `Flash_RkChipName` 芯片型号（`RkChipInfo.ChipName` 映射，如 0x350A → RK3588S 表项或 ASCII 标签）；「获取当前存储」在掩码之外追加 `READ_FLASH_INFO` 几何（容量、擦除块、页大小、闪存厂商）；「切换存储」发送后回读掩码校验，不符时如实报告实际介质（与官方 CheckDeviceStorage 二次 GetDeviceStorage 一致）。
- 门控修复：`UpdateModeVisibility` 对 `RkSendBootButton` 的覆盖改为 `!_busy && Rkdl && !IsRockchipLoaderStageDevice`，ROCKCHIP 序列号的 loader 阶段设备不再保留可用的「发送引导」。
- 定向测试：`RkdlStorageTableTests` 19/19（官方位表、12 个单掩码映射、非法掩码、能力位描述）；Protocols + Devices.Rkdl + Locale 合计 246/244 通过/2 跳过；Flash 58/58。`dotnet format` 仅剩既有 2 条 FINALNEWLINE（`RkdlDeviceE2ETests.cs`、`UiWindowService.cs`）。Desktop Debug 构建 0 警告 0 错误。
- 未验证边界：SPINOR/SPINAND/SATA/PCIE/UFS/RVD 六个介质的真实切换只能由对应硬件确认（本机只有 eMMC/SD 可对照官方工具）；READ_STORAGE 掩码多位置位（如 0x0003）的设备语义未在官方工具中观测到，按不匹配原样报告。

## RKDL 发送引导独立入口与 ROCKCHIP 阶段门控实测（2026-11-01，Windows x64，Debug，共享工作树）

- 重枚举匹配：`UsbReconnectIdentityPolicy` 新增 `RockchipLoader` 策略——替代端点按不变物理位置（设备实例 ID 背后的 Windows 集线器链，回退后端端口路径或 PnP 位置尾部）加 loader 阶段证据（`bcdUSB` bit 0 置位或厂商 `ROCKCHIP` 序列号）匹配，容器 ID 有意不比较（阶段切换本就会改写序列号与容器身份）。`RunRkdlLoaderTransitionAsync` 主路径改用该策略，证据缺失时回退 Container 策略。真机日志确认：maskrom 序列号为数字、loader 阶段序列号为 `ROCKCHIP`。
- 通用「加载引导」按钮的 RKDL 路径不再发送任何内容：`ChooseRkLoaderAsync` 只校验并应用 loader 行；新增瑞芯微分类下专用的「发送引导」按钮（`RkSendBootButton`，`Flash_RkSendBoot` 中英资源 + LocaleKeySets），`OnRkSendBootClick` 是唯一会传输 loader 的 RKDL 路径（校验、应用、下载并等待 loader 阶段重枚举）。
- 阶段门控取代 loader 行门控：`FlashDeviceUiPolicy.IsAvailable` 的第二维度改为 `isRockchipLoaderStage`（设备序列号为 `ROCKCHIP`）。maskrom 阶段只启用「发送引导」；loader 阶段禁用「发送引导」，启用烧录分区、回读分区、添加 root、RootOptionsPanel 与瑞芯微二级分类（RkPane）。
- 其余 RKDL 引导发送逻辑全部剔除：`FlashService.FlashRkdlAsync`/`EraseRkdlAsync` 改为单阶段——要求 loader 阶段设备，遇到 maskrom 端点直接失败并提示先用发送引导；`RunRkReadInfoAsync` 不再经 loader 迁移下载 loader，maskrom 设备只读信息并提示需要 loader。
- 定向测试：`Flash` + `Protocols` + `Devices.Rkdl` + `LocaleDictionaryTests` 合计 285 总数 / 283 通过 / 2 跳过（真机门控）；新增 `DeviceControllerRockchipReconnectionTests` 10/10（位置链回退、跨容器匹配、其他集线器拒绝、`bcdUSB`/序列号阶段证据）与 `FlashDeviceUiPolicyTests` 的阶段分拆用例。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 只剩既有的 `UotanStudio.Tests/Devices/RkdlDeviceE2ETests.cs` 与 `UotanStudio/Views/UiWindowService.cs` 两条 FINALNEWLINE。定向测试经 `-p:OutputPath` 临时输出目录执行后已删除（本机 Tests bin 仍被既有的 `--server dotnettest` 宿主镜像锁占用）。
- 未验证边界：真机上「发送引导 → ROCKCHIP 重枚举 → 烧录分区/回读/root 解锁」的完整链路待用户在烧录机复测；`bcdUSB` bit 0 证据路径未在真机观测到（rockusb.sys 端点通常无描述符，实际生效的是 `ROCKCHIP` 序列号证据）。

## Loader 下载真机 121 诊断与可操作报错实测（2026-11-01，Windows x64，Debug，共享工作树）

- 官方逐字节对齐复核：`Boot_VendorRequest`（`sub_5E5380`，下载 Boot 路径）对负载无条件计算 CRC-16/CCITT-FALSE（步进函数 `sub_5C6D00`，检查值 `0x29B1`），按 4096 字节分块、每块前 `Sleep(50)`、要求 `BytesReturned == chunk`；条目数据经 `sub_5C68E0` 原样 `memmove`，无 RC4 变换。本仓库 `RkdlSession.DeviceRequestAsync` 的封帧、CRC 与分块与官方完全一致；官方日志同机同样出现过 `Total(2050),Sended(0),crc(1898),err(121)`。
- 已删除无调用方的 `WarmUpForLoaderDownload`（官方下载 Boot 前不执行批量预热）。
- vendor 请求失败可操作化：`DeviceRequestAsync` 把控制传输的 `Win32Exception`/`NotSupportedException`/`ObjectDisposedException` 与短写包装为带「板子需重新进入 Maskrom 再重试」指引的 `RkdlException`（保留内层异常）；`RunRkdlLoaderTransitionAsync` 的重枚举超时错误追加同一指引。
- 定向测试：`UotanStudio.Tests.Protocols` 209/209；`FullyQualifiedName~UotanStudio.Tests.Devices.Rkdl` 10 通过 / 2 跳过（真机门控）。
- 门禁：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 只剩既有的 `UotanStudio.Tests/Devices/RkdlDeviceE2ETests.cs` 与 `UotanStudio/Views/UiWindowService.cs` 两条 FINALNEWLINE（本轮新出现的 `RkdlLoaderProbeE2ETests.cs` 尾部换行已修复，并把 `RkdlModeClassificationTests` 的 xUnit1051 改为 `TestContext.Current.CancellationToken`）。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功；警告只有既有的第三方 IL2026/IL2104/IL3053 集合（Ursa、Dock.Avalonia、DataGrid、Semi.Avalonia.Dock、Iciclecreek、Satori GC 实验提示与 Avalonia XAML 反射绑定），本轮改动文件无新增警告。
- 环境边界：本机 `UotanStudio.Tests\bin` 被两个 `--server dotnettest` 的既有 `UotanStudio.Tests.exe` 宿主（PID 24864/15304，Stop-Process/taskkill/WMI 均无法终止）以镜像锁占用，`dotnet test` 复制报 MSB3027；定向测试经 `-p:OutputPath` 临时输出目录执行后已删除。真机复测仍待用户在烧录机上执行：先让板子重新进入 Maskrom（重新上电），再跑加载引导。

## RKDL 与 rkdeveloptool 逐方法对齐实测（2026-11-01，Windows x64，Debug，共享工作树）

- ID block 与 CRC：新增 `UotanStudio.Hash.Crc16Xmodem`（高位优先 0x1021、初值 0x0000、无末异或，检查值 `0x31C3`），与 loader 下载帧用的 `Crc16CcittFalse`（初值 0xFFFF）保持两个类型；新增 `Rk28IdBlock`，按 `RK28_IDB_SEC0..3` 与 `MakeIDBlockData` 构建旧式四头部扇区布局与 head/data/code 布局，`uiBootCodeCrc` 仍走非反射的 `RockchipCrc32`；`RkdlRc4` 转写厂商 `P_RC4`（16 字节密钥按 `0x0f` 掩码循环），旧式布局无条件加密扇区 0/2/3，`uiRc4Flag` 非零时加密 loader 数据与代码，按 512 字节扇区重置状态。
- loader 升级：新增 `RkdlSession.LoaderUpgrade`，要求 `FlashBoot` 与 `FlashData` 条目、`FlashHead` 可选（有 head 时要求 `capability[1] & 1`），把 ID block 写到 LBA 64，写入后不复位设备；`RkdlSession.Transfer` 增加 `maskromLoaderUpgrade` 通道，使 `EnsureBulkCommandAllowed` 只放行这一条 `WriteLba`。
- 稀疏回放：新增 `RkdlSession.Sparse`，解析并校验后先 `ERASE_LBA` 擦除目标范围，再按数据块流式 `WRITE_LBA`；don't-care 数据块只推进 LBA 而不写零，原始载荷经 `SparseWriteSource` 单向有界读取，数据块记录上限 `MaxSparseChunks`。
- 命令与超时：`READ_FLASH_INFO` 使用独立的 5000 毫秒预算；`ResetDevice` 只在 `RkdlResetSubCode.None` 下容忍缺失或不匹配的 CSW；`WaitForLoaderReadyAsync` 按 `CRKDevice::TestDevice` 判定就绪；`ReadLBA`/`ReadDataCommand`/`ReadCsw` 支持覆盖超时；Loader 模式拒绝 0x0471/0x0472 之外的 wIndex；填充包接受 0 或 1 字节短写。
- 分类与能力：`UsbProtocolClassifier.ClassifyBootrom` 先应用 MSC 排除表；`EraseAllFlash` 读不到能力时退化为 `RkCapability.None` 而不是抛错。
- 定向测试：`UotanStudio.Tests.Protocols` 209/209；`UotanStudio.Tests.Firmware` 4497 总数 / 4484 通过 / 13 跳过 / 0 失败。新增用例覆盖 ID block 逐字段与校验和位置、RC4 与类内转写交叉校验、稀疏字节源、模式分类与 CBW 线格式。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format UotanStudio.slnx style --no-restore --verify-no-changes` 零报告，`whitespace` 只剩既有的 `UotanStudio.Tests/Devices/RkdlDeviceE2ETests.cs` 与 `UotanStudio/Views/UiWindowService.cs` 两条 FINALNEWLINE。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物在 `UotanStudio.Desktop\bin\Release\net10.0\win-x64\publish\`；输出只有既有的 Ursa.Themes.Semi、Dock.Avalonia、Avalonia.Controls.DataGrid、Semi.Avalonia.Dock、LTRData.Extensions、YamlDotNet、Common.Logging、Iciclecreek 的 IL2026/IL2104/IL3053 与 Avalonia XAML 反射绑定警告，本轮改动文件无新增警告。
- `UotanStudio.Tests.Devices` 命名空间 372 总数 / 369 通过 / 2 跳过 / 1 失败：`AdbTlsTransportTests.UpgradeToTls_DeviceRebuildsItsCertificateEachHandshake_CompletesBothHandshakes` 在机器负载下以 15 秒握手超时报 `Received an unexpected EOF or 0 bytes from the transport stream`，单跑该类 6/6 通过；同一失败在 HEAD 的独立工作树（只带本轮 18 个改动文件）上同样复现，属负载敏感的既有用例，与本轮改动无关。
- 未验证边界：无瑞芯微真机，loader 升级、稀疏回放与 Mask ROM `WRITE_LBA` 放行只有单元测试证据；ID block 与 MCU 侧的真实握手、`ucRc4Flag` 非零的实机镜像、以及 LBA 64 写入后的设备行为均未在硬件上执行。

## 瑞芯微烧录页动作日志去重实测（2026-11-01，Windows x64，Debug，共享工作树）

- 重复来源一（同一结论被两处写）：`ApplyRkLoaderPath` 在指向 loader 时写 `Flash_RkLoaderDeclared`、为空时写 `Flash_RkLoaderMissing`，而导入配置与行内选文件两条路径随后又各自调用一次 loader 检查，检查本身对同一文件写出 `Flash_RkLoaderMissing` 或 `Flash_RkLoaderChecked`，于是"加载 Loader：… / 该配置引用的 Loader 不存在：… / 瑞芯微 loader 有效，…"各出现两遍。现在 `ApplyRkLoaderPath` 只写 `_rkLoaderPath` 并重发门控，结论统一由 `ValidateRkLoaderFileAsync` 写一次。
- 重复来源二（同一次点击检查两次）：`OnChoosePartitionImageClick` 先写 `entry.FilePath`，属性变化经 `OnEntryPropertyChanged` → `UpdateRowValidityText` 触发一次完整的 loader 检查，方法末尾再直接调用 `ValidateRkLoaderFileAsync` 一次，一次选择写两行相同结论。现在写行字段期间置 `_suppressSpecialFileRowCheck`，选择器自己对 loader 行检查一次并按结论决定是否 `ApplyRkLoaderPath`；parameter 行沿用 `UpdateSpecialFileRowStatus` 的同步检查。
- 行状态与日志一致：新增 `PublishRkLoaderStatus(row, valid)`，`UpdateRkLoaderRowStatusAsync`、行内选择器与异常分支都经它写 `Flash_RkLoaderValid`/`Flash_RkLoaderInvalidStatus`，行内文案与日志不再各写一份判断。
- 定向测试：`UotanStudio.Tests.Flash.FlashRockchipFileRowTests` 9/9（新增"缺失 loader 文件只报一行 `Flash_RkLoaderMissing`"与"有效 loader 只报一行 `Flash_RkLoaderChecked`"两条按 `LogText` 行计数断言的用例）；`UotanStudio.Tests.Flash` 命名空间 57/57。
- 门禁：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告（`UotanStudio.Desktop` 的 `bin\Debug\net10.0` 先后被正在运行的同名宿主进程锁定过一次 `MSB3026` 复制重试，该进程退出后重建通过）；触碰的四个文件 `dotnet format` 的 `whitespace` 与 `style` `--verify-no-changes` 零报告。
- 全量测试：8140 总数 / 8120 通过 / 20 跳过 / 0 失败（2m 41s）。
- 未验证边界：界面日志框的实际显示没有目视，去重结论只经视图模型层 `LogText` 行计数断言；NativeAOT 发布本轮未执行。全解 `--verify-no-changes` 仍被本轮未触碰的 `UotanStudio.Tests/Devices/RkdlDeviceE2ETests.cs` 与 `UotanStudio/Views/UiWindowService.cs` 的 FINALNEWLINE 报告挡住。

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
- RID 回落与 SDK 版本行（同日追加）：csproj 对 `riscv64`/`loongarch64` RID 显式 `UseSatoriGC=false`（Satori 无对应运行时包，发布回落默认 GC），其余 RID 保持默认开启；`NETCoreSdkVersion` 经 `AssemblyMetadata`（键 `DotNetSdkVersion`）写入桌面入口程序集，设置页「垃圾回收」区块新增「.NET SDK 版本」行，运行时读入口程序集元数据，缺失时回退 `RuntimeInformation.FrameworkDescription`。
- 设置页当前 GC 单行摘要（2026-10-31）：「垃圾回收」区块改为两行——「当前垃圾回收」单行摘要（回收器名 + 服务器/工作站形态 + 并发/非并发 + 延迟模式，经 `Settings_GcSummaryFormat` 本地化拼接）与「.NET SDK 版本」；原来的实现/服务器/并发/延迟模式四个平铺条目移除，语言切换只刷新 `CurrentGcText`。
- GC 生效形态探针（2026-10-31，独立最小工程验证检测逻辑）：framework-dependent 运行（IDE/`dotnet run`/`bin` 输出）加载本机 .NET 10.0.14 运行时，`GC.GetConfigurationVariables()` 不含 `SatoriGC`（32 变量，`IsServerGC=False`）；同一工程自包含发布后运行含 `SatoriGC`（33 变量，运行时报告 .NET 10.0.15-ci，`IsServerGC=True`）。Satori GC 仅存在于自包含发布产物，非自包含运行显示默认 GC 是如实报告，不视为缺陷。
- 未验证边界：设置页区块的界面布局只经编译绑定与 LocaleDictionaryTests 验证，没有人工目视；其余 RID 发布未执行；riscv64/loongarch64 的真实发布未执行（本机无对应交叉工具链），烘焙的 SDK 版本未在发布产物中实测；Satori GC 的延迟与吞吐特性未测量（基准工作负载已就绪）。

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