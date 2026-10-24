# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## 当前实测（2026-10-23，Windows x64，主树工作区，Debug）

- `dotnet restore UotanStudio.slnx -p:Configuration=Debug`：成功。
- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 与 `-c Release`：均 0 错误、0 警告；Release 构建的 3 条 EROFS `xUnit1026` 已通过移除未使用参数消除。
- 本轮全量 Rebuild（`-t:Rebuild -m:1 -nodeReuse:false`）：0 错误、0 编译器警告；仅在并发测试宿主占用测试 exe 时出现文件复制重试告警，不计为代码告警。
- `dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes` 与同上的 `style`：均通过；本轮改动的 Media、Comm 设备/USB、宿主设备服务与日志视图、对应测试目录另跑 `--include` 两道门，零报告。
- 版本/通道/提交与关于页改动：`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误、0 警告；`BuildInfoTests` 5/5、`OpenSourceProjectsTests` 2/2、`LocaleDictionaryTests` 6/6 通过；`dotnet format … whitespace|style --verify-no-changes` 全解两道门通过。
- 全量测试（`dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000`）：7104 总数 / 7098 通过 / 0 失败 / 6 跳过，单轮 1m17s（共享工作树）。
- `Firmware.BuilderStreamingTests.Erofs_StreamingOutput_MatchesByteArrayApi` 的偶发红根因是两次构建分别读取 `DateTimeOffset.UtcNow`：`ErofsOptions.BuildTime` 未固定时，跨秒运行会把不同的 epoch 写进 inode 元数据（偏移 1152 的字节在 4/5 间变化），字节数组与流式 API 本身没有差异。测试现固定 `BuildTime = 1_700_000_000`，修复后连续 30/30 通过。
- 生产项目之间的 `InternalsVisibleTo` 已清零：Comm/Compress/Delivery/Firmware/Mcp/Projects 对宿主、MCP 或 Firmware 的授权全部移除，宿主与 MCP 需要的契约改为 public；仅保留 `UotanStudio.Tests`、`UotanStudio.Benchmarks`、`UotanStudio.DeliverySmoke`、`GpuMirrorSmoke`、`UotanStudio.ProjectLifecycleSmoke` 等脚手架授权。
- 架构门定向测试（`--filter-namespace UotanStudio.Tests.Architecture --minimum-expected-tests 1`）：16/16 通过，覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员。
- `dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes` 与 `style --verify-no-changes`：均通过。
- NativeAOT 发布：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，产物 `UotanStudio.Desktop.exe` 约 163 MB；ILC 报告第三方（Dock/Avalonia/Lingua/DataGrid/YamlDotNet/Common.Logging 等）既有的 trim/AOT 警告，未出现本轮 DI/Net/Compress/Firmware 新增的裁剪警告。
- `dotnet build UotanStudio.Comm/UotanStudio.Comm.csproj` 与 `UotanStudio/UotanStudio.csproj` 的 `--no-incremental -m:1 -nodeReuse:false`：均 0 错误、0 警告；全解 `--no-incremental` 在并发测试宿主占用测试输出时出现 MSB3021/MSB3027 文件锁错误，属环境冲突，不是编译器诊断。
- 性能批次的改动范围：15 个文件跑 `dotnet format … whitespace|style --verify-no-changes --include`，两道门零报告；`UotanStudio.Benchmarks` 的 5 个 ProfileRunner 负载产出镜像 `image sha256` 与该批次改动前逐字节相同（EROFS LZ4/ZSTD/LZMA 打包与解包、ext4 打包）。基准工程现在在解决方案内，会被解决方案构建与 format 门覆盖；性能证据仍由 `--profile` 运行器给出。
- `TreatWarningsAsErrors=true` 生效于 7 个项目：`UotanStudio.Comm`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash`、`UotanStudio.Media`、`UotanStudio.Net`、`UotanStudio.RegexInfer`。全解 0 警告同时意味着受管库的 `CS1591` 为 0。
- 公共属性的 `<value>` 注释仍有未闭合缺口（最近一次度量约 257 处，2026-10-18），不影响构建门。
- MCP 工具目录 `UotanStudio.Mcp/Tools/PluginToolCatalog.cs` 共 48 条描述符，其中 6 条 `FlashGated`，只在 `--allow-flash` 下注册。

本轮 6 条跳过全部是环境或已知实现边界：

| 用例 | 原因 |
| --- | --- |
| `Media.VideoDecoderFactoryTests.Linux_CreatesDeferredBackendWithoutClaimingGpuSupport` | Linux 专属能力边界 |
| `Media.ScreenMirrorTransportTests.OpenAdbLease_NetworkTargetWithoutServer_DoesNotOpenUsb` | 本机有 ADB server 在运行，“server 不存在”分支不可测 |
| `Firmware.ErofsCorpusCompactIndexRebuildTests.PreservedRebuild_OfCorpusCompactIndexImage_ReExtractsEveryFileByteIdentical` | 未设 `UOTAN_EROFS_CORPUS` |
| `Firmware.ErofsInodeModelEquivalenceTests.Decode_CorpusImageInode_MatchesIndependentDecodeForEveryField` | 未设 `UOTAN_EROFS_CORPUS` |
| `Services.ScrcpyServerReleaseTests.BundledCatalog_DigestMatchesTheJarShippedWithTheApplication` | 测试宿主输出与检出仓库中都没有 `UotanStudio.Desktop/Assets/scrcpy-server.jar`，没有可哈希的出厂工件 |
| `Projects.ProjectBoundaryOptimizationTests.Package_RoundTripIncludesSolutionUnknownWorkflowAndImages` | `ProjectArchiveService.ExportPackageAsync` 拿到 solution 路径时只打包子项目目录，包内缺 solution 级 `nodes/`，`ProjectPackage.ImportAsync` 因而拒绝每个已注册节点工程 |

## 远端连接修复实测（2026-10-24，Windows x64，Debug）

- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误；1 条 `xUnit1051` 警告来自并行 `UotanStudio.Tests/Protocols/SpacemiTFlashTests.cs`，`UotanStudio.Comm` 自身 0 警告。
- NativeAOT 发布 `dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true`：成功（exit 0），仅第三方库既有的 IL2026/IL2104/IL3053 裁剪警告。
- 新增/相关定向测试全部通过：`AdbRemoteProtocolTests` 10、`HdcRemoteProtocolTests` 11、`AdbRemoteRoutingTests` 9、`HdcRemoteRoutingTests` 6、`AdbPairingDiscoveryTests` 26；`UotanStudio.Tests.Devices` 207、`UotanStudio.Tests.Protocols` 17、`HeadlessControllerGatewayTests` 10、`McpDeviceManagerGatewayTests` 10 均通过（均带 `--minimum-expected-tests 1`）。
- 全量 `dotnet test --solution UotanStudio.slnx --no-build -c Debug --minimum-expected-tests 6000`：7185 总数 / 7176 通过 / 7 跳过 / 2 失败；两条失败均为并行改动：`CompressionImplementationBoundaryTests` 指向 `UotanStudio.Comm/Protocols/SpacemiT/SpacemiTFlasher.cs:440`（并行 SpacemiT 新增代码，不是本次 ADB/HDC 改动），`HashBoundaryGuardTests` 指向 `UotanStudio.Mirroring/Ohscrcpy/OhscrcpyServerManager.cs:712 MD5.HashData`（并行 HDC 投屏改写）。这两处修复后需重跑全量；本次新增/相关 234 项定向测试均通过。
- `dotnet format UotanStudio.slnx style --no-restore --verify-no-changes`：通过。whitespace 全解仅报并行 Mirroring 文件的 FINALNEWLINE；对本次改动的 Comm/宿主/测试文件执行 `--include` 校验通过。
- 真机/真 server 矩阵（Windows 主机，ADB server 5037、HDC server 17035）：
  - ADB 有 server：USB 设备 `6b24c48f` 经 server 执行 shell 与 sync push/pull，回读逐字节一致。
  - ADB 无 server：USB 直连在 WinUSB 与 libusb 两个后端均完成 shell 与 sync push/pull；TCP 直连 `192.168.1.149:5555` 完成 shell、sync 与 7 秒空闲后的再次命令。
  - HDC 有 server：TCP 目标 `127.0.0.1:5555` 与 USB 目标 `5XM0225C18001159` 均完成 shell 与 7 秒空闲后的再次命令。
  - HDC 无 server：停止 HDC server（17035 无监听）后直连 TCP `127.0.0.1:5555`，完成 shell 与 7 秒空闲后的再次命令；此前同一端点另完成过 sync send/recv。
  - HDC USB 直连：设备 `12D1:5000`（0xff/0x50/1，序列号 `5XM0225C18001159`）已绑定 WINUSB；停止 HDC server 使直连策略放行后，WinUSB 与 libusb 两个后端均完成 HDC 握手、shell 与 7 秒空闲后的再次命令。HDC USB 的 server 路由此前也已通过；本机不把 COM 串口当作 HDC 目标。
  - ADB Android 11+ 无线调试 TLS：本机仅有 Android 9 设备，无 Android 11+ 无线调试端点，未做硬件 TLS 连接；STLS 常量、明文收到 A_STLS 快速失败、分部读取由 `AdbRemoteProtocolTests` 覆盖。
  - 扫码配对：没有可控的手机扫码 UI，未做真实扫码；对运行中的 adb server 执行 `host:pair` 探针返回 server 的配对客户端错误（而非地址解析或响应长度错误），确认命令格式；精确命令/OKAY 后 FAIL 由单元测试覆盖。

