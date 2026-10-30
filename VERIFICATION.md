# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

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
- NativeAOT：同轮 `dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained` 成功，产物 `UotanStudio.Desktop.exe` 156,822,528 B；只报告第三方既有的 IL2026/IL2104/IL3053 警告，没有指向新增固件或宿主代码的裁剪/AOT 警告。
- 全量（2026-10-27）：7692 总数 / 7684 通过 / 8 跳过 / 0 失败，单轮 1m42s（`dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build -c Debug --minimum-expected-tests 6000`，共享工作树）。

## 酷派 CPB 解包实测（2026-10-27，Windows x64，Debug）

- `UotanStudio.Firmware/VendorFirmware/Coolpad/Cpb/` 新增只读格式处理器 `Coolpad CPB Package`（`.cpb`）：按 552 字节头（`CP` + 版本 1.7、6 个校正字、机型/硬件/版本/来源文本、镜像头结束位置、CRC16）与 76 字节镜像头（64 字节名称、偏移、长度、CRC16）解包；偏移与长度按头部校正字推导的密钥异或混淆，容器校验和按**解码后**的镜像头累加。参考实现为 `reference/MIO-KITCHEN-SOURCE/src/c_extension/cpb_file/cpbtool.c`。
- 密钥与校验和的两处非直觉行为按参考实现逐位复刻，并在用例里钉住：校正字之和的"除以 6"实际是 `0x66666667` 高半乘再右移一位（等价于除以 5），厂商默认校正字因此得到密钥 `0x00310000`（`DeriveKey_VendorCorrectionWords_ProducesTheDocumentedSeed`）；CRC-16 累加器不截断到 16 位，故头部与镜像头都用完整 32 位比较，同时接受低 16 位一致（对应 16 位寄存器产出方），`BitwiseCrc16` 用逐位移位独立复算这条低 16 位等价关系。
- 定向测试：`UotanStudio.Tests.Firmware.CpbTests` 18/18（头字段与镜像目录、掩码校验和、位级 CRC 等价、密钥常量、解包字节、负载损坏仍写出但报校验失败、头部校验和不一致拒解、截断范围拒解且不留暂存文件、条目筛选、不安全/重复/空名称净化、取消、版本字节判定、魔数拒绝、过短文件、`DetectDetailed` 强魔数、混淆变换自逆）。
- 魔数：`UotanStudio.Firmware.MagicGen/corpus/Magdir/uotan-firmware` 新增 `CP\x01\x07` 规则并重新生成 `CompiledMagicData.{g.cs,zst}`（24764 条规则 / 4206 条顶层 / blob 3,045,111 B → zstd 317,709 B）；`FileMagicTests` 34/34、`CompiledMagicCoverageTests` 5/5。
- 镜像文件管理与界面接线：`ImageInputService` 新增 `ImageFormatKind.Cpb`（标签 `cpb`，`ParseFormatLabel` 可回读，`ProbeFormat` 经已注册处理器与 `.cpb` 后缀双重识别）；`ImageInputServiceTests` 77/77（含 `DetectFormat_CoolpadCpbMagic_ReturnsCpb` 与标签往返）。固件信息视图经 `FirmwareField_{CpbVersion,Model,HardwareVersion,RomFrom,Mismatch}` 本地化（中英双份 resx + `LocaleKeySets`，`LocaleDictionaryTests` 6/6）；资源管理器把 `.cpb` 归入固件包图标，导入与新建工程的选取器加入 `*.cpb`。
- 架构门：`FirmwareArchitectureGuardTests` 12/12、`FirmwareNamespaceFolderTests` 2/2；全 Firmware 命名空间 4378 总数 / 4376 通过 / 2 跳过（未设 `UOTAN_EROFS_CORPUS`）/ 0 失败，单轮 41s；`UotanStudio.Tests.Services` 1799 总数 / 1798 通过 / 1 跳过 / 0 失败，单轮 2m11s；`UotanStudio.Tests.Architecture` 16/16。
- 门禁：`dotnet restore UotanStudio.slnx -p:Configuration=Debug` + `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 两项 `--verify-no-changes` 均零报告。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true --artifacts-path %TEMP%\uotan-aot-cpb` 成功，产物 `UotanStudio.Desktop.exe` 155,977,728 B；只报第三方既有警告（8×IL2104、4×IL3053，来源 Avalonia.Controls.DataGrid、Common.Logging、Dock.Avalonia、Iciclecreek.Avalonia.Terminal、LTRData.Extensions、Semi.Avalonia.Dock、Ursa、Ursa.Themes.Semi、YamlDotNet），没有任何指向 `UotanStudio.*` 的裁剪/AOT 警告；临时产物已删除。共享工作树里其他会话同时构建 Release 时，默认输出目录会以 `CS2012`（用户映射区域占用）失败在 `UotanStudio.Hash`/`Net`/`Media`/`Desktop` 等无关项目上，因此本轮用 `--artifacts-path` 隔离输出。
- 未验证边界：仓库内没有真实酷派 CPB 固件样本，上述解包正确性只由测试自建的容器覆盖（写入方按参考 C 的布局独立构造，不是生产代码的往返）；未与厂商工具产物做逐字节对照；固件信息视图与属性面板未启动 GUI 目视验证；未实现重打包，CPB 保持只读。

## iOS 支持实测（2026-10-26，Windows x64，Debug）

- 定向测试：`UotanStudio.Tests.Protocols` 107/107、`UotanStudio.Tests.Mirroring` 165 通过 + 1 跳过（`OhscrcpyHdcIntegrationTests` 需要 `UOTAN_HDC_MIRROR_DIRECT`）、`UotanStudio.Tests.Devices` 292/292、`UotanStudio.Tests.Services.LocaleDictionaryTests` 6/6。
- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误；Fastboot 测试文件原有的两条 `xUnit1051`/`xUnit1069` 已消除，`FastbootUdpHandshakeTests` 与 `FastbootRemoteProtocolTests` 把 `TestContext.Current.CancellationToken` 交给 `Task.Run`。
- Apple 协议族当前契约（均有对应用例覆盖）：
  - `BinaryPlist` 写入器的偏移表按表长度定位，读回不报 "offset table is inconsistent"；对象按 Apple 的子项在前、容器在后展平，根对象索引位于尾部。
  - `ApplePlist` 的 XML 路径先剥离普通 `<!DOCTYPE` 声明再解析，带内部子集的 DTD 仍然拒绝；字典/数组读取器按键值对消费，不重复读取取值元素。
  - 配对记录的 `ToPlist` 写入 `HostPrivateKey` 为 PEM 字符串，读取同时接受 DER 数据块与 PEM 字节形态的记录。
  - 配对证书链的可分辨名称使用 DER 空序列 `30 00`，`CertificateRequest` 可正常编码 TbsCertificate。
