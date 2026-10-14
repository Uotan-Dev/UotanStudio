# UotanStudio 项目指令

## 项目边界

UotanStudio 是面向 Android 固件、卡刷包、线刷工具和设备通信的 .NET 10/C# 14 桌面应用。修改代码前先确认所属边界，避免把 UI、宿主服务和固件算法混在同一层。

- `UotanStudio.Desktop` 是桌面应用入口，负责平台启动和进程级配置。
- `UotanStudio` 是 Avalonia 宿主，包含界面、ViewModel、项目会话、设置、密钥库适配和应用服务。
- `UotanStudio.Delivery` 是独立交付子项目，命名空间为 `UotanStudio.Services.Delivery`。它负责交付计划校验、Super/Payload/Recovery 构建、DAT/BR 兼容、脚本、ZIP 输出、工具准备和输出校验。
- `UotanStudio.Compress` 是独立的通用压缩库，命名空间为 `UotanStudio.Compress`，只依赖 `UotanStudio.Hash`。它提供通用压缩/解压实现与容器编解码，供固件层复用；**不得反向引用 `UotanStudio.Firmware`**（会形成程序集环，该边界由 `UotanStudio.Tests/Firmware/Architecture/CompressProjectBoundaryTests.cs` 守护）。
- `UotanStudio.Hash` 提供校验和与摘要算法，是不依赖任何其他业务项目的最底层叶子库。全仓库的校验和与摘要计算都必须经由它的公共契约：格式规定的非标准初值、寄存器语义、无最终异或、厂商私有变体与加性校验和由本库实现；语义与 BCL 一致的标准 CRC-32 与 MD5/SHA 摘要族由本库转发（本库是仓库内唯一可以声明 `System.IO.Hashing` 包的项目）。签名与验签、作为签名参数的 `HashAlgorithmName`、HMAC、随机数生成、AES 等厂商加解密与定时比较不在转发范围。本库类型一律 public 并登记 `PublicAPI`，`InternalsVisibleTo` 只给 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks`；边界由 `UotanStudio.Tests/Firmware/Architecture/HashBoundaryGuardTests.cs` 全仓扫描把关，该门只在检出解决方案时有效，找不到 `UotanStudio.slnx` 即失败而非跳过。
- `UotanStudio.Firmware` 负责固件格式、文件系统、Payload、设备树、启动镜像和签名等底层能力；通用压缩实现由 `UotanStudio.Compress` 提供，Firmware 只保留 SquashFS/EROFS/F2FS 块级压缩簇、启动镜像组件封装、Payload 压缩操作、`.cpio.gz`/`.tar.xz` 组合等格式领域封装。
- `UotanStudio.Comm` 负责 ADB、HDC、Fastboot、USB/TCP/UDP 与 EDL/MTK/RKDL 设备通信。整个项目的设备读写（USB、TCP、UDP、串口、adb/HDC server 协议）必须由 Comm 持有；宿主不得自行打开套接字、串口或 USB 句柄，只能消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与 Comm 公开的协议客户端类。
- `UotanStudio.Mirroring`（命名空间 `UotanStudio.Services.Mirroring`）是独立投屏服务层，承载 scrcpy 等投屏协议与会话管理，仅引用 `UotanStudio.Comm`；`UotanStudio.Media` 保持零 Comm 依赖，经解码工厂消费帧数据。scrcpy 不属于 Comm 协议层。
- `UotanStudio.Media` 负责音视频解码、投屏呈现和平台媒体后端。
- `UotanStudio.Projects` 负责项目文件、解决方案快照和项目生命周期模型。
- `UotanStudio.Tests` 使用 xUnit v3 和 Microsoft.Testing.Platform，覆盖 `UotanStudio.Firmware` 库的格式、文件系统和 Payload 测试，并覆盖宿主服务、Comm、Delivery、Mirroring、Media、Projects 和界面流程。它是唯一的可执行测试宿主，`dotnet test --solution` 与该宿主直接运行覆盖全部测试。

交付子项目可以引用 `UotanStudio.Firmware` 和必要的第三方库，但不得反向引用宿主 UI、项目服务或 `KeyLibraryService`。签名依赖通过 `IDeliverySigningKeyProvider` 注入；桌面宿主只负责适配、权限、资源和界面策略。

目录和文件名按职责分组且保持简短。交付代码放在 `Models/`、`Build/`、`Scripts/`、`Tools/` 下；不要把所有类型平铺在项目根目录，也不要为简单转发层创建多级无意义抽象。

## 交付与脚本约定

- `DeliveryPlan` 是交付后端的输入契约。宿主负责采集和恢复界面状态，后端负责校验和生成输出。
- Super、Payload、Recovery 和纯脚本模式的互斥关系必须由现有校验路径维护，不要在 View 和后端各实现一套规则。
- DAT/BR 文件必须按分区与同组的 `<partition>.transfer.list`、`<partition>.new.dat`、`<partition>.new.dat.br` 或补丁文件关联。不能仅凭任意 `.dat` 或 `.br` 文件名推断分区。
- 卡刷 `updater-script` 使用原包提供且经过校验的 Edify 能力；典型调用包括 `block_image_verify`、`block_image_update`、`package_extract_file` 和 `file_getprop`。不得把 shell 命令冒充成卡刷脚本。
- Brotli 文件的卡刷更新调用应传递 `.new.dat.br` 和 `.patch.dat`，例如：

  ```edify
  block_image_update(
      "/dev/block/bootdevice/by-name/system",
      package_extract_file("system.transfer.list"),
      "system.new.dat.br",
      "system.patch.dat"
  ) || abort("E2001: Failed to update system image.");
  ```

- Bat、Linux/macOS shell 和卡刷脚本必须使用同一份文件编排结果。脚本中的机型代号、作者信息和生成元数据来自交付计划，不能在各脚本模板中重复硬编码。
- 生成 ZST 时，工具服务按目标平台准备固定版本的 zstd 并校验 SHA-256。Windows、Linux、macOS 的工具名称和架构选择遵循现有实现；riscv64 二进制不在支持与下载范围内。
- `TXT` 是清单格式，不得把压缩镜像直接作为可刷写镜像列出。压缩镜像必须由相应脚本先解压到临时路径，再交给 fastboot。
- 输出发布使用临时目录、校验和原子替换。失败时保留既有输出，不能先删除用户已有文件。
- ZIP 内部路径使用 `/`，镜像通常位于 `images/`，平台工具位于对应平台目录，具体成员以 `DeliveryOutputService` 和相关测试为准。

## 固件和不可信输入

固件、ZIP、Payload、文件系统镜像和设备返回数据都视为不可信输入。

- 优先使用流式处理和有界缓冲，避免把整个镜像、压缩结果或单个归档条目一次性读入内存。
- 所有偏移、长度、条目数量、解压大小和块数量都必须先检查上限，并防止整数溢出。
- 支持取消的 API 必须贯穿传递 `CancellationToken`；发布到文件时使用临时文件并在完整校验后原子替换。
- 保留输入字节身份、哈希和结构信息。修改后的镜像应分别报告字节、结构、内容和容量等价性，不能只依赖项目内部的往返测试。
- 文件系统重建应尽量继承经校验的原镜像参数；压缩率、块数、最终大小和物理布局允许重新计算，不得承诺修改后一定适合原分区容量。
- 外部工具、WSL、QEMU、真实设备和 GPU 的验证边界必须单独说明。未执行的硬件、原生挂载、商店或性能验证不能写成已通过。

## 通信和设备规则

- ADB/HDC 的 server 路由由 `DebugBridgeRoute` 决定。目标存在、离线或未授权时仍属于 server 管理的目标，协议错误不能自动回退 USB；只有连接被拒绝才视为 server 不存在。
- server 的 serial/connect key 是权威身份，不能按枚举顺序、VID/PID 或设备列表位置映射到 USB 设备。
- 原生 USB 会话必须精确筛选物理路径，接口申请失败立即释放本会话已申请的资源。取消传输时要等待原生操作真正结束后再复用缓冲区。
- scrcpy 投屏复用已有 ADB server 路径；GPU 呈现只有在真实取得 GPU 上下文并准备好 shader 后才启用，失败时保留 CPU 位图回退。
- 涉及真实设备的测试优先使用只读命令。除非用户明确要求并且测试已隔离，不执行重启、刷写、提权、安装、卸载、清日志或清数据。

## UI、项目和持久化

桌面入口位于 `UotanStudio.Desktop`；共享 `UotanStudio` 项目不包含桌面入口类型。
- 交付页面保留现有控件和布局语义。需要新增行为时先复用 `DeliveryViewModel`、`DeliveryCompositionView` 和 `DeliveryView.Operations.cs`，不要在 View 层维护第二套镜像归属、确认状态或输出集合。
- 新建项目、打开、关闭和另存为必须经过项目操作门。保存失败时保留源上下文和原目标内容；另存为成功后再切换项目根目录。
- 解决方案快照中的交付计划和面板可见性通过 `SolutionSnapshot.State` 保存。解决方案根目录不存放 `delivery.json` 之类的交付配置文件；子项目自己的 `.uotan/` 目录语义保持不变。
- 编辑器明确选择“按文本打开”时必须保持文本模式，即使文件包含 NUL 或使用二进制扩展名。自动探测不能覆盖用户的显式模式。
- UI 可见文本经资源字典本地化，不能在界面中拼接中英文。已生成脚本文本只在生成时确定语言，切换界面语言不得偷偷改写已有脚本。
- 文件管理器、交付列表和脚本生成器对 `.dat`、`.new.dat`、`.patch.dat`、`.br` 和 `.transfer.list` 的识别应复用现有格式注册和校验，不要复制后缀判断。

## C#、格式和可维护性

项目目标为 .NET 10/C# 14：`Directory.Build.props` 里的 `TargetFramework` 为 `net10.0`、`LangVersion` 显式写 `14.0`，`global.json` 固定 SDK `10.0.401` 并 `rollForward: latestMinor`，因此本机与 CI 不再各自挑 SDK。指针声明与 `&`、`fixed`、`sizeof`、`stackalloc`→指针都必须在 `unsafe` 上下文中书写，`<Features>` 未启用任何内存安全放宽，生产代码的 `unsafe` 要求保持不变。遵循根目录 `.editorconfig`：

- 4 个空格缩进，禁止 Tab；使用 file-scoped namespace。
- 使用 `var`、模式匹配、集合表达式、对象初始化和空合并等现有规则要求的现代 C# 写法。
- 公共和内部 API 保持明确的可空标注；DTO 的必需值使用 `required` 或合理默认值。
- 控制流必须使用花括号；避免无意义的表达式链、过深的模式匹配和只能增加阅读负担的抽象。
- `insert_final_newline = false` 是当前仓库约束，格式化后不要手动重新添加文件末尾换行。
- 修改 XAML 时使用当前 Avalonia API，避免引入已标记 obsolete 的属性。
- 注释解释原因、边界和外部格式约束；不要堆积与实现重复的注释。

推荐验证命令：

```text
dotnet restore UotanStudio.slnx -p:Configuration=Debug
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes
dotnet format UotanStudio.slnx style --no-restore --verify-no-changes
```

Debug 构建必须显式写 `-c Debug`，并且还原也要带 `-p:Configuration=Debug`：`UotanStudio.csproj` 中 `Dock.Avalonia.Diagnostics` 的 `IncludeAssets` 条件按 `$(Configuration)` 求值，还原阶段该属性为空会剔除编译资产，使 `#if DEBUG` 里的 `AttachDockDebugOverlay` 调用以 `CS1061` 失败。CI 使用的 Release 配置不受影响，因为该调用被 `#if DEBUG` 排除。