## F2FS 真实镜像与 GKI 往返（2026-10-24，Windows x64 + WSL2 Alpine，Debug）

- 目标 `D:\NewProject1\NewProject1\output\userdata.raw`（10 GiB，F2FS v1.16，volume `data`，features `0x1499`；可达条目 3 个，含 1.587 GB `preinstall/cust.img`）。
- 托管解包捕获 3 个 inode 的 mode、uid/gid、大小、`security.selinux`、atime/ctime/mtime、generation、project id、link count、flags 与 advise；`TryRestore` 重建 1.70 GB 镜像，`EquivalentStructure`/`EquivalentContent` 为 true、`EquivalentBytes` 为 false、`FitsSourceFilesystemSize` 为 true，清单 2 条一致。
- 修复 `F2fsBuilder` 主区段数未按 checkpoint 5% overprovision 留出容量、导致 `valid_block_count > user_block_count` 且 GKI 5.10/6.6/6.18 拒绝挂载的缺陷；同时修复解析器把常规条目首个数据块地址当作 `DeviceId` 的问题。回归测试 `F2fsTests.Build_LargeData_CheckpointCapacityCoversEveryValidBlock` 用 320 MB 载荷断言容量账目与保留段。
- GKI 5.10.269 / 6.6.143 / 6.18.32（TCG）对修复后镜像完成 fsck → 只读挂载 → 整树元数据与内容校验 → 读写写回 → fsck → 只读回读，三版均 PASS；`cust.img` SHA-256 与源一致，btime 两侧均为 `0.000000000`。
- `UotanStudio.Tests.Firmware` 全命名空间 4301 项 / 4299 通过 / 0 失败 / 2 跳过（缺 `UOTAN_EROFS_CORPUS`）；F2FS 相关 7 个类 70/70 通过；改动文件的 whitespace 与 style format 门通过。
- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误、0 警告；`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，仅既有第三方 trim/AOT 警告。
- 边界：重建不保留超级块特性位（`0x1499` → `0x58`）与 quota inode；该镜像无硬链接、符号链接、设备节点、压缩、大小写折叠、加密或 verity 内容；QEMU 为 TCG，不代表真实设备或厂商内核。证据在 `artifacts/gki-fs-lab/f2fs-real-20261024/`。

## HDC 投屏（ohscrcpy / UI Test 扩展）

- 设备路径：UI Test 版本探测 → 复用已有控制守护进程，需要新建时推送控制代理并备份/恢复 → 按架构/版本从 `ohos/device` 推送采集 `.so` → `fport` 把设备端 `localabstract:scrcpy_grpc_socket`、`localabstract:uitest_socket`（旧版本 `tcp:5000`、`tcp:8012`）映射到回环端口 → 客户端收 gRPC H.264 访问单元（`/ScrcpyService/onStart` 的 `ReplyMessage`）与 UI Test JSON。
- 设备库来源：本机 DevEco Studio 26.0.0.821 的 `plugins/screen-mirroring/lib/hosScrcpy-1.0.20-beta.jar`（SHA-256 `62cba3701b635a9d9c906a70f18cbc43efd60b7bfd61e2bb18fb88b2c7dfb187`），15 个 `libscrcpy/*.so` 与 `uitest_agent*.so` 已由 `tools/deveco-mirroring/extract.ps1` 提取到 `UotanStudio.Mirroring/Assets/ohos/device/`，逐文件 SHA-256 记录在 `Assets/ohos/README.md`；此前 14 个文件与 `reference/HdcMirror` 拷贝逐字节一致，新增 `libscrcpy_server0.z.so`。提取成功只证明文件来源与完整性，不证明设备可加载。
- 单元测试类：`UotanStudio.Tests.Media.OhscrcpyProtocolTests`、`UotanStudio.Tests.Mirroring.OhscrcpyServerManagerTests`、`UotanStudio.Tests.Mirroring.OhscrcpyClientTests`、`UotanStudio.Tests.Mirroring.ScrcpyToOhscrcpyControlAdapterTests`、`UotanStudio.Tests.Mirroring.MirrorSessionFacadeTests`；命令为 `dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class <FQCN> --minimum-expected-tests 1`，覆盖 gRPC 帧/`ReplyMessage` 解析、SPS 几何、控制消息映射、server-manager 代理/库选择与命令构造、客户端解复用、facade 校验。
- 硬件边界：5XM0225C18001159（HarmonyOS 7.0.0.109）加载 `ohos/device` 下 2026 年采集库时，`dlopen` 因 protobuf/gRPC ABI 落后失败；模拟器缺 `libgrpc.z.so`，且其 `uitest start-daemon singleness` 报 `Client nativeCode not exist`。视频解码/播放与 UI 渲染未验证，不能写成已验证。
- 直连 forward/控制链路：受 `UOTAN_HDC_MIRROR_DIRECT` 门控的 `OhscrcpyHdcIntegrationTests` 接受 HDC connect key 或直连守护进程 `host:port`。本轮尝试 `5XM0225C18001159` 未通过：DevEco 的 HDC server 正在运行，Comm 的 fail-closed USB 策略拒绝直连 USB 打开，因此 `HdcDirectConnection` 无法取得直连会话；这是环境边界，不是控制协议断言失败。停止 HDC server 或在无 server 的主机上重跑该门控用例前，直连链路仍未验证，跳过/失败都不算通过。

## 内嵌资源 zstd 化实测（2026-10-24，Windows x64，Debug/Release）

### 规则表（Firmware）

- 生成物：`UotanStudio.Firmware/Core/Magic/Generated/CompiledMagicData.zst` 317,678 B（原 3,044,942 B blob 的 10.4%），同目录 `CompiledMagicData.g.cs` 1.5 KB（只含 `RuleCount`、`RulesByteLength`、`CompressedByteLength`、`RulesSha256`、`GeneratedAt`）。规则数据未变：解码后 SHA-256 `fe2e629bfea17370ef2f85625e00d3d198cfa3d9ae95d2bb9bb01d8ef2bbf6b1` 与改动前逐字节一致，`topLevel=4205`、`rules=24763` 与跳过统计不变。
- 程序集：`UotanStudio.Firmware.dll` Release 12,213,248 B → 4,443,648 B（−7.41 MiB）；Debug 4,770,304 B。资源以 `LogicalName=UotanStudio.Firmware.Core.Magic.rules.zst` 内嵌；`WithCulture=false` 是必需的，否则 MSBuild 会把文件名里的 `.bin` 段当文化并产出 satellite assembly。
- 运行时：`FileMagic.Identify` 首次调用 331 ms（资源读取 + zstd 解码 + SHA-256 校验 + 4205 条顶层规则建索引 + 首个匹配），热调用 113 µs/次（1000 次平均）；zstd 解码本体 2.0–2.8 ms，对比原 base64 路径 5.5 ms 与 6.1 MB 临时分配。
- 识别正确性：`dotnet run --project UotanStudio.Firmware.MagicGen -- --smoke` 20/23 命中，未命中的 `plain-text`、`empty` 与 `ZIP` 样本属于既有边界。

### 字体（Host）

- 生成物：`UotanStudio/Fonts/MiSans-Regular.ttf.zst` 4,900,305 B、`MiSans-Bold.ttf.zst` 4,979,674 B（原 8,122,324 / 8,009,316，60.3% / 62.2%）；原始 TTF 移出仓库与项目，仅保留在本地 `reference/fonts-source/`（仓库忽略）供重新生成。
- 程序集：`UotanStudio.dll` Debug 23,382,016 B → 17,742,848 B（−5.38 MiB）。
- 运行时冒烟（真实 `UotanStudio.dll` + Win32/Skia 控制台宿主，反射实例化 `ZstdEmbeddedFontCollection`）：集合键 `fonts:UotanStudio/MiSans`，Regular 与 Bold 均解析为 `MiSans`、29,758 个字形；离屏渲染 `Uotan 中文字体` 得到 2,599 个非透明像素。冷构造（两张字形：资源读取 + zstd 解码 + Avalonia 复制到非托管）186.7 ms，首次 typeface 解析 6.0 ms。
- 未验证：真实 GUI 首帧与整体启动耗时、主题/回退链在窗口中的表现；AOT 运行时的字体加载只由发布产物中包含五个逻辑资源名间接证明。

### 鸿蒙采集库（Mirroring）

- 生成物：15 个 `Assets/ohos/device/*.so` 共 11,204,026 B → 15 个 zstd 帧共 3,478,225 B（31.0%），加同目录 `manifest.txt`（文件名、解码长度、SHA-256）；`UotanStudio.Mirroring.dll` Debug 3,627,520 B。
- 运行时：首次使用时物化到 `%TEMP%/UotanStudio/ohos-device/<清单 SHA-256 前 16 位>/`，逐文件校验长度与摘要后原子替换；`OhscrcpyDeviceAssetsTests` 3/3（目录存在、15 个 `*.so`、原始文件名保留、重复访问返回同一目录），`OhscrcpyServerManagerTests` 12/12。
- 发布输出不再包含 `ohos/device` 松散文件（`publish/ohos/device` 只剩空目录）。
- 未验证：真机/HDC 推送与 `dlopen`；多进程并发首次物化共用同名 `<name>.tmp` 的竞态未特殊处理。

### 卡刷更新器（Delivery）

- 生成物：`Assets/update-binary-arm64.zst` 838,349 B（原 2,002,464 B，41.9%），逻辑名 `UotanStudio.Delivery.Assets.update-binary-arm64`。
- 运行时：内存解压 → 长度 + SHA-256 校验 → `%TEMP%/UotanStudio/delivery-updater/` 原子落盘并设置可执行位；`EmbeddedUpdaterAssetTests` 2/2（ELF 头、固定长度、重复调用复用同一路径）。
- 发布输出 `Assets/` 只剩三个图标，不再携带 2 MB 更新器。
- 未验证：设备侧实际执行该更新器（原有边界不变）。

### 统一门禁

- `dotnet build UotanStudio.slnx --no-restore -c Debug`：0 错误；2 条 `xUnit1051` 警告来自并行改动的测试文件（`SpacemiTFlashTests`、`HdcServerTransferTests`）。
- 全量测试：7229 总数 / 7222 通过 / 7 跳过 / 0 失败，单轮 1m26s。
- 定向测试全绿：`CompiledMagicResourceTests` 4、`FileMagicTests` 34、`CompiledMagicIndexEquivalenceTests` 4、`OhscrcpyServerManagerTests` 12、`EmbeddedUpdaterAssetTests` 2、`OhscrcpyDeviceAssetsTests` 3。
- `dotnet format UotanStudio.slnx whitespace|style --no-restore --verify-no-changes --include <本次改动文件>`：两道门零报告。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，`UotanStudio.Desktop.exe` 154,243,584 B（147.1 MiB）；只有 Avalonia/Dock/Ursa/Lingua/YamlDotNet/Common.Logging 等第三方既有的 IL2026/IL2104/IL3053 警告，没有本次改动新增的裁剪/AOT 警告，五个逻辑资源名都能在 exe 中检索到。
- 未验证：GUI 实际启动与字体呈现、AOT 运行时首次解压耗时、真机 HDC 推送、卡刷更新器设备侧执行。

## 启动窗状态与资源解压阶段实测（2026-10-24，Windows x64，Debug）

- 行为：字体集合注册从 `App.Initialize` 移到启动序列首个阶段 `SplashPhase.Resources`，启动窗文本显示“正在解压内置资源...”；新增 `SplashPhase.DevicesFound` 显示“发现 N 台设备/未发现设备”；`ApplyingSettings` 阶段去掉重复推进；`Ready` 不再依赖主窗预构建成功。进度条改为单条恒 `IsIndeterminate="True"`，不再绑定 `Value`，删除“完成到 100%”的第二条进度条与 `IsInitializing`。
- 启动窗字体：全局 `Control` 样式把每个控件设为 `fonts:UotanStudio/MiSans#MiSans`，而资源解压发生在启动窗之后，因此 `SplashViewModel.SplashFontFamily` 用 `FontManager.TryMatchCharacter` 取平台默认族并作为三个文本块的本地值；否则启动窗会先解析空的 `fonts:` 集合。
- 运行时冒烟（真实 `App` + Win32/Skia 控制台宿主，未启动消息循环）：`SplashFontFamily='Segoe UI'`；三个自绘文本块字体均为 `Segoe UI`，`fonts:` 家族从未出现在启动窗文本上（Ursa 模板自带的两个标题文本块用主题字体 `Inter`）；`BusyProgress` 的 `IsIndeterminate=True`、`Value=0`、`Maximum=100`、可见；状态文本依次为 `正在初始化... → 正在解压内置资源... → 正在读取设置... → 正在扫描设备... → 发现 3 台设备 / 未发现设备 → 正在启动应用服务... → 初始化完成`。
- 定向测试：`LocaleDictionaryTests` 6/6；`UotanStudio` 与 `UotanStudio.Tests` Debug 构建 0 错误 0 警告；`dotnet format ... whitespace|style --include` 两道门零报告。
- 全量测试：7229 总数 / 7222 通过 / 7 跳过 / 0 失败，单轮 1m26s。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，`UotanStudio.Desktop.exe` 154,245,120 B（147.1 MiB），只有第三方既有的 IL2026/IL2104/IL3053 警告；`正在解压内置资源`、`未发现设备`、`Splash_Resources`、`BusyProgress` 都能在 exe 中检索到。
- `dotnet build UotanStudio.slnx` 的 Desktop 复制步骤在有一个 `UotanStudio.Desktop` 实例运行时会被输出目录锁挡住（MSB3021/MSB3027），属环境冲突；Host 与 Tests 已分别构建通过。
- 未验证：真实窗口首帧观感（字体切换、进度条动画）与整体启动时长；启动窗改动没有自动化 UI 覆盖。

## 直连 USB ADB 投屏的端点占用实测（2026-10-24，Windows x64，Debug）

- 根因：设备详情补全（`getprop`/电池/存储批量与 `su` 探测）、日志抓取与投屏启动共用同一条独占直连 USB ADB 端点租约；补全在设备出现或列表刷新后自动运行并持有该租约数秒，而投屏启动既不等待也不重试，于是把控制器的独占拒绝原文（`Endpoint is already leased.`）当成启动失败原因。经 adb server 路由的详情补全走 server 会话，不会触发该冲突，因此只有 USB ADB 直连端点会失败。
- 修复：Comm 以 `DeviceEndpointBusyException`（派生自 `InvalidOperationException`，消息文本不变）区分“端点被占用”；宿主投屏启动（`StartMirror` 与 `StartOhscrcpyMirror`）只对这一异常做 15 s 有界等待（100→500 ms 退避），超时抛可操作的双语 `IOException`，独占语义与 fail-closed 策略不变。
- 定向测试：`DeviceEndpointBusyTests` 4/4（占用时抛类型化异常、占用租约释放后同一端点可再打开、等待后取得成功、持续占用转为 `IOException`）；`UotanStudio.Tests.Devices` 226/226；`MirrorSessionFacadeTests` 12/12；`McpBootProtocolToolTests` 9/9；`HeadlessControllerGatewayTests` 10/10。
- 全量测试：7250 总数 / 7244 通过 / 6 跳过 / 0 失败，单轮 2m41s。
- 构建与格式：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误（只剩既有的 `SpacemiTFlashTests` xUnit1051 警告）；`dotnet format ... whitespace|style --verify-no-changes` 两道零报告。
- 未验证：真实设备上详情补全与投屏启动的实际重叠窗口、15 s 预算是否覆盖最慢补全路径均未在硬件上复现；日志抓取长期占用端点时只覆盖了超时后的错误文案分支，GUI 呈现未逐项目视。

## 投屏端点占用的遗留持有者回收（2026-10-24，Windows x64，Debug）

- 现场：HDC 投屏会话在一次输入注入失败后结束（`Input injection failed` → `Restored the previous UI Test control agent`），约 20 s 后对同一物理设备发起 USB ADB 直连投屏，报 `Start failed: The device endpoint is busy`，且恰好在上一次改动引入的 15 s 预算耗尽时报出。
- 根因：投屏会话结束与独占设备端点归还之间没有和下一次启动串行化。上一轮只加了 15 s 有界等待，把"本宿主自己的上一次投屏会话仍持有端点"当成"其它操作占用"：既不主动回收，也等不到自愈。已关闭或被替换的投屏文档遗留的会话，对启动方与无关操作完全无法区分；设备端点只能有一个持有者，因此等待再久也不会成功。
- 修复：`DeviceMonitorService` 登记本宿主启动的投屏会话（按物理设备序列号，附带发现键以覆盖同一设备的 ADB 与 HDC 两个接口）；`AcquireForMirror` 在首次 `DeviceEndpointBusyException` 时先停止该设备遗留的投屏会话并立即重试一次，之后再进入原有的 100→500 ms 有界等待。其它进程/其它操作的占用语义不变，仍是有界等待后抛可操作的双语 `IOException`。
- 定向测试：`DeviceEndpointBusyTests` 6/6（新增"遗留会话被回收后无需等待即取得端点"与"回收无效时只回收一次并抛可操作 `IOException`"）；`UotanStudio.Tests.Devices` 231/231；`MirrorSessionFacadeTests` 12/12。
- 构建与格式：本次改动的四个文件 `dotnet build` 与 `dotnet format whitespace|style --verify-no-changes` 全部通过。
- 未验证：真实设备上"遗留会话 → 新投屏启动"的时序与回收耗时；本轮没有可用硬件，GUI 表现未目视。
- 环境冲突（当轮）：工作区里 `ScreenMirrorGpuPresenter` 已删除 `CompositionFramePresented`，而 `ScreenMirrorViewModel` 与 `ScreenMirrorVideoControl` 仍引用它，属并行会话的未完成改动，使工作区的 `UotanStudio` 与测试宿主无法整体编译；本轮的构建与定向测试在把这些 UI 文件回退到 HEAD 的隔离副本中完成，没有修改那些文件。

## 投屏启动被后台设备详情占用端点的实测（2026-10-24，Windows x64，Debug）

- 现场：设备列表刷新后立即对同一台设备发起 USB ADB 直连投屏，日志先出现 `USB - async bulk IN chunk failed with native error 995`，随后 scrcpy 服务就绪，10 s 后 `Start failed: The device endpoint is busy`（正好耗尽 15 s 等待预算）。
- 根因：独占直连 USB ADB 端点同时被"设备详情补全"借用，而补全整段无界——每条 shell 命令走 `ExecuteCommand` 的 30 s 默认期限且不接取消（一次补全最多 7 条：`getprop`/`wm size`/`wm density`/`dumpsys battery`/`/proc/meminfo`/`df`），其后 `HasSuAsync` 又传 `CancellationToken.None` 并额外探测 2×6 s。投屏启动只等待 15 s，因此刷新触发的补全必然把用户发起的投屏压成"端点占用"。日志里的 995 是竞争会话被拆除时被中止的批量 IN：异步路径把它折叠成零字节非超时读，协议循环当作空闲继续等待，既不报错也不释放端点。
- 修复：宿主把补全登记为"后台端点工作"（`BeginBackgroundEndpointWork`），投屏启动在首次占用时先取消该设备仍登记的后台工作（`ReleaseMirrorEndpointHolders`，与遗留投屏会话回收同一入口）再立即重试；同一设备的 ADB/HDC 接口按 `PhysicalId` 归并（`IsSamePhysicalTarget`），遗留会话回收也改用该匹配。补全本身加上 20 s 总预算与 8 s 单条命令预算，并把令牌一路传给 `RunAdb`、`HasSuAsync` 与 shell 客户端。Comm 侧把 `ERROR_OPERATION_ABORTED`(995) 视为致命批量传输错误（与同步 `ReadChunk` 一致），使"传输被中止"以故障上报而不是静默零字节读。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误、2 条既有 `xUnit1051` 警告；`DeviceEndpointBusyTests` 10/10（新增物理身份匹配 2 例、后台工作取消 2 例）；`WinUsbAbortedTransferTests` 2/2；`UotanStudio.Tests.Devices` 237/237；改动文件的 `dotnet format` whitespace 与 style 两道门零报告。
- 未验证：没有可用硬件，真实设备上"刷新 → 补全 → 投屏"的重叠窗口、抢占后 15 s 预算是否足够、995 改为致命后各协议在真机上的重试表现都未复现；GUI 未见。

## 设备序列号从启动后回退到 VID:PID 的实测（2026-10-24，Windows x64，Debug）

- 现场：启动后设备行先显示真实序列号，很快变成 `VID:PID`（如 `18D1:4EE7`）。
- 根因：Windows 上序列号只有两条来源——设备实例 ID 的最后一段（Windows 首次插入读到 iSerial 时才会是序列号，否则是含 `&` 的定位串）与一次 `WinUsb_GetDescriptor` 窥视（`PeekSerialNumber`）。窥视要临时打开接口，一旦本程序或其它进程已占用该接口就返回 null，而 `DeviceMonitorService.Discovery` 在序列号为 null 时回退成 `{VID:X4}:{PID:X4}`。因此"先有序列号、随后消失"是重枚举时窥视失败，不是设备不再上报序列号；同一次丢失还会让 `BuildDeviceKey` 从 `serial:` 落到 `path:`，造成条目身份抖动。
- 修复：Comm 新增 `FifoGate`（严格到达顺序的串行闸门，按排队顺序放行）与 `UsbSerialCache`（按设备实例 ID 限定、30 分钟有效的正向条目，加 2 秒的失败抑制条目）；`WinUSBFinder` 在创建设备前用 `ResolveEnumeratedSerial` 统一解析序列号（实例 ID 后缀 → 缓存 → 受闸门保护的单次窥视），设备就绪后再把 `SerialNumber` 回写缓存。窥视失败时沿用此前读到的序列号，不再降级成 `VID:PID`；实例 ID 在重新插入时改变，过期序列号不会带到另一次连接。协议指令本身早已由每设备 `DeviceCommandDispatcher`（`Channel` + 单读者）按 FIFO 串行执行，原先落在池外的只有这次描述符窥视。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误、2 条既有 `xUnit1051` 警告；`UsbSerialCacheTests` 6/6（命中、换实例不串号、过期、空白忽略、失败抑制、无实例条目兜底）；`FifoGateTests` 2/2（持有期间后者排队、三个排队者按到达顺序放行）；`UotanStudio.Tests.Devices` 245/245；改动文件 `dotnet format` whitespace 与 style 两道门零报告。
- 未验证：没有真实 USB 设备，未复现"启动 → 序列号 → 回退"的现场时序；接口被占用时窥视失败的次数、重枚举频率与 2 秒抑制是否匹配真机未实测。

## 投屏端点长期被占用与详情缺失的传输层根因（2026-10-24，Windows x64，Debug）

- 现场：抢占与回收上线后仍报 `The device endpoint is busy`，同时设备管理器不再显示设备详细信息；两者同时出现说明端点没有回到池里，而详情补全本身也需要同一条独占端点。
- 根因：`OverlappedTransferAsync` 取消/超时后先 `CancelIoEx`，再**无界等待**驱动确认中止（原注释明确写着"按 UAF 防护保持无界"）。驱动若始终不确认，ADB 消息循环就永远不结束，`DeviceLease` 的清理顺序（流 → 会话 → 传输 → 端点）卡在会话一步，端点占用永不归还：投屏等 15 s 后报占用，详情也被同一条端点拒绝。上一轮已把批量传输的 995 归为致命，但致命只在传输层抛错，仍然跨不过这个无界等待。
- 修复：中止确认改为 **5 s 有界**；超时后把固定的 `OVERLAPPED` 与事件放入 `QuarantinedTransfers` 隔离区（绝不释放内核可能仍引用的块），并以 `ERROR_OPERATION_ABORTED` 让会话失败，使租约完成清理、端点回到可用状态。详情补全的单条命令期限从 8 s 恢复到 30 s（总预算 45 s），避免慢设备上"详情整段失败"；查询失败会写一条 `DeviceManager` Debug 日志，便于定位是超时还是取消。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误、2 条既有 `xUnit1051` 警告；`UotanStudio.Tests.Devices` 245/245；改动文件 `dotnet format` whitespace 与 style 零报告。
- 未验证：没有真机，`CancelIoEx` 不确认的路径只能靠代码复核，无法在硬件上复现；隔离区在长时间反复卡死时的内存占用（每次约一块固定 OVERLAPPED + 一个事件）未压测。

## 中止批量传输不再杀死会话、卡死端点可被强制摘除（2026-10-24，Windows x64，Debug）

- 现场：投屏仍报 `The device endpoint is busy`，`FirstChanceExceptionLogger` 每 5 s 记录一条默认文案的 `TimeoutException`，随后出现 `ADB handshake failed: …已中止 I/O 操作`（Win32 995）。详情补全同样拿不到端点，所以设备管理器没有详细信息。
- 根因：995 表示"这次重叠传输被中止"，管道本身仍然可用；上一轮把它判为致命，于是每次中止都让整个 ADB 会话失败（新会话连握手都失败），而真正的问题——某个持有者一直不放端点——被 15 s 预算掩盖成"端点占用"。此外超时中止的确认等待与读取超时都是 5 s，导致日志里每 5 s 一条 `TimeoutException` 的规律节奏。
- 修复：`RecoverAbortedTransfer` 复位受影响管道并上报可重试超时，让协议循环重新发起传输；只有句柄已关闭或连续 3 次中止才判致命，避免"一有中止就死会话"和"无限空转"两端极端。投屏启动的等待预算耗尽后改为升级处理：`DeviceController.DescribeReservations` / `ForceDetachReservations` 强制摘除仍占用该端点地址的租约（日志写明 `protocol/route` 占用者），等占用归还（≤5 s）后重试一次，因此不再把卡住的持有者变成永久占用。详情补全的时限保持 30 s/条 + 45 s 总预算并及时记录失败原因。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误、2 条既有警告（`xUnit1051`、`HdcSessionHandleTests` 的 `xUnit1031`）；`DeviceEndpointBusyTests` 12/12（新增"强制摘除后成功"与"摘除无效只摘一次并抛可操作 IOException"）；`UotanStudio.Tests.Devices` 247/247；改动文件 whitespace 与 style 零报告。
- 未验证：没有真机，995 的复位恢复与强制摘除的真实时序未复现；强制摘除可能中断用户正在进行的文件传输，只在投屏预算耗尽后触发一次。

## Comm 链路诊断：指令池、文件传输、原始负载与链路状态日志（2026-10-24，Windows x64，Debug）

- 目标：把"链路日志"从只有 USB 层的可选痕迹，扩到指令池进出、协议步骤、文件传输、原始数据和端点租约状态，同时保证默认不刷屏、绝不把文件内容整段写进日志。
- 实现：新增 `CommTrace`（`Off/Error/Info/Debug/Trace` 五级、分类 + 级别汇接口 `ICommLogger`、默认写错误流）、`PayloadPreview`（十六进制 + ASCII 的有界预览）与 `ConsoleCommLogger`；级别取自 `UOTAN_COMM_LOG`，原始负载是第二个显式开关 `UOTAN_COMM_RAW=1`，预览上限固定 `MaxRawPayloadBytes = 256`。插桩点：`DeviceCommandDispatcher` 入队/开始/完成/失败/取消/释放（等待深度、结果类型与文本长度）、`AdbSyncClient` 的 push/pull（远端路径、模式、总字节、块数、耗时、逐块 trace、失败与设备拒绝）、`AdbShellClient.ExecuteAsync`（命令摘要、退出码、stdout/stderr 字节数与负载预览）、`DeviceController` 的租约获取/归还/释放占位摘除/强制摘除/占用冲突（带 `DescribeReservations` 明细）、`UsbDevice.EmitTransfer` 的抓帧负载。宿主 `UsbLogBridge` 同时实现 `IUsbLogger` 与 `ICommLogger`，`App.axaml.cs` 里的 `UsbLogBridge.Install()` 一次装好两个汇，级别映射为 Error/Info/Debug/Trace 并以 Comm 分类作为来源名。
- 现场坑：第一版把等待深度写成 `_channel.Reader.Count`，该无界通道在这个配置下直接抛 `NotSupportedException`，使 `DebugBridgeRouteTests` 4 例在租约释放时失败；改为 `Interlocked` 维护的 `_pending` 计数后恢复。
- 实测：`dotnet build UotanStudio.Comm/UotanStudio.Comm.csproj -c Debug` 0 警告 0 错误；`dotnet build UotanStudio/UotanStudio.csproj -c Debug` 成功（宿主桥编译通过）；`dotnet build UotanStudio.Tests/UotanStudio.Tests.csproj -c Debug` 仅 2 条既有警告（`xUnit1051`、`xUnit1031`）；新增 `UotanStudio.Tests.Diagnostics` 25/25（`PayloadPreviewTests` 5 例：空负载、可打印、截断、不可打印、下限夹紧；`CommTraceTests` 20 例：级别解析、级别闸门、`Off` 下 Error 仍落盘、原始捕获双开关）；`UotanStudio.Tests.Devices` 247/247；`UotanStudio.Tests.Protocols` 36/36；11 个改动文件 `dotnet format` whitespace 与 style 两道门零报告。
- 未验证：没有真实 USB/ADB 设备，日志内容只在单元测试与代码复核层面验证；`UOTAN_COMM_RAW=1` 下长时间大文件传输的日志体积、`Trace` 级逐块输出对投屏与传输吞吐的实际影响未压测；宿主日志面板中新分类的显示与过滤未逐项目视。

## Comm 潜在缺陷静态排查与两处修复（2026-10-24，Windows x64，Debug）

- 方法：对 `UotanStudio.Comm` 全库做模式扫描（`async void`、sync-over-async、空 `catch`、`CancellationToken.None`、`Task.Run` 后不观察、`Thread.Sleep`、`Marshal.AllocHGlobal`、`ArrayPool` 配平、`new TaskCompletionSource` 选项、`while (true)` 退出条件、wire 长度上界、`DeviceEndpointId` 相等性），再逐个复核命中点。
- 已修复 1：`UsbStream.Write` 把 `Session.Write` 的返回字节数直接丢弃。USB 批量写按块循环，超时/错误时只写入部分块并返回实际计数，而 `Stream.Write` 无法表达部分成功——原实现把剩余字节静默丢掉。现在不足 `count` 即抛 `IOException`；新增 `UsbStreamWriteTests` 2/2（全量写入不抛、短写抛出并带 `2 of 4 bytes`）。
- 已修复 2：`UsbApiProviderBase.SafeEnumerate` 的未知异常分支只记“unknown exception”，丢掉类型与消息，枚举失败无法定位。改为记录 `{类型}: {消息}`。
- 复核为非缺陷：`EdlPhysicalRange.ValidateAgainst` 与 `AdbQrPairingService` 里 `_ =` 丢弃的是“只为触发校验而调用”的结果（校验靠抛异常）；`DeviceController.Dispose` 的 `_ = Task.Run` 内部逐租约 try/catch，不会漏释放；`FifoGate`/`DeviceCommandDispatcher`/`WinUSBDevice.AsyncIO` 的 `TaskCompletionSource` 全部带 `RunContinuationsAsynchronously`；`DeviceEndpointId` 是 `readonly record struct`，值相等性可用于租约字典；ADB 入站长度受 `AdbConstants.MaxPayload`（1 MB）与对端 `A_MAX_PAYLOAD` 双重夹紧，shell v2 帧 16 MB、ADB server 失败文本 65535 字节均有上界。
- 保留观察项（未改动，缺硬件验证）：`SerialTransport.ReadAsync/WriteAsync` 用 `Task.Run(阻塞串口调用, token)`——令牌无法中断已开始的阻塞读，取消后该读仍可能消费字节，需真机确认串口帧是否会被错切；`HdcServerTaskChannel.ReadExactly`、`AdbServerServices` 的 `.AsTask().GetAwaiter().GetResult()` 属 sync-over-async（无同步上下文故不致死锁，但有线程池阻塞）；`HdcForwardTunnel.ConnectLoopback` 的 `Thread.Sleep(250)` 与 13 处 `Thread.Sleep` 均为阻塞等待；`AdbMessage`/`HdcConnection` 的裸 `catch` 均带日志或状态收敛。
- 实测：`UotanStudio.Comm` 0 警告 0 错误；`UotanStudio.Tests.Devices` 247/247、`UotanStudio.Tests.Protocols` 37/37、`UotanStudio.Tests.Diagnostics` 27/27；改动文件 whitespace 与 style 零报告。
- 未验证：纯静态审计 + 模式扫描，没有真实 USB/串口/HDC 设备；Linux/macOS 后端未逐行复核；上述观察项均未在硬件上复现。

## ADB 握手失败可用原始帧定位（2026-10-24，Windows x64，Debug）

- 现场：`ADB handshake failed: Invalid ADB message magic!` 只有一句 `[Debug] Exception`，没有收到的原始字节，无法判断是失步、另一个读取方分走流，还是读错了接口。
- 修复：`AdbConnection.ReadMessageAsync` 对每帧记录原始 24 字节头部（首帧 `Debug`、其余 `Trace`，十六进制 + ASCII 预览）；`ParseHeader` 抛错时额外写一条 `Error` 级日志，携带完整 24 字节头、解析出的 command/magic/期望值，以及“命令合理 = 从中途开始读；完全陌生 = 另一个读取方或错误接口”的判读提示；入站负载超限同样在 `Error` 里带头部；`ThrowIfLoopFaulted` 在重新抛出前先按 `Error` 记录捕获到的故障类型、消息与传输实现名。负载本体只在 `UOTAN_COMM_RAW=1` 且 `Trace` 级时按 256 字节预览记录。
- 实测：`UotanStudio.Comm` 0 警告 0 错误；`UotanStudio.Tests.Diagnostics` 27/27（新增 `CommTrace` 字符串重载两条：`Error` 不受级别屏蔽、`Debug` 受屏蔽）；`UotanStudio.Tests.Devices` 247/247、`UotanStudio.Tests.Protocols` 37/37；`UotanStudio.Comm/Protocols/Adb/AdbConnection.cs` 等三个改动文件 whitespace 与 style 零报告。
- 未验证：没有复现该握手失败的现场；新日志本身未在真机上产生过，`Trace` 级每帧头部的日志体积在文件传输时未测。

## ADB 帧头出现陌生素节时的归属判定（2026-10-24，Windows x64，Debug）

- 现场：`rx #1 header rejected (UsbDeviceTransport): 24 B: FC 5F 82 CF …` 与重发 CNXN 后的 `rx #2` 都是高熵、每次不同的字节；链路描述为 `05C6:90DB iface FF/42/01 ep in 86 out 03`，接口按描述符确实声明为 ADB（`WinUsb_QueryInterfaceSettings` 与管道查询取自同一接口），所以不是"选错接口"的普通情形。
- 判定手段（新增日志，不改协议）：`WinUSBDevice` 在 open 后按 `Debug` 打印配置描述符里的全部接口（编号、类别三元组、每个端点的地址/类型/包长）与实际绑定的 alt setting 与 in/out 管道，用来确认 0x86/0x03 是否真的属于 FF/42/01；`AdbConnection` 在一次性重同步用掉之后再做最多 3 次 300 ms 短读，逐次记录"到达耗时 + 十六进制"，从而区分三种情况：300 ms 内再无字节（首帧只是残留脏数据，链路本身可用）、持续到达（端点承载的是别的服务）、以及恰好读到合法 ADB 头（对端已恢复，但该帧被探测吃掉，需重建会话）。`UsbDeviceTransport` 增加并发读者检测：同一条传输上出现第二个入站读者时按 `Debug` 记录 `transport #N … now has K concurrent inbound reads`——这正是"残留会话与新会话共用一条管道、把帧切开"的直接证据。
- 复核：`UotanStudio.Mirroring` 内对 `DeviceLease`、`GetStream()`、`UsbStream` 的引用为 0，投屏数据只能作为 ADB `WRTE` 载荷走同一条连接，因此"残留投屏"本身不会在帧头位置产生陌生素节；只有当同一传输被两个 ADB 读者共享（旧会话未完全退出、TLS 升级重叠等）时才会。
- 实测：`UotanStudio.Comm` 0 警告 0 错误；`UotanStudio.Tests.Devices` 247/247、`UotanStudio.Tests.Protocols` 37/37、`UsbStreamWriteTests` 2/2；三个改动文件 whitespace 与 style 零报告。
- 未验证：没有复现该设备现场；并发读者检测与外来流探测都只经过编译与既有用例回归，尚未在真机上触发过一次。

## ADB 直连端点持续流出非 ADB 数据时的描述符取证（2026-10-24，Windows x64，Debug）

- 现场（用户实测日志）：`rx #1`/`rx #2` 与随后三次探测帧都是高熵、每次不同的字节，且三次探测的到达耗时均为 **0 ms**；全新启动、不点投屏同样复现；`adb devices` 能看到该设备。串行重同步与"残留会话"两条假设因此被排除：端点是在**持续全速**输出非 ADB 数据，不是残留脏字节，也不是投屏会话仍在写。
- 处置：`UsbDevice.EnumeratedInterfaces` / `UsbDeviceInfo.EnumeratedInterfaces` 保存配置描述符里的**全部**接口（此前 `WinUSBDevice` 解析出来后丢弃，只上报被绑定的那一个），`AdbConnection.DescribeLink` 在每条握手失败日志后追加 `descriptor interfaces: #NN class/sub/proto eps [addr/attr/pkt …]`。这样一次失败日志就能回答：被绑定的 `0x86/0x03` 是否真的属于 `FF/42/01`，以及设备是否还有别的候选接口。
- 判定口径（供现场对照）：探测帧仍在 `0 ms` 到达 = 端点承载活的服务；若 `adb devices -l` 列出的是 `host:port`（网络 adb），说明手机的 USB adb 功能在当前复合里没有起来，直连读到的就是厂商诊断流；若列出的是 USB 序列号（`6b24c48f`），server 能驱动同一接口而我们不能，则是批量端点对选错（常见 ADB 对为 `81/01`，本例为 `86/03`），下一步应按对选端点并加"换对重试"。
- 实测：`UotanStudio.Comm` 0 警告 0 错误；`UotanStudio.Tests.Devices` 247/247、`UotanStudio.Tests.Protocols` 37/37；五个改动文件 whitespace 与 style 零报告。
- 未验证：新增的 `descriptor interfaces` 日志还没在现场产生过；端点对改选（`81/01` 优先 + 换对重试）尚未实现，需上一条判定结果后再动。

## 端点占用残留与刷新卡顿（2026-10-24，Windows x64，Debug；含计算机操作实测）

- 现场一：应用启动即报 `Endpoint is already leased.`（当时本机未连接任何 Android 设备），设备详情、设备日志与投屏全部失败。
- 现场二：真机日志里 `opened … bound alt setting 00 (FF/42/01)` → `released reservation` → 约 2 s 后投屏启动失败 `Endpoint is already leased.`，同时界面刷新卡住约 5 s 且无动画。
- 根因（Comm 租约表）：强制分离会把租约从 `_openLeases` 摘除，但 `_leased` 条目保留为“释放中占位”，直到该租约完成异步清理才归还；清理若永不完成（设备消失后传输无法 dispose），端点就永久不可再打开。`DescribeReservations` 只读 `_openLeases`，对外表现为“没人占用”，于是宿主“强制摘除后再等归还”的循环既等不到也等不对，最后把原始占用错误抛给用户。
- 修复（Comm）：占位超过 1 s 视为清理卡死，在 `OpenTransportAsync`/`AdoptSession` 取得占用时回收并记错误日志说明卡的时长；`DescribeReservations` 把释放中占位报告为 `source/transport releasing placeholder`。存活租约绝不被抢占。
- 修复（宿主）：`OpenAdbLease` 遇到占用时先记录实际占用者、摘除该占用并重试一次，仍失败才抛出带占用者清单的 `IOException`；设备详情补全的 MTK 身份查询与 EDL 探测由同步等待改为 `Task.Run` 后台执行（原先 5 s 预算会连带卡住界面线程）；`DeviceMonitorService.Scan()` 的锁等待上限 250 ms，超时返回上一份快照并记录耗时。
- 计算机操作实测（本机，无真机）：修复前启动的应用，设备日志窗口稳定出现 `Endpoint is already leased.`；修复后（新构建）同一场景该错误消失，日志窗口正常渲染条目，UIA 查询在 0.39–0.74 s 返回，界面线程无阻塞。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误；`Devices` 247/247、`Protocols` 41/41、`Media` 204/204（1 跳过）；改动文件 whitespace 与 style 零报告。
- 未修复/未验证：投屏窗口被其它窗口遮盖后“仍显示投屏中”的问题本轮未改动——`ScreenMirrorCompositionRecovery` 只覆盖可视对象分离/重新挂载，没有窗口遮挡驱动的会话暂停与状态复位；该问题需要真机复现后再加钩子。

## 端点占用残留与刷新卡顿（2026-10-24，Windows x64，Debug）


- 现场：应用刚启动就报 `Endpoint is already leased.`（当时机器上没有连接任何 Android 设备），设备详情刷新与投屏都因此失败；设备日志窗口显示同一条占用错误。日志可见一次 `released reservation` 之后约 2 s 又出现占用冲突。
- 根因（Comm 租约表）：强制分离（设备移除、server 消失、投屏急停）会把租约从 `_openLeases` 摘除，但 `_leased` 中的条目保留为“释放中占位”，直到该租约完成异步清理才归还。清理若永不完成（传输在设备消失后无法 dispose），该端点就永久不可再打开；而 `DescribeReservations` 只读 `_openLeases`，对外表现为“没人占用”，重试与“强制摘除后再等”的循环因此既等不到也等不对，最终把占用错误抛给用户。
- 修复（Comm）：占位超过 1 s 视为清理卡死，在 `OpenTransportAsync`/`AdoptSession` 取得占用时回收并记一条错误日志说明卡的时长；`DescribeReservations` 现在把释放中占位报告为 `source/transport releasing placeholder`，使“谁占着”的诊断与等待逻辑说真话。存活租约绝不被抢占。
- 修复（宿主）：设备详情补全的 MTK 身份查询与 EDL 协议探测原先在调用线程上直接等待（内部同步阻塞），5 s 预算会把界面线程一起卡住——现改为 `Task.Run` 后台执行；`OpenAdbLease` 取不到端点或租约时按“无匹配直连端点 / 租约被拒”分别记 Warn，不再只给一句 `Unable to open an ADB session for the device.`，消息里带上序列号、端点键与实际路由。
- 实测：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误；`UotanStudio.Tests.Devices` 247/247、`Protocols` 41/41、`Media` 204/204（1 跳过）；改动文件 whitespace 与 style 零报告。
- 计算机操作实测（本机，无真机）：重启后的应用正常启动，UIA 查询在 0.38–0.52 s 返回（无界面线程阻塞），设备日志窗口给出新的占用/会话错误文案；因为没有连接设备，投屏与详情补全的真机路径未覆盖，1 s 占位回收与 5 s 卡顿改善需要真机复跑确认。

## 刷新卡顿与 logcat 超时（2026-10-24，Windows x64，Debug）


- 现场日志三簇：`Clearing logcat buffers timed out`（ADB shell 命令 10 s 无应答）、`AdbConnection.SendAsync` 的 `TaskCanceledException`（等待发送闸门时调用方令牌触发，不是传输错误）、每 11 s 一簇共 11 次 `OperationCanceledException`（日志视图捕获被反复拆掉重启，每次重启取消一批在途 ADB 操作）。本次没有任何 `adb-link` 报错，说明端点不再流出陌生帧，而是**完全不应答**——与"另一进程在共享接口上持续读数"是两种不同的失败形态。
- 计时定位的宿主缺陷：`DeviceMonitorService.Scan()` 在 `_scanLock` 内跑完整 USB 枚举（含 server 探测与 fastboot 序列号控制传输，端点异常时可达 10 s），期间任何调用 `Scan()` 的界面路径都被阻塞，表现为点击刷新后整个窗口卡死数秒。改为 `Lock.TryEnter` 上限 250 ms，超时直接返回上一份已完成快照并记日志；完整扫描超过 400 ms 记录耗时，控制器刷新单独记一条，下一次现场日志可直接看到秒数花在哪一段。
- 日志视图抖动：同一设备的捕获失败后，任意一次设备列表刷新（状态抖动）都会重启捕获，于是每轮 ≈11 s 重复"清空缓冲区 10 s 超时 + 一批取消"。改为同设备重启带 5 s 退避，成功启动清除退避。
- `ClearLogcatBuffersAsync` 的 `TimeoutException` 原先丢弃内部异常也没有上下文；现在带上耗时、预算、实际路由（server/direct）、是否看到成功标记、设备侧诊断输出，并保留内部 `OperationCanceledException`。
- 实测：`UotanStudio` 0 警告 0 错误；`UotanStudio.Tests.Devices` 247/247；三个改动文件 whitespace 与 style 零报告。
- 未验证：无真机，冻结时长与该链路下的 logcat 恢复只能靠下一次现场日志确认；投屏与 logcat 是否可用取决于链路能否执行 shell 命令，本轮未改变设备侧握手结果。

## 直连 ADB 与运行中的 adb server 共用同一 WinUSB 接口（2026-10-24，Windows x64，Debug）

- 现场：`adb devices` 能看到该设备（USB），直连握手却读到持续全速、每次不同的高熵帧（探测三次均为 0 ms）；全新启动且不投屏同样复现。`UotanStudio.Mirroring` 对裸管道零引用，投屏数据只走 ADB `WRTE` 载荷，因此"残留投屏自身在写管道"被排除。
- 根因：Windows 的 WinUSB **允许同一接口被多个文件句柄同时打开**，驱动不做独占。当 PC 上的 adb server（例如遗留的 `adb logcat`、Android Studio、被孤儿化的 `adb.exe`）已经附着在该接口上时，我们的直连会话仍能打开成功，于是两个读者把同一条 ADB 字节流切片：对方帧中间的载荷落到了我们的帧头位置，表现为高熵、每次不同、0 ms 到达。路由层本应在这种情形下拒绝直写，但 `DebugBridgeSessionOpener.AnyServerListening` 在探测抛异常（超时、DNS、连接重置）时返回 `false`，把"无法判定"当成"没有 server"（fail-open）；`AdbServerOwns` 又只用启动快照，server 后启动时同样放行。
- 修复：`AnyServerListening` 改为 fail-closed——探测无结论按"server 存在"处理并记录一条 `route` 级日志说明原因；`AdbServerOwns` 在 Auto 模式下对"快照说没有 server"补一次实时探测，避免启动快照过期的时间窗；ADB 握手在判定"端点持续流出非 ADB 数据"时附带一次实时 server 探测结论（`An ADB server is listening on 127.0.0.1:5037 …` / `No ADB server is listening …`），使日志自身就能区分"别人占着"与"设备端点不是 ADB"。
- 现场自证：`adb kill-server`（或结束遗留 `adb.exe`）后直连恢复 = 共用接口确认；`adb devices` 为空且直连仍乱码 = 设备复合/端点问题。
- 实测：`UotanStudio.Comm` 0 警告 0 错误；`UotanStudio.Tests.Devices` 247/247、`UotanStudio.Tests.Protocols` 38/38（`--no-build`，测试工程当次因并行会话的在途改动 `AdbShutdownFarewellTests.cs` 无法重新编译）；改动文件 whitespace 与 style 零报告。
- 未验证：没有在同类现场复跑过 `kill-server` 前后对比；fail-closed 后"server 在监听但未接管该设备"的情况下直连也会被拒绝，是否会误伤需要真机确认。

## 共享 ADB 会话消除端点抢占与界面卡顿（2026-10-25，Windows x64，Debug；真机 GUI 实测）

- 现场（用户日志）：`The ADB lease for … is still held by ; detaching that holder …`（占用者文本为空）、约 2 s 后 scrcpy 就绪、随后每 5 s 一条 `TimeoutException`，几分钟后 `Opening the ADB lease … returned no lease`；同时"设备日志刷一段时间后收不到传输""设备管理器没有详细信息""投屏被别的窗口遮盖后不恢复、仍显示投屏中""刷新设备详细信息整程序卡约 5 s"。
- 根因：直连 USB ADB 端点被宿主每个消费方各自以独占租约打开（日志抓取、详情补全、root 探测、shell、投屏），冲突时 `OpenAdbLease` 直接强制摘除对方租约——日志抓取与投屏互相拆掉对方的会话，端点清理卡住时还会出现"没有租约"；`EnrichAdbInfoAsync` 的 root 探测仍在 UI 线程上同步取得租约；`FirstChanceExceptionLogger` 无条件把被正常捕获的 `TimeoutException`/`OperationCanceledException` 写进全局日志。
- 修复（会话）：所有直连 USB ADB 消费方改为借用 `DeviceMonitorService` 的共享会话（`SharedAdb.cs`：引用计数、断链会话立即出池、退出时统一释放），投屏服务端也跑在同一连接上（`ManagedMirrorSession`/`StartScrcpyOverUsbSession` 新增 `ownsAdbSession`，共享时由借用作用域归还连接）；`OpenAdbLease` 不再强制摘除任何占用者，占用诊断始终给出具体占用者或"仍在释放中的占用"。
- 修复（界面与日志）：详情补全的属性查询与 root 探测全部留在工作线程；投屏侧增加关键帧节流（750 ms）、静默 5 s 请求关键帧、清理/结束时状态离开 `ScreenMirror_Mirroring`，窗口激活、最小化还原与 Dock 文档重新挂载时重挂合成表面并请求关键帧；日志抓取失败或无输出时按 5 s→60 s 退避自愈，单 tick 刷入上限 250 条 / 64 KiB；`FirstChanceExceptionLogger` 默认关闭，`UOTAN_FIRST_CHANCE_LOG=1` 才记录。
- GUI 实测（真机 `6b24c48f`，USB 05C6:90DB ADB 接口；无 adb server，直连路由）：设备管理器读全序列号/品牌/型号/Qualcomm/Android 9/SDK 28/arm64-v8a 与"1 个设备已发现"；设备日志在投屏期间与滚动后持续出新行（滚到顶部再回到底部，最新一条与当前时间相差 ≤ 20 s）；点击投屏后属性面板显示 `投屏中` / H264 / Media Foundation (GPU) / 1440 × 2880，全局日志出现 `Composition presentation active: D3D11 shared texture`；用记事本覆盖主窗口 6–7 s 后切回，视频区像素差异 15768–29657 px 而属性区 0 px 变化，再注入"主屏幕"后视频区再变 21940 px，说明画面恢复且控制通道可用；刷新详情按钮后 UIA 查询 659 ms 返回且 `targetResponsive=true`；全局日志全程只有 Info 与一条关键帧 Debug，无 Exception/Warn/Error；关闭程序两次均干净退出（第二个实例 `CloseMainWindow` 后 6 s 内 `EXITED_CLEAN`）。
- 实测（自动化）：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误（2 条既有 xUnit 分析器警告）；`UotanStudio.Tests.Devices` 245/245、`UotanStudio.Tests.Mirroring` 111/111（1 硬件跳过）、`UotanStudio.Tests.Media` 204/204（1 Linux 跳过）、`UotanStudio.Tests.Architecture` 16/16、`FirstChanceExceptionLoggerTests` 6/6；改动文件 whitespace 与 style 两道门零报告。
- 未验证：没有 HDC（HarmonyOS）目标，`EnrichHdcInfoAsync` 的同步前缀与 ohscrcpy 路径未复测；server 路由（adb server 拥有目标）仍按每次调用开会话，未在真机验证其与投屏的并发；没有复现用户 04:45 的现场时序（租约被摘除后 5 分钟才"没有租约"）；长时间（>1 h）日志与投屏共存、拔插重连、NativeAOT 发布与 macOS/Linux 路径均未执行。

## 投屏标签切换后黑屏：视频数据在收、画面不再呈现（2026-10-25，Windows x64，Debug；真机 GUI 实测）

- 现场：打开投屏后切到其它文档/标签再切回，画面全黑，属性仍显示"投屏中"，触摸与按键注入正常，等待 12 s 不恢复。
- 复现（真机 05C6:90DB）：投屏后切到"交付"页 3 s 再切回，视频区平均亮度 44.4 → 4.83（max 49），145913 个采样像素变化；保持 12 s 仍为 4.83。
- 根因：`ScreenMirrorGpuPresenter` 在可视对象离开可视树 250 ms 后拆除合成表面（原延迟去抖的到期动作）。直连 USB 的 GPU 零拷贝路径把解码帧交给合成表面消费，表面被拆掉后解码器的导出环失去消费者：帧被丢弃、`MarkPresented` 不再发生，解码器随后不再产出帧。诊断日志证实切换后 `bytes` 持续增长（8.2 MB→14.2 MB）而 `decoded`/`presented` 冻结在 87/83，且 `surface=True hasFrame=False`——视频数据在到、解码/呈现管线停摆，因此显示"投屏中"却没有画面；控制通道独立于视频，按键仍可用。
- 修复：① `ScheduleDetachCompositionVisual` 不再拆除表面，只把呈现标记为暂停（`_hostDetached`），表面、导入与表面视觉对象继续由呈现器持有；真正关闭文档仍由 `DetachCompositionVisual`/`Dispose` 释放。② 增加管线停滞看门狗：有视频字节到达但 1.5 s 内没有新解码帧时请求关键帧（`RequestKeyFrame` 自带 750 ms 节流），5 s 仍无解码帧则以可恢复错误触发一次解码器重建（Windows 上从 GPU 导出降到 CPU 回读），使任何路径下的静默黑屏都能自愈。③ 删除不再使用的延迟分离令牌（`ScreenMirrorCompositionRecovery.ScheduleDetach`/`IsDetachCurrent`/`CancelDetach` 与呈现器的 `DetachDebounce`/`_detachTimer`）。
- 实测（真机 GUI，两轮）：切走 3 s 再切回后视频区平均亮度 72.68 → 72.59（max 255），仅 832/569232 个采样像素变化（时钟级变化）；随后注入"主屏幕"使视频区变化 126659 个采样像素，证明画面仍在实时更新且控制可用；最终二进制复跑一次为 44.52 → 36.50（max 255）、62952 个采样像素变化，无黑屏。
- 实测（自动化）：`dotnet build UotanStudio.slnx --no-restore -c Debug` 0 错误；`UotanStudio.Tests.Media` 202/202（1 Linux 跳过；删除 2 个已失效的延迟分离令牌用例）、`UotanStudio.Tests.Mirroring` 111/111（1 硬件跳过）、`UotanStudio.Tests.Devices` 245/245、`UotanStudio.Tests.Architecture` 16/16；改动文件 whitespace 与 style 两道门零报告。
- 未验证：本机只有一台设备，未用两台真机同时开两个投屏标签逐一切换；本次验证的是同一套"隐藏文档→重新显示"路径（切到交付页再切回），诊断日志已证明停摆发生在呈现侧而非设备侧。隐藏期间每个打开的投屏文档仍占用一份合成表面，未做多文档长时间显存压测；macOS IOSurface 与 Linux VA-API 导出路径同样受益于"暂停而不拆除"，但未在对应平台验证。

## CI 门禁现状

`.github/workflows/ci.yml` 跑 `setup-dotnet` 取 10.0.x、构建、whitespace 与 style 两道 `dotnet format`，然后 `dotnet test --solution UotanStudio.slnx --no-restore -c Release --minimum-expected-tests 6000`，Test 步骤 `timeout-minutes: 15`，不设置任何 `UOTAN_*`，因此门控用例自跳。`.github/workflows/release.yml` 只负责 NativeAOT 发布。套件规模再变化时，Test 步骤的下限要跟着实测调，而不是靠放宽来掩盖删除。

## 门禁覆盖不到的地方

- `UotanStudio.Benchmarks` 与 `UotanStudio.FilesystemFixture` 现在在 `UotanStudio.slnx` 内，随解决方案构建/format/测试门一起运行。`Benchmarks` 的 `Avalonia.Headless` `PackageVersion` 仍必须保留在 `Directory.Packages.props`。
- 增量构建不重跑分析器，会显示"0 个警告"；任何警告清点都要带 `--no-incremental`。
- Debug 还原与构建都要显式 `-c Debug`（原因见根 `AGENTS.md` 的 Debug 构建条）。
- 测试宿主输出必须留在仓库内，否则 `FirmwareContextDependencyScannerTests` 失败并额外产生环境跳过。
- 共享工作树里并行会话重写 `bin/`/`obj/` 会伪造大批失败；判定红绿前先确认加载的字节与源码一致。`PayloadIncrementalBuilderTests` 家族在全量并发负载下出现过 SHA-256 与清单不符的红，单跑该类 14/14 通过。
- 并行会话正在优化 EROFS 托管实现；`Firmware.BuilderStreamingTests.Erofs_StreamingOutput_MatchesByteArrayApi` 当前逐字节不一致，归因于该会话的未合并改动，不是本轮架构改动的回归。
- NativeAOT 发布仍会报告第三方 UI/Dock 库的 `IL2026` 反射绑定警告；本轮 DI 注册全部使用显式工厂，未新增容器反射注册警告，但第三方警告不等于运行时 GPU/窗口路径已验证。
- `UotanStudio.Tests` 当前没有 `UotanStudio.Tests.Comm` 命名空间；`UotanStudio.Comm/README.md` 的 `FullyQualifiedName~UotanStudio.Tests.Comm` 过滤命令零匹配，Comm 协议/传输的回归只有零散的 wire-format、设备映射与集成测试。

## 未验证边界

- `win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 未在本机执行；真实 USB 设备、物理手机与 macOS/Linux 平台路径未验证。
- NativeAOT：2026-10-23 当前源码已成功发布 `win-x64`；`win-arm64`、`linux-*`、`osx-*` 未在本机发布，AOT 下 `tools/list` 数量与真实设备/图形路径未重测。
- EDL/MTK 的界面项（expert 开关下的写入块上限与 NAND 每块页数覆盖、跳过读写确认头、主分区条目数组起始 LBA 与条目数量/大小覆盖、按清单回读、加密页脚读取、pbl/QFPROM 转储与安全启动状态、DA 阶段的区域转储控件、按目录匹配 programmer、设备卡上的 bootloader/Boot ROM 版本与 MEID/SOCID 行、只读 RPMB 容量按钮）只有假件与脚本化覆盖：没在真实 EDL/MTK 设备上执行过任何一次读写，也没有逐项目视过启动后的 GUI。MEID/SOCID 只在内存与设备卡显示，未持久化、未导出、未进日志；RPMB 容量未经真实 XFlash 存储就绪会话验证。
- WSL/QEMU 原生挂载与原生工具互操作不在仓库门禁内；文件系统写入方的 GKI 5.10/6.1 挂载、fsck 与整树读回取证在仓库外工装完成，不随 CI 运行。
- 无 server 的 TCP ADB 与 scrcpy 投屏结论来自同一模拟器实例的 5555 与 16384 回环端口实测；该 ROM 不回应 `GET_CLIPBOARD`，设备→客户端的主动剪贴板推送未在硬件上观察到，运行时切换也未在模拟器或设备上复跑。
- 有意不接入宿主的 Comm 能力以 `UotanStudio.Comm/README.md` 的 "Implemented but deliberately not surfaced in the host" 一节为准：目前记录的是要求 preloader 命令阶段且交换被拒即让会话 fault 的 `MtkSession.GetBootLoaderVersionAsync` 与 `GetBromVersionAsync`（同一对字节已由非致命的 `QueryBootIdentityOwnedAsync` 在初始化时收集）。该节同时声明厂商 exploit 实现类入口已整体移出代码库，不要按旧记录去找它们。
- 投屏与设备之外的 GUI 交互（真实 FolderPicker、拖拽、缩放手势）没有自动化覆盖，Avalonia 编译绑定只在构建期校验绑定名，不等于渲染正确。