- AirPlay 接收端（`UotanStudio.Mirroring/AirPlay/`）：RTSP 握手（OPTIONS/ANNOUNCE/SETUP/RECORD/FLUSH/TEARDOWN/GET_PARAMETER/SET_PARAMETER）、mDNS 报文编解码、128 字节镜像头解复用、AES-128-CTR 与 FairPlay 解包骨架、交织与 UDP 媒体通道都有用例覆盖（`AirPlayReceiverSessionTests` 用回环 TCP 假客户端走完整握手并推帧）。
- AirPlay 未验证边界（不得写成已验证）：
  - 没有 iPhone 真机：mDNS 广播没有被 iOS 实际浏览过，UDP 数据端口没有被真实客户端推过流，帧率/延迟/音画同步没有任何测量。
  - 不随包提供 FairPlay 密钥表，`AirPlayOptions.FairPlayKeyPath` 未配置时加密流被明确拒绝（403）；配置后也只验证了 CBC 解包结构，没有设备 ekey 向量。
  - HAP `/pair-setup` 与 `/pair-verify` 未实现，返回 501；现代 iOS 依赖它完成配对，因此当前实现不能与真实设备完成投屏握手。
  - 宿主 AirPlay 入口只启动接收端并报告监听端口，没有接预览呈现；GPU/CPU 呈现路径未接入 AirPlay。
- iOS 隧道与直连 USB 未在任何硬件上验证。

## ADB 协议对照上游与实机验证（2026-10-26，Windows x64，Debug；真机 Wi-Fi）

- 对照对象：`reference/adb-aosp/`（AOSP `platform/packages/modules/adb`）的 `adb.h`、`adb.cpp`、`transport.cpp`、`client/auth.cpp`、`docs/dev/protocol.md`。
- 校验和：上游 `transport.cpp:send_packet` 只在 `protocol_version < A_VERSION_SKIP_CHECKSUM (0x01000001)` 时写负载累加和，否则写 0；接收侧 `check_header` 只校验 magic 与 data_length，**从不校验 data_check**。原实现按“协商版本 ≤ A_VERSION_MIN 才校验”在收到对端 CNXN 之前以 0 作为版本，把现代设备 CNXN 的 0 校验和判成 `ADB payload CRC mismatch`（USB 直连与 TLS 直连都复现）。已移除入站校验（保留不匹配时的 Debug 诊断，不再拒绝帧），并按协商版本决定出站 `data_check`（收到对端 CNXN 之前按上游写累加和）。
- 其它对齐：CNXN 的版本/最大负载按上游 `update_version` 取 min（≤`A_VERSION`、≤`MAX_PAYLOAD`）；TLS 模式下忽略 AUTH（上游 `if (t->use_tls) break`）；STLS 应答保持 24 字节头 + `A_STLS_VERSION`；OPEN 目标与 AUTH 公钥负载的结尾 NUL 与上游一致。
- 托管 TLS 全双工：Windows 回退的 BouncyCastle `TlsStream` 用一个信号量串行化 **await 式** 读写；ADB 消息循环常驻 read 会让所有写（OPEN/WRTE/OKAY）永久阻塞（实测 `OpenServiceStream` 挂起超过 120 s）。现改为托管路径阻塞 I/O：read 在线程池线程上执行、write 在调用线程上执行（BC 的 `m_recordWriteLock` 与 ADB 发送锁仍串行化写），期限由套接字 Receive/SendTimeout 承担；空闲帧首字节仍按连接层的“无限等待”规则执行。
- 实机（无 server，Wi-Fi，`192.168.1.144:40443`，设备 `24129PN74C`）：本机组播浏览 0 条广告、adb server 的 mDNS 表也为空，而**单播 DNS-SD 查询**拿到 `adb-6a082bda-lZgxIP._adb-tls-connect._tcp.local 192.168.1.144:40443`（该网络过滤组播应答）。用修复后的 `AdbSession` + `AdbTlsTransport` 直连：握手成功 `peer=0x01000001`，`exec:echo uotan-crc-ok` 返回 `uotan-crc-ok`，7 秒空闲后再次 `exec` 返回 `uotan-after-idle`，释放正常。
- 未验证边界：USB 直连的同一修复没有可用的 USB ADB 设备复测；配对阶段的广播仍只走组播（该网络实测可发现），组播全部被过滤且无法手动输入端口时仍无法配对；IPv6-only 链路与多网卡未实测。

## ADB TLS 服务端证书验证（2026-10-27，Windows x64，Debug；无 server 的扫码直连）

