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