测试项目是可执行的 xUnit v3 测试宿主。异步测试使用 `async Task`、`await` 和 `TestContext.Current.CancellationToken`；测试方法中禁止 `.Result`、`.Wait()` 和 `.GetAwaiter().GetResult()`。同步等待只能留在明确的同步基础设施边界，并且不得因此让 xUnit analyzer 报警。

测试命令使用 `global.json` 选择的 Microsoft.Testing.Platform。项目级和解决方案级入口分别使用 `--project` 与 `--solution`；全部测试位于 `UotanStudio.Tests`，固件测试按 `Firmware/` 目录分组；定向测试优先使用 xUnit v3 原生过滤参数，并用 `--minimum-expected-tests 1` 防止过滤条件失效时把“零测试”误报为成功：

```text
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Shell.ChecksumServiceTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-method UotanStudio.Tests.Shell.ChecksumServiceTests.ComputeAsync_KnownVectors_ProducesStandardDigests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.ErofsTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~ChecksumServiceTests" --minimum-expected-tests 1
dotnet test --solution UotanStudio.slnx --no-restore
```

`--filter-class`、`--filter-method`、`--filter-namespace` 和 `--filter-trait` 属于 xUnit v3 原生简单过滤器；同一命令不得把它们与 `--filter` 或 `--filter-query` 混用。传统 `--filter` 仅为现有 VSTest 过滤表达式和 CI 脚本保留。过滤后返回零测试时先检查选择器，不得归类为测试代码失败。

