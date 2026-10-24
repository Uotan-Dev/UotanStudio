# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## 当前实测（2026-10-25，Windows x64，Debug）

- `dotnet restore UotanStudio.slnx -p:Configuration=Debug` 与 `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误；全解只剩 `SpacemiTFlashTests`、`HdcServerTransferTests` 的 2 条既有 `xUnit1051` 警告。
- `dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes` 与同上的 `style`：全解两道门零报告。
- 定向测试：`UotanStudio.Tests.Mirroring` 127/127（1 硬件跳过）、`UotanStudio.Tests.Media` 202/202（1 Linux 跳过）、`UotanStudio.Tests.Devices` 249/249、`UotanStudio.Tests.Architecture` 16/16。架构门覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员。
- 全量（2026-10-24）：7250 总数 / 7244 通过 / 6 跳过 / 0 失败，单轮 2m41s（`dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000`，共享工作树）。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，`UotanStudio.Desktop.exe` 154,245,120 B（147.1 MiB）；只有 Avalonia/Dock/Ursa/Lingua/YamlDotNet/Common.Logging 等第三方既有的 IL2026/IL2104/IL3053 警告，没有本项目新增的裁剪/AOT 警告，内嵌资源名都能在产物中检索到。
- `TreatWarningsAsErrors=true` 生效于 7 个项目：`UotanStudio.Comm`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash`、`UotanStudio.Media`、`UotanStudio.Net`、`UotanStudio.RegexInfer`；全解 0 警告同时意味着受管库的 `CS1591` 为 0。
- 生产项目之间没有 `InternalsVisibleTo` 授权；宿主与 MCP 需要的契约是公共 API，只保留 `UotanStudio.Tests`、`UotanStudio.Benchmarks`、`UotanStudio.DeliverySmoke`、`GpuMirrorSmoke`、`UotanStudio.ProjectLifecycleSmoke` 等脚手架授权。
- MCP 工具目录 `UotanStudio.Mcp/Tools/PluginToolCatalog.cs` 共 48 条描述符，其中 6 条 `FlashGated`，只在 `--allow-flash` 下注册。
- 公共属性的 `<value>` 注释仍有约 257 处缺口（2026-10-18 度量），不影响构建门。

6 条跳过全部是环境或已知实现边界：

| 用例 | 原因 |
| --- | --- |
| `Media.VideoDecoderFactoryTests.Linux_CreatesDeferredBackendWithoutClaimingGpuSupport` | Linux 专属能力边界 |
| `Media.ScreenMirrorTransportTests.OpenAdbLease_NetworkTargetWithoutServer_DoesNotOpenUsb` | 本机有 ADB server 在运行，"server 不存在"分支不可测 |
| `Firmware.ErofsCorpusCompactIndexRebuildTests.PreservedRebuild_OfCorpusCompactIndexImage_ReExtractsEveryFileByteIdentical` | 未设 `UOTAN_EROFS_CORPUS` |
| `Firmware.ErofsInodeModelEquivalenceTests.Decode_CorpusImageInode_MatchesIndependentDecodeForEveryField` | 未设 `UOTAN_EROFS_CORPUS` |
| `Services.ScrcpyServerReleaseTests.BundledCatalog_DigestMatchesTheJarShippedWithTheApplication` | 测试宿主输出与检出仓库中都没有 `UotanStudio.Desktop/Assets/scrcpy-server.jar`，没有可哈希的出厂工件 |
| `Projects.ProjectBoundaryOptimizationTests.Package_RoundTripIncludesSolutionUnknownWorkflowAndImages` | `ProjectArchiveService.ExportPackageAsync` 拿到 solution 路径时只打包子项目目录，包内缺 solution 级 `nodes/`，`ProjectPackage.ImportAsync` 因而拒绝每个已注册节点工程 |

## 设备链路实测（2026-10-24，Windows 主机，ADB server 5037、HDC server 17035）

- ADB 有 server：USB 设备 `6b24c48f` 经 server 执行 shell 与 sync push/pull，回读逐字节一致。
- ADB 无 server：USB 直连在 WinUSB 与 libusb 两个后端均完成 shell 与 sync push/pull；TCP 直连 `192.168.1.149:5555` 完成 shell、sync 与 7 秒空闲后的再次命令。
- HDC 有 server：TCP 目标 `127.0.0.1:5555` 与 USB 目标 `5XM0225C18001159` 均完成 shell 与 7 秒空闲后的再次命令。
- HDC 无 server：停止 HDC server（17035 无监听）后直连 TCP `127.0.0.1:5555`，完成 shell 与 7 秒空闲后的再次命令，并完成过 sync send/recv。
- HDC USB 直连：设备 `12D1:5000`（0xff/0x50/1，序列号 `5XM0225C18001159`）绑定 WINUSB；停止 HDC server 使直连策略放行后，WinUSB 与 libusb 两个后端均完成 HDC 握手、shell 与 7 秒空闲后的再次命令。本机不把 COM 串口当作 HDC 目标。
- ADB Android 11+ 无线调试 TLS：本机仅有 Android 9 设备，没有 Android 11+ 无线调试端点，未做硬件 TLS 连接；STLS 常量、明文收到 A_STLS 快速失败、分部读取由 `AdbRemoteProtocolTests` 覆盖。
- 扫码配对：没有可控的手机扫码 UI，未做真实扫码；对运行中的 adb server 执行 `host:pair` 探针返回 server 的配对客户端错误，确认命令格式；精确命令/OKAY 后 FAIL 由单元测试覆盖。

## F2FS 真实镜像与 GKI 往返（2026-10-24，Windows x64 + WSL2 Alpine，Debug）

