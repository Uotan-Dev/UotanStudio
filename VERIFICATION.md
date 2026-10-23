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