- 根因：adbd 的 TLS 服务端证书没有可钉扎身份。对照 `reference/adb-android16`：`daemon/auth.cpp` 的 `adbd_auth_tls_handshake` 每个进程懒生成随机 RSA 密钥（`rsa_pkey`），`transport.cpp` 的 `FdConnection::DoTlsHandshake` 每次握手都调用 `crypto/x509_generator.cpp` 的 `GenerateX509Certificate()`，把 `notBefore` 设为当前时间、序列号固定为 1，因此证书 DER 每次连接都变化；主机侧只用 `SetCertVerifyCallback([](X509_STORE_CTX*) { return 1; })` 接受任意服务端证书，设备侧在 `adbd_tls_verify_cert` 里校验本机公钥。
- 症状：按端点钉扎服务端证书 SHA-256 的旧实现第一次连接成功并记下指纹，第二次连接（新证书）在客户端 `NotifyServerCertificate` 处以 `bad_certificate(42)` 中止，设备侧尚未收到客户端证书；设备行随即失效，投屏报 `Device <host:port> is not connected`。
- 变更：`AdbTlsTransport` 删除服务端证书指纹参数、观察者与 `RemoteServerCertificateSha256`，平台与 BouncyCastle 两条路径都接受任意（仍必须出示）服务端证书；`DeviceController.OpenSecureNetworkAdbSession` 去掉钉扎参数；宿主 `PairedNetworkTarget` 只保留 `Address`/`RequiresTls` 并删除指纹持久化。设备以 `bad_certificate`/`certificate_unknown`/`unknown_ca`/`access_denied` 拒绝本机密钥时，握手与已建立连接的读写都返回“重新扫码配对或接受设备调试授权”的失败文案。
- 定向测试：`UotanStudio.Tests.Devices.AdbTlsTransportTests` 6/6——回环 TLS 1.3 服务端每次连接重建自签证书，连续两次 `UpgradeToTls` 均成功、服务端确实收到客户端证书，另有告警映射用例；`Services.PairedNetworkTargetSettingsTests` 3/3；`Devices` 298/298、`Protocols` 112/112。
- 未验证边界：真实手机没有复跑扫码直连与投屏；本轮检测时 `192.168.1.144:40443` 不可达（设备侧无线调试已关闭或端口已换），旧钉扎指纹与该地址的对应关系只由代码路径与设置文件推断。测试里的 TLS 1.3 服务端用 Schannel（临时密钥不受支持，故服务端证书先导出 PFX 再导入），BouncyCastle 服务端与真机 adbd 的对端行为未覆盖。

## 投屏输入注入预检（2026-10-27，Windows x64，Debug；真机 ColorOS）

- 设备侧事实：一加 ColorOS（设备 `24129PN74C`）没有把 `android.permission.INJECT_EVENTS` 授予 `com.android.shell` 时，scrcpy 服务端（`app_process`，uid 2000）的每次触摸注入都被 `system_server` 的 `InputManagerService` 拒绝（`Permission Denial: injectInputEvent() ... requires android.permission.INJECT_EVENTS`）。视频流不经过该权限，因此表现为“有画面、触摸全灭”，与投屏链路和 TLS 会话无关。
- 只读预检：投屏会话建立后执行 `dumpsys package com.android.shell | grep -E 'Package \[|INJECT_EVENTS'`，`InputInjectionProbe.Parse` 以 `Package [com.android.shell]` 标题确认探测有效、以 `granted=true` 作为唯一肯定信号；`granted=false`、只有 requested 行或权限行完全缺失都判为被拦，缺少包标题则返回未知（不误报）。探测复用投屏共享的直连 ADB 会话（server 路由时用 server 会话），不另开传输。
- 界面：判为被拦时在投屏标题栏显示 `ScreenMirror_InputInjectionBlocked`（中英各一条，含开发者选项 → USB 调试（安全设置）指引）；每次触摸在 3 秒节流下重跑一次预检，用户打开该开关后下一次点击即清除提示，无需重启会话。
- 定向测试：`UotanStudio.Tests.Devices.InputInjectionProbeTests` 7/7（granted/denied/仅 requested/其它权限/缺包标题/空输出）；`LocaleDictionaryTests` 6/6（新增 key 的中英一致与生成索引一致）；`MirrorSessionFacadeTests` 13/13。
- 未验证边界：标题栏提示条没有在受该 ROM 限制的真机上做人工确认（需要重新连接该设备并复跑投屏）；预检在放行设备上会多执行一条 `dumpsys`，极慢设备上的耗时未测量；ROM 若以 `dumpsys package` 之外的形式隐藏该授权，预检返回未知而不显示提示。

## 扫码配对连接端口获取：对齐 AOSP 与 ADB_Pair_Go（2026-10-27，Windows x64，Debug；无 server 的扫码直连）

