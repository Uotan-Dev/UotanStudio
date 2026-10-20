# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## 当前实测（2026-10-21，Windows x64，主树工作区，Debug）

- `dotnet restore UotanStudio.slnx -p:Configuration=Debug`：成功。
- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug --no-incremental`：0 错误、0 警告（含 `UotanStudio.Tests`）。
- 本轮全量 Rebuild（`-t:Rebuild -m:1 -nodeReuse:false`）：0 错误、0 编译器警告；仅在并发测试宿主占用测试 exe 时出现文件复制重试告警，不计为代码告警。
- `dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes` 与同上的 `style`：均通过；本轮改动的 Media、Comm 设备/USB、宿主设备服务与日志视图、对应测试目录另跑 `--include` 两道门，零报告。
- 全量测试（`UotanStudio.Tests.exe -noColor -noLogo`）：6578 总数 / 6572 通过 / 0 失败 / 6 跳过，单轮 1m33s–1m42s（共享工作树多次测量）。
- `dotnet build UotanStudio.Comm/UotanStudio.Comm.csproj` 与 `UotanStudio/UotanStudio.csproj` 的 `--no-incremental -m:1 -nodeReuse:false`：均 0 错误、0 警告；全解 `--no-incremental` 在并发测试宿主占用测试输出时出现 MSB3021/MSB3027 文件锁错误，属环境冲突，不是编译器诊断。
- 性能批次 `docs/perf/optimization-wave-2026-10-21-b.md` 的改动范围：15 个文件跑 `dotnet format … whitespace|style --verify-no-changes --include`，两道门零报告；`UotanStudio.Benchmarks` 的 5 个 ProfileRunner 负载产出镜像 `image sha256` 与该批次改动前逐字节相同（EROFS LZ4/ZSTD/LZMA 打包与解包、ext4 打包）。基准工程不在解决方案内，这道证据只能由 `--profile` 运行器给出。
- `UotanStudio.IconVerification`（不在 `UotanStudio.slnx`）：156 个图标目标（108 个静态 TDesign 控件 + 48 个 `AppIconKind` 动态图标）headless 渲染检查 0 失败；单图与联系表输出到 `artifacts/icon-verification/`。
- `TreatWarningsAsErrors=true` 生效于 6 个项目：`UotanStudio.Comm`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash`、`UotanStudio.Media`、`UotanStudio.RegexInfer`。全解 0 警告同时意味着受管库的 `CS1591` 为 0。
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

- `UotanStudio.Benchmarks` 不在 `UotanStudio.slnx` 内，解决方案构建与测试门都不碰它。它 `PackageReference` 了 `Avalonia.Headless`，删掉 `Directory.Packages.props` 里对应的 `PackageVersion` 行会让它 `dotnet restore` 报 `NU1010`，而解决方案门依然全绿。
- `UotanStudio.IconVerification` 同样故意不加入 `UotanStudio.slnx`，只能手动运行 `dotnet run --project UotanStudio.IconVerification/UotanStudio.IconVerification.csproj -c Debug`；它的自动断言覆盖非空形状、边界裁切、前景色占比、24 px 间距与 16/24/48/96 px 缩放，语义形状仍需查看 `artifacts/icon-verification/sheets/` 下的 PNG 联系表。
- 增量构建不重跑分析器，会显示"0 个警告"；任何警告清点都要带 `--no-incremental`。
- Debug 还原与构建都要显式 `-c Debug`（原因见根 `AGENTS.md` 的 Debug 构建条）。
- 测试宿主输出必须留在仓库内，否则 `FirmwareContextDependencyScannerTests` 失败并额外产生环境跳过。
- 共享工作树里并行会话重写 `bin/`/`obj/` 会伪造大批失败；判定红绿前先确认加载的字节与源码一致。`PayloadIncrementalBuilderTests` 家族在全量并发负载下出现过 SHA-256 与清单不符的红，单跑该类 14/14 通过。
- `UotanStudio.Tests` 当前没有 `UotanStudio.Tests.Comm` 命名空间；`UotanStudio.Comm/README.md` 的 `FullyQualifiedName~UotanStudio.Tests.Comm` 过滤命令零匹配，Comm 协议/传输的回归只有零散的 wire-format、设备映射与集成测试。

## 未验证边界

- `win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 未在本机执行；真实 USB 设备、物理手机与 macOS/Linux 平台路径未验证。
- NativeAOT：最近一次成功发布记录针对 2026-10-10 的产物，当前源码未重跑 `dotnet publish UotanStudio.Desktop -c Release -r win-x64 --self-contained -p:PublishAot=true`，AOT 下的 `tools/list` 数量也未重测。
- EDL/MTK 的界面项（expert 开关下的写入块上限与 NAND 每块页数覆盖、跳过读写确认头、主分区条目数组起始 LBA 与条目数量/大小覆盖、按清单回读、加密页脚读取、pbl/QFPROM 转储与安全启动状态、DA 阶段的区域转储控件、按目录匹配 programmer、设备卡上的 bootloader/Boot ROM 版本与 MEID/SOCID 行、只读 RPMB 容量按钮）只有假件与脚本化覆盖：没在真实 EDL/MTK 设备上执行过任何一次读写，也没有逐项目视过启动后的 GUI。MEID/SOCID 只在内存与设备卡显示，未持久化、未导出、未进日志；RPMB 容量未经真实 XFlash 存储就绪会话验证。
- WSL/QEMU 原生挂载与原生工具互操作不在仓库门禁内；文件系统写入方的 GKI 5.10/6.1 挂载、fsck 与整树读回取证在仓库外工装完成，不随 CI 运行。
- 无 server 的 TCP ADB 与 scrcpy 投屏结论来自同一模拟器实例的 5555 与 16384 回环端口实测；该 ROM 不回应 `GET_CLIPBOARD`，设备→客户端的主动剪贴板推送未在硬件上观察到，运行时切换也未在模拟器或设备上复跑。
- 有意不接入宿主的 Comm 能力以 `UotanStudio.Comm/README.md` 的 "Implemented but deliberately not surfaced in the host" 一节为准：目前记录的是要求 preloader 命令阶段且交换被拒即让会话 fault 的 `MtkSession.GetBootLoaderVersionAsync` 与 `GetBromVersionAsync`（同一对字节已由非致命的 `QueryBootIdentityOwnedAsync` 在初始化时收集）。该节同时声明厂商 exploit 实现类入口已整体移出代码库，不要按旧记录去找它们。
- 投屏与设备之外的 GUI 交互（真实 FolderPicker、拖拽、缩放手势）没有自动化覆盖，Avalonia 编译绑定只在构建期校验绑定名，不等于渲染正确。