单轮全量测试本地实测约 2 分半；CI runner 的 Release 单轮跑满 4 分 55 秒仍未结束，因此 `.github/workflows/ci.yml` 的 Test 步骤带 `timeout-minutes: 15` 上限：超过上限按挂死处理，不设法定位阻塞的集合就不要继续提高该数值，本地跑整套也用同一个上限。

测试名称使用 `Method_Scenario_ExpectedBehavior`，遵循 Arrange/Act/Assert，优先验证真实格式互操作性、失败回滚、取消和边界，而不是只验证内部对象往返。跨平台、WSL、QEMU、真实设备和真实语料测试缺少环境时应明确跳过并写出原因。真实输入与外部预言测试按变量逐个开启：`UOTAN_MAGISKBOOT_ORACLE` 指向 magiskboot 可执行文件，`UOTAN_SELINUX_POLICY_SAMPLE`、`UOTAN_ELF_FIREHOSE_SAMPLE`、`UOTAN_HOS_UPDATE_APP_SAMPLE` 指向样例文件，`UOTAN_SAMSUNG_DAT_MERGE_ORACLE`、`UOTAN_SAMSUNG_PAYLOAD_MERGE_ORACLE` 与 `UOTAN_EROFS_CORPUS` 指向预言或语料目录；变量未设置时相关用例跳过，跳过原因里必须写出对应变量名，CI 不设置任何一个。原生工具互操作（mkfs/e2fsck/fsck.erofs/fsck.f2fs 与环回挂载）与 TB 级大数据构建不再由测试工程驱动，其取证在仓库外工装完成。