- 参考源码：`reference/adb-android16/`（AOSP `packages/modules/adb`）与 `reference/ADB_Pair_Go/`（`github.com/lx200916/ADB_Pair_Go`，`git clone --depth 1` 拉到本地后逐文件核对 `adbPair.go`/`README.md`）。
- 官方链路（AOSP）：`daemon/mdns.cpp` 的 `register_adb_secure_connect_service(tls_port)` 以 `persist.adb.wifi.guid`（格式 `adb-<serialno>-<6 位随机字母数字>`）作为**实例名**注册 `_adb-tls-connect._tcp`，端口取自 `TlsServer::port()`；`daemon/adb_wifi.cpp` 的 `enable_wifi_debugging()` 在启用无线调试时启动 TLS 服务并注册该实例、`disable_wifi_debugging()` 注销，且启用前先 `start_mdnsd()`（`init.svc.mdnsd` 未运行就启动它，5 秒未就绪只写日志）。主机侧 `client/adb_wifi.cpp` 在配对成功后把配对交换里的 `ADB_DEVICE_GUID` 交给 `adb_secure_connect_by_service_name()`；`client/transport_mdns.cpp` 用它作为实例名在 `_adb-tls-connect` 的结果里按名字查找，`client/mdnsresponder_client.cpp` 再用 `adb_wifi_is_known_host(instance_name)` 与配对时写入 `adb_known_hosts.pb` 的 GUID 比对，命中才 `connect_device("<guid>._adb-tls-connect._tcp")`。ADB_Pair_Go 只覆盖前半段：zeroconf 浏览 `_adb-tls-pairing._tcp` 匹配二维码 `S` 字段后调用外部 `adb pair`，端口发现整个交给 adb 的 GUID 链路，没有独立实现。
- 结论：官方没有“轮询端口”这一步，端口只存在于 mDNS 的 `_adb-tls-connect` 实例里，而实例名就是配对返回的 GUID；本库此前按地址做泛化浏览并丢掉 GUID，随后又用全范围扫描串行兜底，两处偏差都在本轮对齐。
- 变更：配对交换返回的 GUID 带入 `AdbPairingEndpoint.Guid`；每轮除 PTR 浏览外，直接查询 `<guid>._adb-tls-connect._tcp.local` 的 SRV/TXT（组播与单播各一次），`AdbMdnsDiscovery.DescribesDevice` 让“实例名等于 GUID”与“地址等于配对地址”等价，`SelectConnectPorts` 优先选 GUID 命中的实例；`ConnectDiscoveryTimeout` 30 秒降到 10 秒，`ForeignPortFallbackDelay` 8 秒降到 4 秒。范围扫描不再是串行兜底，而是与广播查找**同时**进行（10 秒预算、768 并发、250 毫秒单次期限、命中即停），先给出端口的一方胜出并取消另一方，最坏等待从“30 秒浏览 + 25 秒扫描”降到约 8 秒。
- 实机测量（`192.168.1.144`，无线调试开启、该设备完全不广播 mDNS）：扫描加 ADB 握手确认在 **7.3 秒**内命中 `44065`；同一轮还发现另一个开放端口 `42568`，它没有完成 ADB 握手，说明“只接受完成握手的端口”是必需的判据。该地址 32768–60999 内只有这两个端口接受连接，其余 SYN 被静默丢弃，因此扫描耗时由超时而非 RTT 决定。
- 定向测试：`TcpPortScannerTests` 7/7；新增 `AdbConnectIdentityTests` 5/5（GUID 实例跨地址命中、仅地址命中、不同设备不命中、GUID 实例优先于更新的非 GUID 实例、无 GUID 时保持地址排序）；`Devices` 命名空间整批仍有 2 项既有的并发超时（`AdbTlsTransportTests.UpgradeToTls_DeviceRebuildsItsCertificateEachHandshake_CompletesBothHandshakes`、`HdcServerTransferTests.Recv_RegularFile_PublishesOnlyAfterCompletion`），单独复跑分别 6/6 与 10/10 通过。`UotanStudio.Comm` 与 `UotanStudio.Tests` 定向构建 0 警告 0 错误（此时整解决方案构建在 `UotanStudio.Desktop` 复制输出 DLL 处被正在运行的宿主进程锁定，与本次改动无关），whitespace 与 style `--verify-no-changes` 通过。
- 未验证边界：GUID 直查没有在“设备确实广播但 PTR 被过滤、SRV 仍可答”的网络上复测（本轮这台手机根本不广播，只验到扫描路径）；没有真机重跑完整扫码流程；IPv6-only 与改过 `ip_local_port_range` 的设备不在扫描覆盖内；`start_mdnsd` 失败（该 ROM 可能没有 mdnsd 或注册失败）时设备永远不会广播，除扫描外没有官方替代路径。

## RKDL flash 几何与擦除（2026-10-27，Windows x64，Release）

- `READ_FLASH_INFO` 的 11 字节结构前缀现在解出扇区数、扇区大小、以扇区计的块大小与 flash 片选掩码，并派生块字节大小、块数与在位芯片列表；`READ_CAPABILITY` 解出直接 LBA 位与首 4M 访问位。响应短于结构前缀时按设备实际应答的字节数报错，不再静默补零。
- `WRITE_SECTOR` 在建 CBW 之前先校验负载：必须是 528 字节物理扇区（512 数据 + 16 备用）的整数倍、不得超过一条命令接受的 32 扇区，且 LBA 范围可用；`TEST_UNIT_READY` 把 CSW 数据残留里的进度解出来，`EraseBlocks` 返回设备以 CSW 状态 1 应答的块数，使“事务失败”和“报出坏块”可区分。
- 擦除按上游 `CRKDevice` 的分支走两条路：介质为 eMMC（`READ_FLASH_ID` 前四字节小端等于 `EMMC`）或设备声明直接 LBA 时，用 `ERASE_LBA` 按 32768 扇区分块覆盖上报扇区数；否则按片选掩码逐颗在位芯片用 `ERASE_FORCE` 以 16 块为一条命令推进。每个分块前检查取消，进度按已擦除的扇区或块上报。
- 定向测试：`UotanStudio.Tests.Protocols.RkdlFlashGeometryDecodeTests` 7/7（结构字段映射、短响应报错、缺块几何报零块、能力位、无标志能力字、能力短响应报错、eMMC ID 的小端读法）。
- 未验证边界：本机没有连接任何 RKDL 设备，读几何、写扇区与两条擦除路径都没有线上实测；`EraseAllFlash`/`EraseLbaRange`/`IsEmmcFlash` 目前只是 `UotanStudio.Comm` 的公共 API，宿主与 MCP 都没有调用入口，因此还没接上仓库要求的危险操作确认门；不同主控对 CSW 状态 1 的实际语义差异未验证。

## 设备链路实测（2026-10-24，Windows 主机，ADB server 5037、HDC server 17035）

