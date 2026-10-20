# UotanStudio 项目指令

## 项目边界

UotanStudio 是面向 Android 固件、卡刷包、线刷工具和设备通信的 .NET 10/C# 14 桌面应用。修改代码前先确认所属边界，避免把 UI、宿主服务和固件算法混在同一层。

- `UotanStudio.Desktop` 是桌面应用入口，负责平台启动和进程级配置。
- `UotanStudio` 是 Avalonia 宿主，包含界面、ViewModel、项目会话、设置、密钥库适配和应用服务。
- `UotanStudio.Delivery` 是独立交付子项目，命名空间为 `UotanStudio.Services.Delivery`。它负责交付计划校验、Super/Payload/Recovery 构建、DAT/BR 兼容、脚本、ZIP 输出、工具准备和输出校验。
- `UotanStudio.Compress` 是独立的通用压缩库，命名空间为 `UotanStudio.Compress`，只依赖 `UotanStudio.Hash`。它提供通用压缩/解压实现与容器编解码，供固件层复用；**不得反向引用 `UotanStudio.Firmware`**（会形成程序集环，该边界由 `UotanStudio.Tests/Firmware/Architecture/CompressProjectBoundaryTests.cs` 守护）。
- `UotanStudio.Hash` 提供校验和与摘要算法，是不依赖任何其他业务项目的最底层叶子库。全仓库的校验和与摘要计算都必须经由它的公共契约：格式规定的非标准初值、寄存器语义、无最终异或、厂商私有变体与加性校验和由本库实现；语义与 BCL 一致的标准 CRC-32 与 MD5/SHA 摘要族由本库转发（本库是仓库内唯一可以声明 `System.IO.Hashing` 包的项目）。签名与验签、作为签名参数的 `HashAlgorithmName`、HMAC、随机数生成、AES 等厂商加解密与定时比较不在转发范围。本库类型一律 public 并以中英双语 XML 注释描述公共形态，`InternalsVisibleTo` 只给 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks`；边界由 `UotanStudio.Tests/Firmware/Architecture/HashBoundaryGuardTests.cs` 全仓扫描把关，该门只在检出解决方案时有效，找不到 `UotanStudio.slnx` 即失败而非跳过。
- `UotanStudio.Firmware` 负责固件格式、文件系统、Payload、设备树、启动镜像和签名等底层能力；通用压缩实现由 `UotanStudio.Compress` 提供，Firmware 只保留 SquashFS/EROFS/F2FS 块级压缩簇、启动镜像组件封装、Payload 压缩操作、`.cpio.gz`/`.tar.xz` 组合等格式领域封装。
- `UotanStudio.Comm` 负责 ADB、HDC、Fastboot、USB/TCP/UDP 与 EDL/MTK/RKDL 设备通信。整个项目的设备读写（USB、TCP、UDP、串口、adb/HDC server 协议）必须由 Comm 持有；宿主不得自行打开套接字、串口或 USB 句柄，只能消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与 Comm 公开的协议客户端类。
- `UotanStudio.Mirroring`（命名空间 `UotanStudio.Services.Mirroring`）是独立投屏服务层，承载 scrcpy 等投屏协议与会话管理，仅引用 `UotanStudio.Comm`；`UotanStudio.Media` 保持零 Comm 依赖，经解码工厂消费帧数据。scrcpy 不属于 Comm 协议层。
- `UotanStudio.Media` 负责音视频解码、投屏呈现和平台媒体后端。
- `UotanStudio.Projects` 负责项目文件、解决方案快照和项目生命周期模型。
- `UotanStudio.Tests` 使用 xUnit v3 和 Microsoft.Testing.Platform，覆盖 `UotanStudio.Firmware` 库的格式、文件系统和 Payload 测试，并覆盖宿主服务、Comm、Delivery、Mirroring、Media、Projects 和界面流程。它是唯一的可执行测试宿主，`dotnet test --solution` 与该宿主直接运行覆盖全部测试。

交付子项目可以引用 `UotanStudio.Firmware` 和必要的第三方库，但不得反向引用宿主 UI、项目服务或 `KeyLibraryService`。签名依赖通过 `IDeliverySigningKeyProvider` 注入；桌面宿主只负责适配、权限、资源和界面策略。

目录和文件名按职责分组且保持简短。交付代码放在 `Models/`、`Build/`、`Scripts/`、`Tools/` 下；不要把所有类型平铺在项目根目录，也不要为简单转发层创建多级无意义抽象。

## 交付与脚本约定

- 交付后端的模式互斥、DAT/BR 分组、Edify 与多平台脚本生成、zstd 工具准备与 SHA-256、输出发布的暂存与回滚、ZIP 成员与工具目录规则集中在 `UotanStudio.Delivery/AGENTS.md`，改 Delivery 前先读它。跨边界的部分留在这里：`DeliveryPlan` 是交付后端唯一的输入契约，宿主负责采集与恢复界面状态、后端负责校验与生成输出，Super、Payload、Recovery 与纯脚本模式的互斥只能由现有校验路径一处维护，不要在 View 和后端各实现一套规则。

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

测试工程的宿主、命令、写法与环境门控集中在 `UotanStudio.Tests/AGENTS.md`；实测数字、跳过清单与门禁覆盖缺口集中在根 `VERIFICATION.md`。

生产代码不得用项目级 `NoWarn` 隐藏新的生产警告，也不得用它掩盖可修复的空引用、裁剪或异步问题；`UotanStudio.Tests` 为旧整镜像内存型构建 API 保留的 `CS0618` 抑制不在此列。

裁剪兼容性优先修复真实调用链。避免在生产路径使用 `StackFrame.GetMethod()` 等需要保留反射元数据的 API；若确需使用，必须明确标注和验证裁剪边界，不能为了安静构建而全局禁用 IL2026。

## 公开 API 文档

`UotanStudio.Compress`、`UotanStudio.Hash`、`UotanStudio.RegexInfer`、`UotanStudio.Comm`、`UotanStudio.Firmware`、`UotanStudio.Firmware.MagicGen`、`UotanStudio.Delivery`、`UotanStudio.Media`、`UotanStudio.Mirroring`、`UotanStudio.Projects`、`UotanStudio.Mcp`、`UotanStudio.Desktop` 的 csproj 显式设置 `GenerateDocumentationFile`，根 `.editorconfig` 把 `CS1591` 设为 `warning`；`UotanStudio` 和 `UotanStudio.Tests` 各自的 `.editorconfig` 把它设为 `none`。`UotanStudio.Hash`、`UotanStudio.RegexInfer`、`UotanStudio.Compress`、`UotanStudio.Comm`、`UotanStudio.Media` 与 `UotanStudio.Firmware` 另带 `TreatWarningsAsErrors`，缺注释的公共类型会使这些项目构建失败。公共形态只由代码与 XML 注释声明，仓库不再维护 `PublicAPI.*.txt` 基线文件，也不引用 `Microsoft.CodeAnalysis.PublicApiAnalyzers`。

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

## 验证状态与边界

实测数字、本轮跳过清单、CI 门禁现状、门禁覆盖不到的地方与未验证边界都在根 `VERIFICATION.md`，每跑完一道门禁就更新那里的"当前实测"。留在根文件的只有三条会改变操作的约束：警告清点必须带 `--no-incremental`，增量构建不重跑分析器；测试宿主的输出目录必须留在仓库内，否则 `FirmwareContextDependencyScannerTests` 会因找不到 `UotanStudio.slnx` 而失败并额外产生环境跳过；`UotanStudio.Benchmarks` 不在 `UotanStudio.slnx` 内，解决方案门不构建它，它自己还原依赖 `Directory.Packages.props` 保留 `Avalonia.Headless` 的版本行。