测试项目（`UotanStudio.Tests`）不使用行间 `//` 或 `/* */` 注释。跳过原因必须来自代码里的 `Skip = "…"` 文案，测试夹具的意图写在测试名称与断言中，不写在注释里。

测试项目中保留的 `CS0618` 抑制只用于覆盖旧的整镜像内存型构建 API；生产代码必须使用文件路径或流式重载。不要用项目级 `NoWarn` 隐藏新的生产警告，也不要用它掩盖可修复的空引用、裁剪或异步问题。

裁剪兼容性优先修复真实调用链。避免在生产路径使用 `StackFrame.GetMethod()` 等需要保留反射元数据的 API；若确需使用，必须明确标注和验证裁剪边界，不能为了安静构建而全局禁用 IL2026。

## 公开 API 文档

`UotanStudio.Compress`、`UotanStudio.Comm`、`UotanStudio.Firmware`、`UotanStudio.Firmware.MagicGen`、`UotanStudio.Delivery`、`UotanStudio.Media`、`UotanStudio.Mirroring`、`UotanStudio.Projects`、`UotanStudio.Mcp`、`UotanStudio.Desktop` 的 csproj 显式设置 `GenerateDocumentationFile`，根 `.editorconfig` 把 `CS1591` 设为 `warning`；`UotanStudio` 和 `UotanStudio.Tests` 各自的 `.editorconfig` 把它设为 `none`。

- 上述项目的公共类型与成员都要有中英双语 XML 注释：`<summary>` 内英文整句在前，中文整句写在同一 `<summary>` 的 `<para>` 中。
- 参数写在 `<param>`，方法返回值写在 `<returns>`，属性和索引器的取值写在 `<value>`（属性不用 `<returns>`）。`<param>` 名称必须与签名逐一对应。
- `<exception>` 只写已由代码路径或测试证实会抛出的异常；`<example>` 只用于非平凡的入口点，代码块放在 `<example>` 内并标注 `language`。
- `<see cref>` 必须能单独解析；同名成员存在多个重载或类型与属性重名时，写明具体重载或在中文段落用文字描述，避免 `CS0419`。
- 这些项目不使用行间 `//` 注释，说明性内容一律进入 XML 注释。vendored 源文件保留头部来源与许可证说明以及配套 `.LICENSE` 文件，其余注释不保留。
- 代码注释和仓库文档只描述当前状态，不描述历史、草稿或演进。