- ADB 有 server：USB 设备 `6b24c48f` 经 server 执行 shell 与 sync push/pull，回读逐字节一致。
- ADB 无 server：USB 直连在 WinUSB 与 libusb 两个后端均完成 shell 与 sync push/pull；TCP 直连 `192.168.1.149:5555` 完成 shell、sync 与 7 秒空闲后的再次命令。
- HDC 有 server：TCP 目标 `127.0.0.1:5555` 与 USB 目标 `5XM0225C18001159` 均完成 shell 与 7 秒空闲后的再次命令。
- HDC 无 server：停止 HDC server（17035 无监听）后直连 TCP `127.0.0.1:5555`，完成 shell 与 7 秒空闲后的再次命令，并完成过 sync send/recv。
- HDC USB 直连：设备 `12D1:5000`（0xff/0x50/1，序列号 `5XM0225C18001159`）绑定 WINUSB；停止 HDC server 使直连策略放行后，WinUSB 与 libusb 两个后端均完成 HDC 握手、shell 与 7 秒空闲后的再次命令。本机不把 COM 串口当作 HDC 目标。
- ADB Android 11+ 无线调试 TLS：STLS 常量、明文收到 A_STLS 快速失败、分部读取与“等待下一帧不设读取期限”由 `AdbRemoteProtocolTests` 覆盖；无 server 的扫码直连真机（2026-10-26，`192.168.1.144` 的 `_adb-tls-connect` 端口）完成配对与首次连接。托管 TLS（Windows 回退到 BouncyCastle）的读取期限只对已经开始到达的帧生效：等待下一帧无限等待，期限到达即把传输标记失效而不是继续复用损坏的记录层；空闲期间消失的对端由 TCP keepalive 兜底。`AdbTlsTransport.IsConnected` 反映真实链路状态（托管 TLS 已关闭/失败，或读写已失败即视为断开），共享池把失效会话立即移出池并连同其独占端点租约一起拆除，下一次借用重新打开并按 IP 重新解析端口；端口改写后触发一次设备列表刷新。真机上的 shell、logcat、属性查询与断线重连未复测。
- 扫码配对：没有可控的手机扫码 UI，未做真实扫码；对运行中的 adb server（platform-tools 36.0.0）执行 `host:pair` 确认命令格式与结果帧：请求确认后 server 追加 `OKAY`/`FAIL` 状态帧，连接被拒时返回 `Failed: Unable to start pairing client.`（实测分类为 `AdbServerPairingClientException`），非 `ServerOnly` 模式据此改用进程内 SPAKE2 配对后再经 server 连接；有 server 的配对/连接顺序对齐上游命令行的两步（`adb pair` 之后重新读取 `adb mdns services` 再 `adb connect`）：`host:pair` 成功后先重读 `host:mdns:services` 并 `host:connect:` 设备的 `_adb-tls-connect` 行，本地组播浏览保留现有实现并在表无行时兜底，配对前候选最后使用。结果帧的两种形状、失败分类与连接来源顺序由单元测试覆盖，真实扫码与回退后的真机连接仍未验证。
- 扫码后的连接端口自动获取：server 侧按 `host:devices-l` 的两种身份识别已连接设备（`ip:port`，以及经 `host:mdns:services` 表解析的 `adb-…._adb-tls-connect._tcp` 实例名）；显式连接先取重读的 server mDNS 表行，其次实时本地浏览，配对前候选最后，且 `host:connect:` 的应答要在目标列表中复核为非 `offline` 才被接受。配对后每 3 秒重读一次、直到 30 秒期限，候选按“最近广播的 SRV 优先、地址匹配先于仅主机名匹配”排序；无 server 时每轮先做实时浏览再探测，8 秒宽限后把其余广播实例的端口也按配对主机试探（覆盖双栈应答方用另一地址族/主机名发布的情况），组播没有交出任何实例时再向设备自身发送**单播 DNS-SD 问询**（该网络的组播应答会被过滤，实测单播仍能取到端口），未解析出地址的实例按其 SRV 端口参与试探，握手失败按 IP 重新浏览一次（宽限窗口后取最新实例），手动端口预填同样优先使用最新广播值。另外，已记住的安全目标在扫描时与 server 目标对齐：server 已列出同一主机时改用其当前端口，且同一主机已有其它 ADB TCP 行时不再单独生成 `adb-tcp:` 行（消除 43407/36695 这类同机双端口）。匹配、排序、时间戳与同主机去重由 `AdbPairingDiscoveryTests`、`AdbServerMdnsTests` 与 `AdbRemoteRoutingTests` 覆盖；单播兜底与真实手机的换端口时序仍以实机日志为准，IPv6-only 链路未实测。

## F2FS 真实镜像与 GKI 往返（2026-10-24，Windows x64 + WSL2 Alpine，Debug）

- 目标 `D:\NewProject1\NewProject1\output\userdata.raw`（10 GiB，F2FS v1.16，volume `data`，features `0x1499`；可达条目 3 个，含 1.587 GB `preinstall/cust.img`）。
- 托管解包捕获 3 个 inode 的 mode、uid/gid、大小、`security.selinux`、atime/ctime/mtime、generation、project id、link count、flags 与 advise；`TryRestore` 重建 1.70 GB 镜像，`EquivalentStructure`/`EquivalentContent` 为 true、`EquivalentBytes` 为 false、`FitsSourceFilesystemSize` 为 true，清单 2 条一致。
- `F2fsBuilder` 按 checkpoint 5% overprovision 预留主区段容量，`valid_block_count` 不超过 `user_block_count`；回归测试 `F2fsTests.Build_LargeData_CheckpointCapacityCoversEveryValidBlock` 用 320 MB 载荷断言容量账目与保留段。
- GKI 5.10.269 / 6.6.143 / 6.18.32（TCG）完成 fsck → 只读挂载 → 整树元数据与内容校验 → 读写写回 → fsck → 只读回读，三版均 PASS；`cust.img` SHA-256 与源一致，btime 两侧均为 `0.000000000`。
- `UotanStudio.Tests.Firmware` 全命名空间 4301 项 / 4299 通过 / 0 失败 / 2 跳过（缺 `UOTAN_EROFS_CORPUS`）；F2FS 相关 7 个类 70/70 通过。
- 边界：重建重放可复现的超级块特性位并重建 quota inode，crtime 位按上游 `0x0100` 重放；源含 casefold 目录或 verity 文件内容时显式拒绝，无法复现的特性位计入丢弃报告。该镜像无硬链接、符号链接、设备节点、压缩、大小写折叠、加密或 verity 内容；QEMU 为 TCG，不代表真实设备或厂商内核。证据在 `artifacts/gki-fs-lab/f2fs-real-20261024/`。

## 内嵌资源 zstd 帧（2026-10-24，Windows x64，Debug/Release）

### 规则表（Firmware）

- 生成物：帧 `UotanStudio.Assets/Assets/firmware/magic/CompiledMagicData.zst` 317,709 B（由 `UotanStudio.Firmware.MagicGen` 生成，压缩脚本只索引），常量 `UotanStudio.Firmware/Core/Magic/Generated/CompiledMagicData.g.cs` 1.4 KB（只含 `RuleCount`、`RulesByteLength`、`RulesSha256`、`GeneratedAt`）。解码后 SHA-256 `fe2e629bfea17370ef2f85625e00d3d198cfa3d9ae95d2bb9bb01d8ef2bbf6b1`、`topLevel=4205`、`rules=24763` 与跳过统计保持不变。
- 程序集：`UotanStudio.Firmware.dll` Release 12,213,248 B → 4,443,648 B（−7.41 MiB），Debug 4,770,304 B。帧改由 `UotanStudio.Assets` 以 `LogicalName=assets/firmware/magic/%(Filename)%(Extension)` 内嵌并声明 `WithCulture=false`；固件侧只经 `AssetStore.Read(AssetCatalog.MagicRules, AssetNames.Magic.Rules)` 取已校验字节。
- 运行时：`FileMagic.Identify` 首次调用 331 ms（资源读取 + zstd 解码 + SHA-256 校验 + 4205 条顶层规则建索引 + 首个匹配），热调用 113 µs/次（1000 次平均）；zstd 解码本体 2.0–2.8 ms。
- 识别正确性：`dotnet run --project UotanStudio.Firmware.MagicGen -- --smoke` 20/23 命中，未命中的 `plain-text`、`empty` 与 `ZIP` 样本属于既有边界。