- 目标 `D:\NewProject1\NewProject1\output\userdata.raw`（10 GiB，F2FS v1.16，volume `data`，features `0x1499`；可达条目 3 个，含 1.587 GB `preinstall/cust.img`）。
- 托管解包捕获 3 个 inode 的 mode、uid/gid、大小、`security.selinux`、atime/ctime/mtime、generation、project id、link count、flags 与 advise；`TryRestore` 重建 1.70 GB 镜像，`EquivalentStructure`/`EquivalentContent` 为 true、`EquivalentBytes` 为 false、`FitsSourceFilesystemSize` 为 true，清单 2 条一致。
- `F2fsBuilder` 按 checkpoint 5% overprovision 预留主区段容量，`valid_block_count` 不超过 `user_block_count`；回归测试 `F2fsTests.Build_LargeData_CheckpointCapacityCoversEveryValidBlock` 用 320 MB 载荷断言容量账目与保留段。
- GKI 5.10.269 / 6.6.143 / 6.18.32（TCG）完成 fsck → 只读挂载 → 整树元数据与内容校验 → 读写写回 → fsck → 只读回读，三版均 PASS；`cust.img` SHA-256 与源一致，btime 两侧均为 `0.000000000`。
- `UotanStudio.Tests.Firmware` 全命名空间 4301 项 / 4299 通过 / 0 失败 / 2 跳过（缺 `UOTAN_EROFS_CORPUS`）；F2FS 相关 7 个类 70/70 通过。
- 边界：重建不保留超级块特性位（`0x1499` → `0x58`）与 quota inode；该镜像无硬链接、符号链接、设备节点、压缩、大小写折叠、加密或 verity 内容；QEMU 为 TCG，不代表真实设备或厂商内核。证据在 `artifacts/gki-fs-lab/f2fs-real-20261024/`。

## 内嵌资源 zstd 帧（2026-10-24，Windows x64，Debug/Release）

### 规则表（Firmware）

- 生成物：`UotanStudio.Firmware/Core/Magic/Generated/CompiledMagicData.zst` 317,678 B（原 3,044,942 B blob 的 10.4%），同目录 `CompiledMagicData.g.cs` 1.5 KB（只含 `RuleCount`、`RulesByteLength`、`CompressedByteLength`、`RulesSha256`、`GeneratedAt`）。解码后 SHA-256 `fe2e629bfea17370ef2f85625e00d3d198cfa3d9ae95d2bb9bb01d8ef2bbf6b1`、`topLevel=4205`、`rules=24763` 与跳过统计保持不变。
- 程序集：`UotanStudio.Firmware.dll` Release 12,213,248 B → 4,443,648 B（−7.41 MiB），Debug 4,770,304 B。资源以 `LogicalName=UotanStudio.Firmware.Core.Magic.rules.zst` 内嵌；`WithCulture=false` 必需，否则 MSBuild 会把文件名里的 `.bin` 段当文化并产出 satellite assembly。
- 运行时：`FileMagic.Identify` 首次调用 331 ms（资源读取 + zstd 解码 + SHA-256 校验 + 4205 条顶层规则建索引 + 首个匹配），热调用 113 µs/次（1000 次平均）；zstd 解码本体 2.0–2.8 ms。
- 识别正确性：`dotnet run --project UotanStudio.Firmware.MagicGen -- --smoke` 20/23 命中，未命中的 `plain-text`、`empty` 与 `ZIP` 样本属于既有边界。

### 字体（Host）

- 生成物：`UotanStudio/Fonts/MiSans-Regular.ttf.zst` 4,900,305 B、`MiSans-Bold.ttf.zst` 4,979,674 B（原 8,122,324 / 8,009,316，60.3% / 62.2%）；原始 TTF 只在本地 `reference/fonts-source/`（仓库忽略）作重新生成的输入。
- 程序集：`UotanStudio.dll` Debug 23,382,016 B → 17,742,848 B（−5.38 MiB）。
- 运行时冒烟（真实 `UotanStudio.dll` + Win32/Skia 控制台宿主）：集合键 `fonts:UotanStudio/MiSans`，Regular 与 Bold 均解析为 `MiSans`、29,758 个字形；离屏渲染 `Uotan 中文字体` 得到 2,599 个非透明像素。冷构造 186.7 ms，首次 typeface 解析 6.0 ms。
- 未验证：真实 GUI 首帧与整体启动耗时、主题/回退链在窗口中的表现；AOT 运行时的字体加载只由发布产物中包含对应逻辑资源名间接证明。

### 鸿蒙采集库（Mirroring）

- 生成物：15 个 `Assets/ohos/device/*.so` 共 11,204,026 B → 15 个 zstd 帧共 3,478,225 B（31.0%），加同目录 `manifest.txt`（文件名、解码长度、SHA-256）；`UotanStudio.Mirroring.dll` Debug 3,627,520 B。
- 运行时：首次使用时物化到 `%TEMP%/UotanStudio/ohos-device/<清单 SHA-256 前 16 位>/`，逐文件校验长度与摘要后原子替换；`OhscrcpyDeviceAssetsTests` 3/3，`OhscrcpyServerManagerTests` 12/12。
- 发布输出不含 `ohos/device` 松散文件（`publish/ohos/device` 只剩空目录）。
- 未验证：真机/HDC 推送与 `dlopen`；多进程并发首次物化共用同名 `<name>.tmp` 的竞态未特殊处理。

### 卡刷更新器（Delivery）

- 生成物：`Assets/update-binary-arm64.zst` 838,349 B（原 2,002,464 B，41.9%），逻辑名 `UotanStudio.Delivery.Assets.update-binary-arm64`。
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
- 投屏与设备之外的 GUI 交互（真实 FolderPicker、拖拽、缩放手势）没有自动化覆盖，Avalonia 编译绑定只在构建期校验绑定名，不等于渲染正确。