## Git 和工作区纪律

- 当前工作区可能包含多个并行功能改动。只编辑完成当前任务所需的文件，保留无关修改。
- 禁止使用 `git reset --hard`、全量恢复或删除未知来源文件来“清理”工作区。
- 不要提交、推送、合并或重写历史，除非用户明确要求。需要提交时使用英文 Conventional Commit、`git commit -s`，并保持现有签名约定。
- 修改完成后报告实际变更、构建命令、定向测试、跳过的测试和仍未验证的边界。不要把编译通过写成真实设备或全平台运行通过。

## 当前验证基线

实测工作区验证状态（2026-10-10，.NET 10/C# 14）：

- `dotnet build UotanStudio.slnx --no-restore --no-incremental -c Debug`：0 错误、0 警告（含 `UotanStudio.Tests`，异步测试一律用 `TestContext.Current.CancellationToken`，无 `xUnit1051`）。
- `dotnet build UotanStudio.Firmware/UotanStudio.Firmware.csproj --no-restore --nologo --no-incremental -v:n`：0 错误、0 警告。警告清点必须带 `--no-incremental`：增量构建不重跑分析器，会显示"0 个警告"。
- `TreatWarningsAsErrors=true` 在 `UotanStudio.Comm`、`UotanStudio.Media` 与 `UotanStudio.Firmware` 生效；`UotanStudio.Tests` 另有 `NoWarn CS0618`。在 Firmware 中新增无 XML 注释的公共类型会使构建以 `CS1591`/`RS0016` 失败。
- whitespace/style `dotnet format --verify-no-changes`：通过。
- 完整测试集：10912 总数 / 10873 通过 / 15 跳过 / 24 失败，单轮约 2m37s（2026-10-12 在主树 `dotnet test --solution UotanStudio.slnx --no-restore` 实测）。24 条失败全部属于并行会话正在进行的 EROFS inode 模型拆分：`UotanStudio.Firmware/FileSystem/Erofs/Models/Inodes/ErofsInode.cs` 在解码时抛 `FormatException: EROFS inode uses undefined i_format bits 0x0010`，集中在 `ErofsInodeModelEquivalenceTests`、`ErofsBuildGoldenHashTests`、`ErofsSharedXattrReadTests`、`ErofsRebuildFeatureReplayTests`、`ErofsTests.Builder_WritesStandardFlatIFormat` 和一条 `PackingOptionPropagationTests`。EDL、MTK、GPT 与界面本轮改动在全量运行中 0 失败；同一轮 `--no-incremental` 全解重建为 0 警告 0 错误，whitespace 与 style 两道 `dotnet format --verify-no-changes` 均通过。工作区含并行会话的未提交改动，因此该数字是工作区状态而不是某个提交的属性。把测试宿主输出重定向到仓库外目录会让 `FirmwareContextDependencyScannerTests.TryFindRepositoryRoot_CurrentTestHostOutput_FindsCheckedOutSolution` 因找不到 `UotanStudio.slnx` 而失败，并额外产生环境跳过，因此该用例只在仓库内运行时结果有效。
- 13 条跳过全部来自环境边界：4 条需要 Linux 伪终端（`/usr/bin/python3`）、1 条需要可读的 `/sys/bus/usb/devices` 树、1 条需要 POSIX 符号链接、1 条需要 Linux libc 定位读、1 条是 Linux GPU 后端边界、1 条需要 macOS IOKit、2 条因本机没有对应 debug bridge server（ADB/HDC），以及 `ProjectArchiveService.ExportPackageAsync` 对 solution 路径只打包子项目目录造成的 1 条。MCP 工具目录本轮实测为 51 个只读/普通工具加 6 个 `--allow-flash` 门控工具（共 57 个描述符，取自 `UotanStudio.Mcp/Tools/PluginToolCatalog.cs` 的目录数组），其中 EDL/MTK 只读引导协议工具 2 个：`mtk_identify`、`mtk_peek`；`UotanStudio.Comm/Assets/Mtk/Payloads` 已带 49 个 GPLv3 审计副本与同目录 `Payloads.LICENSE`，逐字节溯源用例在仓库内运行时执行。构建工具目录的 7 个测试类（`McpToolCatalogTests`、`McpBootProtocolToolTests`、`McpBootProtocolReadOnlyTests`、`McpBootProtocolIntrospectionTests`、`McpHttpExposureTests`、`McpSandboxTests`、`PluginToolDescriptorTests`）固定在同一个 xUnit collection `"MCP tool generation"` 内串行执行：并发首次生成工具元数据会让 `IProgress<ProgressNotificationValue>` 这类环境参数间歇泄漏进 `inputSchema`，串行后该门在整命名空间与整解决方案批量运行中不再复现。
- JAR/DEX、LP、流式构建、异常日志、Delivery 和镜像输入的定向测试均通过；`UotanStudio.Tests.Comm.Protocols.Adb` 命名空间 48 条全部通过（sync RECV 由测试假件模拟后才可完成）。
- EDL/MTK 审计与暴露判定补齐（B1–B5、A3、A4）后的实测：`UotanStudio.Tests.Comm.Protocols.Edl` 1515 条全通过，`Comm.Protocols.Mtk` 与 `Devices`、`Views`、`Flash`、`Services.MtkRpmbServiceTests` 合计 2995 条中 2994 通过、1 条因本机没有 HDC server 跳过。`dotnet build UotanStudio.slnx --no-restore --nologo -c Debug --no-incremental` 为 0 错误 0 警告；whitespace 与 style 两道 `dotnet format --verify-no-changes` 的剩余报告全部落在并行会话的 `NativeBinCatalog.cs`、`NativeBinSettings.cs`、`DangerousConfirmDialog.axaml.cs`、`DialogService.cs`、`EdlSession.CommandMode.cs` 与三个 EDL 测试文件上，本轮改动触及的 9 个文件在两道门中均为零报告（`UotanStudio.Tests/Services/NativeBin/NativeBinProvisionerTests.cs` 处于并行会话未完成状态，其编译错误为 `NativeBinProvisioner : IDisposable` 与 `InMemorySettings(UseMirrorList:)` 契约尚未成形，与本轮的 libusb 注册入口无关）。
- 补齐 MEID/SOCID 显示、RPMB 容量读取与 libusb 注册入口后的全量测试：11258 总数 / 11233 通过 / 22 跳过 / 2 失败（2m05s）。2 条失败为 `LibUsbInterfaceClaimTests.BusyClaim_UnsupportedDetach_PreservesClaimErrorAndStops` 与 `PackingOptionPropagationTests.Pack_PreservedErofsWithDotOmittedDisabledKey_ClearsTheInodeAdviceDimension`（上文已列出的 EROFS 打包项，`Expected: Enabled / Actual: Disabled`），均属并行会话的 libusb 运行时改签与 EROFS inode 拆分；EDL、MTK、GPT 与界面命名空间 0 失败。上一轮同一台机器上还出现过一条 `ProjectOperationsBoundaryTests.CreateAndOpen_RestoresDataInOrder_ExcludesMissingImages` 失败，重跑即通过，属并发负载下的偶发，未复现。
- 未验证边界：win-arm64、linux-x64、linux-arm64、osx-x64、osx-arm64 四个 RID 未在本机执行；真实 USB 设备、物理手机、macOS/Linux 平台路径未验证；EDL/MTK 界面项（expert 开关下的写入块上限与 NAND 每块页数覆盖、跳过读写确认头、主分区条目数组起始 LBA 与分区条目数量/大小覆盖框、按清单回读按钮、加密页脚读取、pbl/QFPROM 转储与安全启动状态读取、expert 且阶段为 DA 时才出现的区域转储控件；按目录匹配 programmer 的按钮与结论行；设备卡上的 MTK bootloader/Boot ROM 版本与 MEID/SOCID 行——目标不上报对应字节时该行隐藏；只读 RPMB 容量按钮）只有 headless 视图与脚本化假件覆盖，未在真实 EDL/MTK 设备上执行过任何一次读写命令，也没有逐项目视过启动后的 GUI 界面；A3 的结论行文案与 `IsChecked`/选择框联动未在真实 FolderPicker 上点过；MEID/SOCID 只在内存与设备卡显示，未持久化、未导出、未进日志；RPMB 容量按钮未经真实 XFlash 存储就绪会话验证，其 `ReportedRpmbCapacityBytes` 对 eMMC 取自存储应答第三个容量字段，未在硬件上核对；WSL/QEMU 原生挂载与原生工具互操作不在仓库门禁内（测试工程已不驱动该类门控），文件系统写入方的 GKI 5.10/6.1 挂载、fsck 与整树读回取证在仓库外的 `D:\GkiErofsRun`、`E:\GkiFsLab` 工装完成，不随 CI 运行。无 server 的 TCP ADB 与 scrcpy 投屏结论来自模拟器回环端口（同一实例的 5555 与 16384）实测：直接握手、shell、`localabstract` 多流、h264 首帧与关键帧、SET_CLIPBOARD 回包均通过；该 ROM 不回应 GET_CLIPBOARD，设备→客户端剪贴板主动推送仍未在硬件上观察到，且本次运行时切换未在模拟器或设备上复跑。
- 有意不接入宿主的能力（理由与前置条件见 `UotanStudio.Comm/README.md` 的 "Implemented but deliberately not surfaced in the host" 一节）：鉴权 RPMB 写入 `MtkSession.WriteXFlashRpmbFramesAsync` / `WriteXmlRpmbFramesAsync`、改引导与供电语义的 `SetXFlashResetKeyAsync` / `SetXFlashChecksumLevelAsync` / `SetXFlashBatteryOptionAsync`、回包不可解读的 `QueryXFlashSecurityControlCodeAsync`，以及要求 preloader 阶段且失败即 fault 的 `GetBootLoaderVersionAsync` / `GetBromVersionAsync`（同一对字节已由非致命的 `QueryBootIdentityOwnedAsync` 在初始化时收集）。
- NativeAOT：`dotnet publish UotanStudio.Desktop -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，0 错误，仅剩既有的第三方 `IL2026`/`IL2104`/`IL3053` trampoline 警告。本轮发布产物实测通过无头 MCP stdio 冒烟：`initialize`、`tools/list` 与 zstd、gzip、deflate、zlib、brotli、lz4、xz 七种算法（实现位于 `UotanStudio.Compress`）的 `compress_file`/`decompress_file` 往返：对同一 20000 字节样本，七个还原文件的 SHA-256 与源文件逐一相同。该冒烟用的产物早于目录新增的 3 个只读引导工具，因此当前源码下的 AOT `tools/list` 数量尚未重测，6 个刷写类工具仍只在 `--allow-flash` 下注册。brotli 压缩长度在 AOT 与 JIT 产物之间相差约 2.7 KB（两者都能逐字节还原），属代码生成路径差异而非缺陷。
- CI 门禁见 `.github/workflows/ci.yml`（`setup-dotnet` 取 `10.0.x` 正式版 + 构建 + whitespace/style 双 `dotnet format` + `dotnet test --solution`，Test 步骤 `timeout-minutes: 15`，不设置 `UOTAN_*`，因此门控套件自跳）；`.github/workflows/release.yml` 只负责 NativeAOT 发布，同样取 `10.0.x` 正式版。`global.json` 显式 pin `10.0.401` 并 `rollForward: latestMinor`，因此本机与 CI 不再各自挑 SDK。只用 nuget.org 单源即可还原 net10 依赖并成功 `PublishAot` 发布 win-x64。zstd 由 `ZstdSharp.Port` 提供，net10 参考包不含内置 Zstandard 类型。
- 公开 API 注释：10 个受管库项目的公共方法已具备双语 `<summary>`、逐参数 `<param>` 与 `<returns>`（编译器 `CS1591` 为 0，度量脚本按公共可见性过滤后无残留方法缺口）；公共属性/索引器的 `<value>` 仍有约 257 处缺口（实测），属尚未闭合的后续项，不影响构建门。