### 字体（Host）

- 生成物：`UotanStudio.Assets/Assets/fonts/MiSans-Regular.ttf.zst` 4,900,305 B、`MiSans-Bold.ttf.zst` 4,979,674 B（原 8,122,324 / 8,009,316，60.3% / 62.2%）；原始 TTF 只在本地 `reference/fonts-source/`（仓库忽略）作重新生成的输入。
- 程序集：`UotanStudio.dll` Debug 23,382,016 B → 17,742,848 B（−5.38 MiB）。
- 运行时冒烟（真实 `UotanStudio.dll` + Win32/Skia 控制台宿主）：集合键 `fonts:UotanStudio/MiSans`，Regular 与 Bold 均解析为 `MiSans`、29,758 个字形；离屏渲染 `Uotan 中文字体` 得到 2,599 个非透明像素。冷构造 186.7 ms，首次 typeface 解析 6.0 ms。
- 未验证：真实 GUI 首帧与整体启动耗时、主题/回退链在窗口中的表现；AOT 运行时的字体加载只由发布产物中包含对应逻辑资源名间接证明。

### 鸿蒙采集库（Mirroring）

- 生成物：15 个 `UotanStudio.Assets/Assets/ohos/device/*.so` 共 11,204,026 B → 15 个 zstd 帧共 3,477,417 B（31.0%），逐文件长度与 SHA-256 记在唯一清单 `UotanStudio.Assets/Assets/manifest.tsv`；`UotanStudio.Mirroring.dll` Debug 3,627,520 B。
- 运行时：首次使用时由 `AssetStore.Materialize(AssetCatalog.OhosDevice)` 物化到 `%TEMP%/UotanStudio/assets/ohos/device/<bundle 摘要前 16 位>/`，逐文件校验长度与摘要后原子替换；`OhscrcpyDeviceAssetsTests` 通过条件同上，`OhscrcpyServerManagerTests` 12/12。
- 发布输出不含 `ohos/device` 松散文件（`publish/ohos/device` 只剩空目录）。
- 未验证：真机/HDC 推送与 `dlopen`；多进程并发首次物化共用同名 `<name>.tmp` 的竞态未特殊处理。

### 卡刷更新器（Delivery）

- 生成物：`UotanStudio.Assets/Assets/delivery/update-binary-arm64.zst` 838,349 B（原 2,002,464 B，41.9%），逻辑名 `assets/delivery/update-binary-arm64.zst`。
- 运行时：内存解压 → 长度 + SHA-256 校验 → `%TEMP%/UotanStudio/delivery-updater/` 原子落盘并设置可执行位；`EmbeddedUpdaterAssetTests` 2/2。
- 发布输出 `Assets/` 只有三个图标，不携带 2 MB 更新器。
- 未验证：设备侧实际执行该更新器。

## GUI 与投屏实测（2026-10-25，Windows x64，Debug；真机）

- 环境：Qualcomm `05C6:90DB`（直连 USB ADB，序列号 `6b24c48f`，Android 9 / SDK 28 / arm64-v8a）+ 华为 `12D1:5000`（HDC，connect key `5XM0225C18001159`，OpenHarmony-7.0.0.105 / SDK 26）；本机没有 adb/hdc server，两条链路都是直连。设备管理器同时读到两台设备且详情完整。
- 共享 ADB 会话下的 GUI 行为：设备日志在投屏期间与滚动后持续出新行；点击投屏后属性面板显示 `投屏中` / H264 / Media Foundation (GPU) / 1440 × 2880，全局日志出现 `Composition presentation active: D3D11 shared texture`；用记事本覆盖主窗口 6–7 s 后切回，视频区像素差异 15768–29657 px 而属性区 0 px 变化，注入"主屏幕"后视频区再变 21940 px；刷新详情后 UIA 查询 659 ms 返回且 `targetResponsive=true`；两次关闭程序均 6 s 内 `EXITED_CLEAN`。
- 关闭投屏文档：`SendMessageTimeout(WM_NULL)` 采样 25 s，静态画面下关闭瞬间 UI 最大延迟 9.4 ms（p95 0.5 ms），无一次超过 200 ms；关闭后设备日志仍在刷新，再次投屏正常出画（视频区均值 36.66）。
- 标签切换：切到交付页 3 s 再切回，视频区平均亮度 72.68 → 72.59（max 255），832/569232 个采样像素变化；注入"主屏幕"后变化 126659 个采样像素。最终二进制复跑一次为 44.52 → 36.50（max 255）、62952 个采样像素变化。
- 双设备投屏：同时打开 HDC 与 ADB 两个投屏文档，来回切换后 ADB 画面保持（72.70 → 72.61，852/569232 采样像素变化），注入"最近任务"后视频区变化 127776 采样像素。
- HDC 冷启动：`--open-screen-mirror 5XM0225C18001159` 出画（视频区均值 39.29，max 255）；清理本产品遗留的采集扩展后复跑均值为 50.31；`hdc fport ls` 可见应用注册的 `tcp:…→localabstract:scrcpy_grpc_socket` 与 `localabstract:uitest_socket` 两条转发。
- 未验证：密码页安全视图只验证了"不误判断联"，解锁后能否自动恢复出画未验证；设备待机断流的自动重开只在门禁与代码路径上验证，未在真机等待屏幕超时复现；双设备同时投屏时只有 ADB 一路有画面，两路同时出画未验证；HDC 冷启动稳定性未做 20 次以上重复统计。

## AVB 全链密钥、页脚与 OTA 操作（2026-10-27，Windows x64，Debug）

- 固件层新增 `AvbDescriptorTable`（原样保留描述符表的解析/替换）、`AvbSigner.SignVbmetaWithDescriptorTableAsync`（未知描述符类型不丢失）、`AvbSigner.RemoveFooterAsync`、`AvbImageVerifier`（尾部 + VBMeta 签名 + 内容/FEC 的端到端校验）、`PayloadPatcher.ReplaceAsync` 与 `PayloadSignatureVerifier`（两个 RSA 签名槽独立校验）。
- 宿主层新增 `AvbOtaService`：原始 `payload.bin` 或 A/B OTA ZIP 输入，重签所选分区并更新顶层 `vbmeta` 描述符，重建 Payload，ZIP 输入经 `OtaZipPatchService` 重新发布；`VerifyAsync` 校验 ZIP 结构、载荷签名与以 `vbmeta` 为根的 AVB 链。`OtaZipPatchService` 增加带 `IDeliverySigningKeyProvider` 的公开重载，宿主经 `DeliveryKeyProvider` 注入整包签名密钥。
- UI 新增 Tools → AVB 工具窗口（密钥/页脚/A-B OTA 三页）：AVB 密钥生成、导入、删除与 `avb_pkmd.bin` 导出；hash/hashtree 尾部添加、镜像校验、尾部移除与详情；OTA 分区选择、`disable-avb`、Payload/ZIP 密钥选择、`otapatch` 与 `otaverify`。窗口已接入 `HostWindowKind.AvbTool`、`MenuViewModel` 与 98 个 `Avb_*` 本地化键。
- 定向测试：`AvbDescriptorTableTests` 6/6、`AvbImageVerifierTests` 6/6、`AvbFooterRemovalTests` 4/4、`PayloadPatcherPublicTests` 4/4、`AvbOtaServiceTests` 3/3（含原始载荷与带载荷签名/整包签名的 OTA ZIP 往返）。
- `UotanStudio.Tests.Firmware` 全命名空间 4398 项 / 4396 通过 / 0 失败 / 2 跳过（缺 `UOTAN_EROFS_CORPUS`）；全解决方案 7662 项，两次完整运行均为 7655 通过 / 7 跳过 / 0 失败。之后在工作区并行负载下复跑时分别出现 1 个与本改动无关的时序/隔离失败（EROFS feature replay 与 USB serial cache 各一次），两类单独复跑分别为 20/20、6/6 通过。
- 全解决方案 Debug 构建 0 错误；`dotnet format` whitespace 与 style 双门无差异。并发会话中的 `FatBuilderTests` 仍有 6 条 xUnit1051 警告，不属于本次改动。NativeAOT `win-x64` 自包含发布成功（0 错误），警告仍为既有第三方 Ursa/Dock/Avalonia/DataGrid/YamlDotNet/Common.Logging 的 IL2104/IL3053/IL2026 与 `Makaretu.Dns.LinuxHelper` 的 Linux 专属告警，没有出现指向 AVB 新增代码的裁剪警告。
- 未验证边界：OTA 补丁/校验只用本地构建的完整 Payload 与最小 OTA ZIP 验证，未在真实厂商 OTA、差分/source 操作、`super` 动态分区或真机启动链上接受；顶层链式描述符保持原样且不能重定向，选择链保护分区会被拒绝；`otaverify` 校验载荷签名与 AVB 链，但不独立校验外层 ZIP 签名；AVB 工具窗口与原生文件选择器未做人工 GUI 验证；未与 Android `avbtool` 做逐字节外部对照。

## WinUSB 接管与厂商驱动包（2026-10-30，Windows x64，Debug）

- 接管路径：`WinUsbSelfSignedDriverPackage` 生成设备级 INF、自签代码签名证书与目录签名；暂存走 `SetupCopyOEMInfW(SPOST_PATH)`，绑定只对 `CM_Get_Device_ID_ListW` 返回且 `CM_Get_DevNode_Status` 报 `CM_PROB_FAILED_INSTALL` 的在位节点调 `CM_Reenumerate_DevNode(RETRY_INSTALLATION)`，随后 30 s 内轮询观测绑定切换；UI 触达的路径不调用 `UpdateDriverForPlugAndPlayDevices`/`DiInstallDriverW`。提权助手等待不设超时、不杀进程。
- 实测规避项（Win11）：mssign32 对带认证属性（SPC opus/statement）的 .cat 主体签名会在目录 SIP 内 `0xC0000005` 崩溃——与目录内容无关（系统自带 .cat 同样复现）、与提权无关；目录签名因此不带认证属性，产物 PKCS#7 内容类型 `1.3.6.1.4.1.311.10.1` 与系统自带目录一致。
- 确认框口令输入框在勾选不可逆确认前禁用；USB 树驱动操作失败（`Failed`/`DeviceNotFound`）结果与异常一样写入全局日志（`ElevationRequired` 不记）。
- 驱动包负载经 `UotanStudio.Assets` 的 `drivers/<packageId>` 资源包分发，`UotanStudio.Comm` 侧只有 `DriverPackageAssets` 经 `AssetStore` 读取，不再有程序集级内嵌项：八套包共 74 个负载、19.94 MiB 原始字节、15.18 MiB 帧（rockusb 3、mediatek-vcom 4、mediatek-full 22、google-usb 6、rockchip-adb 7、qualcomm-qud 19、silabs-cp210x 7、ftdi-cdm 6）。`dotnet run tools/compress-embedded-assets.cs -- --check` 报 `asset manifest check: OK`，整份清单 93 行全部逐字节校验通过。
- 定向测试：`UotanStudio.Tests.Assets` 命名空间 42 项 / 41 通过 / 1 跳过（未随包的 scrcpy 服务器）；`UotanStudio.Tests.Devices` 命名空间 353 项 / 350 通过 / 2 跳过（缺 `UOTAN_*` 环境）；`UotanStudio.Tests.Firmware.Architecture` 55 项全通过，含把接管包的摘要计算约束到 `UotanStudio.Hash` 的 `HashBoundaryGuardTests` 与 `ProjectReferenceDirectionGuardTests`。
- 门禁：全量 Debug 构建 `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 whitespace 与 style 双门零报告；全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000` 8018 总数 / 7998 通过 / 20 跳过 / 0 失败（1m43s）。
- 夹具与并发边界：`UotanStudio.Tests.Devices` 单独满载运行时出现过 1 次 `AdbTlsTransportTests` 失败，同一命令复跑与全解决方案运行均为 0 失败，属于并行负载下的握手超时，与本改动无关。
- 未验证边界：接管、解除接管与厂商驱动包安装/卸载均未在带管理员令牌的真机会话复跑（没有真机、没有 UAC 提示、没有一次真实的自签目录暂存）；重插后的绑定、WinUSB 会话与设备管理器中的 RKDL 目标未验证；`win-arm64` 与其余 RID 下的驱动包物化未执行。

## CI 门禁现状

`.github/workflows/ci.yml` 跑 `setup-dotnet` 取 10.0.x、构建、whitespace 与 style 两道 `dotnet format`，然后 `dotnet test --solution UotanStudio.slnx --no-restore -c Release --minimum-expected-tests 6000`，Test 步骤 `timeout-minutes: 15`，不设置任何 `UOTAN_*`，因此门控用例自跳。`.github/workflows/release.yml` 只负责 NativeAOT 发布。套件规模再变化时，Test 步骤的下限要跟着实测调，而不是靠放宽来掩盖删除。

## 门禁覆盖不到的地方

- `UotanStudio.Benchmarks` 与 `UotanStudio.FilesystemFixture` 在 `UotanStudio.slnx` 内，随解决方案构建/format/测试门一起运行。`Benchmarks` 的 `Avalonia.Headless` `PackageVersion` 仍必须保留在 `Directory.Packages.props`。
- 增量构建不重跑分析器，会显示"0 个警告"；任何警告清点都要带 `--no-incremental`。
- Debug 还原与构建都要显式 `-c Debug`（原因见根 `AGENTS.md` 的 Debug 构建条）。
- 测试宿主输出必须留在仓库内，否则 `FirmwareContextDependencyScannerTests` 失败并额外产生环境跳过。
- 共享工作树里并行会话重写 `bin/`/`obj/` 会伪造大批失败；判定红绿前先确认加载的字节与源码一致。`PayloadIncrementalBuilderTests` 家族在全量并发负载下出现过 SHA-256 与清单不符的红，单跑该类 14/14 通过。
- NativeAOT 发布仍会报告第三方 UI/Dock 库的 `IL2026` 反射绑定警告；DI 注册全部使用显式工厂，但第三方警告不等于运行时 GPU/窗口路径已验证。
- `UotanStudio.Tests` 没有 `UotanStudio.Tests.Comm` 命名空间；`UotanStudio.Comm/README.md` 的 `FullyQualifiedName~UotanStudio.Tests.Comm` 过滤命令零匹配，Comm 协议/传输的回归只有零散的 wire-format、设备映射与集成测试。

## 未验证边界

- `win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 未在本机执行；真实 USB 设备、物理手机与 macOS/Linux 平台路径未验证。NativeAOT 只在本机发布过 `win-x64`，AOT 下 `tools/list` 数量与真实设备/图形路径未重测。
- EDL/MTK 的界面项（expert 开关下的写入块上限与 NAND 每块页数覆盖、跳过读写确认头、主分区条目数组起始 LBA 与条目数量/大小覆盖、按清单回读、加密页脚读取、pbl/QFPROM 转储与安全启动状态、DA 阶段的区域转储控件、按目录匹配 programmer、设备卡上的 bootloader/Boot ROM 版本与 MEID/SOCID 行、只读 RPMB 容量按钮）只有假件与脚本化覆盖：没在真实 EDL/MTK 设备上执行过任何一次读写，也没有逐项目视过启动后的 GUI。MEID/SOCID 只在内存与设备卡显示，未持久化、未导出、未进日志；RPMB 容量未经真实 XFlash 存储就绪会话验证。
- WSL/QEMU 原生挂载与原生工具互操作不在仓库门禁内；文件系统写入方的 GKI 5.10/6.1 挂载、fsck 与整树读回取证在仓库外工装完成，不随 CI 运行。
- 无 server 的 TCP ADB 与 scrcpy 投屏结论来自同一模拟器实例的 5555 与 16384 回环端口实测；该 ROM 不回应 `GET_CLIPBOARD`，设备→客户端的主动剪贴板推送未在硬件上观察到，运行时切换也未在模拟器或设备上复跑。
- HDC 直连 forward/控制链路只由受 `UOTAN_HDC_MIRROR_DIRECT` 门控的 `OhscrcpyHdcIntegrationTests` 覆盖；本机因 DevEco 的 HDC server 占用目标，直连 USB 被 Comm 的 fail-closed 策略拒绝，该链路未验证。采集库需要设备侧匹配的 protobuf/gRPC ABI，不匹配时视频路径按能力不足快速失败；设备库随包分发只代表文件存在，不代表可加载。
- 有意不接入宿主的 Comm 能力以 `UotanStudio.Comm/README.md` 的 "Implemented but deliberately not surfaced in the host" 一节为准：目前记录的是要求 preloader 命令阶段且交换被拒即让会话 fault 的 `MtkSession.GetBootLoaderVersionAsync` 与 `GetBromVersionAsync`（同一对字节已由非致命的 `QueryBootIdentityOwnedAsync` 在初始化时收集）。该节同时声明厂商 exploit 实现类入口已整体移出代码库，不要按旧记录去找它们。
- HarmonyOS `update.bin` 只解析组件表与元数据 TLV 形态：`0x08` 签名载荷只跳过、不验签；ZIP 外壳、组件镜像内部的 `HARMONY!`/HVB 封装与文件系统二次解包不在该处理器内。除该小样例外的真实整包（多 GB、L1 布局）未实测，打包未实现。
- 投屏与设备之外的 GUI 交互（真实 FolderPicker、拖拽、缩放手势）没有自动化覆盖，Avalonia 编译绑定只在构建期校验绑定名，不等于渲染正确。
