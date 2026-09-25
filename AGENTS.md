# UotanStudio 项目指令

## 项目边界

UotanStudio 是面向 Android 固件、卡刷包、线刷工具和设备通信的 .NET 10/C# 14 桌面应用。修改代码前先确认所属边界，避免把 UI、宿主服务和固件算法混在同一层。

- `UotanStudio.Desktop` 是桌面应用入口，负责平台启动和进程级配置。
- `UotanStudio` 是 Avalonia 宿主，包含界面、ViewModel、项目会话、设置、密钥库适配和应用服务。
- `UotanStudio.Delivery` 是独立交付子项目，命名空间为 `UotanStudio.Services.Delivery`。它负责交付计划校验、Super/Payload/Recovery 构建、DAT/BR 兼容、脚本、ZIP 输出、工具准备和输出校验。
- `UotanStudio.Firmware` 负责固件格式、文件系统、Payload、压缩、设备树、启动镜像和签名等底层能力。
- `UotanStudio.Comm` 负责 ADB、HDC、Fastboot、USB/TCP/UDP 与 EDL/MTK/RKDL 设备通信。整个项目的设备读写（USB、TCP、UDP、串口、adb/HDC server 协议）必须由 Comm 持有；宿主不得自行打开套接字、串口或 USB 句柄，只能消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与 Comm 公开的协议客户端类。
- `UotanStudio.Mirroring`（命名空间 `UotanStudio.Services.Mirroring`）是独立投屏服务层，承载 scrcpy 等投屏协议与会话管理，仅引用 `UotanStudio.Comm`；`UotanStudio.Media` 保持零 Comm 依赖，经解码工厂消费帧数据。scrcpy 不属于 Comm 协议层。
- `UotanStudio.Media` 负责音视频解码、投屏呈现和平台媒体后端。
- `UotanStudio.Projects` 负责项目文件、解决方案快照和项目生命周期模型。
- `UotanStudio.Tests` 使用 xUnit v3 和 Microsoft.Testing.Platform，覆盖各库、宿主服务和交付流程。

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
- 生成 ZST 时，工具服务按目标平台准备固定版本的 zstd 并校验 SHA-256。Windows、Linux、macOS 的工具名称和架构选择遵循现有实现；不再下载或生成 riscv64 二进制。
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

- `UotanStudio.Desktop` 保持应用入口；不要在共享 `UotanStudio` 项目重新引入 `App.axaml.cs` 式桌面入口。
- 交付页面保留现有控件和布局语义。需要新增行为时先复用 `DeliveryViewModel`、`DeliveryCompositionView` 和 `DeliveryView.Operations.cs`，不要在 View 层维护第二套镜像归属、确认状态或输出集合。
- 新建项目、打开、关闭和另存为必须经过项目操作门。保存失败时保留源上下文和原目标内容；另存为成功后再切换项目根目录。
- 解决方案快照中的交付计划和面板可见性通过 `SolutionSnapshot.State` 保存。解决方案根目录不再创建旧式 `.uotan/delivery.json`；子项目自己的 `.uotan/` 目录语义保持不变。
- 编辑器明确选择“按文本打开”时必须保持文本模式，即使文件包含 NUL 或使用二进制扩展名。自动探测不能覆盖用户的显式模式。
- UI 可见文本经资源字典本地化，不能在界面中拼接中英文。已生成脚本文本只在生成时确定语言，切换界面语言不得偷偷改写已有脚本。
- 文件管理器、交付列表和脚本生成器对 `.dat`、`.new.dat`、`.patch.dat`、`.br` 和 `.transfer.list` 的识别应复用现有格式注册和校验，不要复制后缀判断。

## C#、格式和可维护性

项目目标为 .NET 10/C# 14。遵循根目录 `.editorconfig`：

- 4 个空格缩进，禁止 Tab；使用 file-scoped namespace。
- 使用 `var`、模式匹配、集合表达式、对象初始化和空合并等现有规则要求的现代 C# 写法。
- 公共和内部 API 保持明确的可空标注；DTO 的必需值使用 `required` 或合理默认值。
- 控制流必须使用花括号；避免无意义的表达式链、过深的模式匹配和只能增加阅读负担的抽象。
- `insert_final_newline = false` 是当前仓库约束，格式化后不要手动重新添加文件末尾换行。
- 修改 XAML 时使用当前 Avalonia API，避免引入已标记 obsolete 的属性。
- 注释解释原因、边界和外部格式约束；不要堆积与实现重复的注释。

推荐验证命令：

```text
dotnet build UotanStudio.slnx --no-restore --nologo
dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes
dotnet format UotanStudio.slnx style --no-restore --verify-no-changes
```

测试项目是可执行的 xUnit v3 测试宿主。异步测试使用 `async Task`、`await` 和 `TestContext.Current.CancellationToken`；测试方法中禁止 `.Result`、`.Wait()` 和 `.GetAwaiter().GetResult()`。同步等待只能留在明确的同步基础设施边界，并且不得因此让 xUnit analyzer 报警。

测试命令使用 `global.json` 选择的 Microsoft.Testing.Platform。项目级和解决方案级入口分别使用 `--project` 与 `--solution`；定向测试优先使用 xUnit v3 原生过滤参数，并用 `--minimum-expected-tests 1` 防止过滤条件失效时把“零测试”误报为成功：

```text
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Shell.ChecksumServiceTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-method UotanStudio.Tests.Shell.ChecksumServiceTests.ComputeAsync_KnownVectors_ProducesStandardDigests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~ChecksumServiceTests" --minimum-expected-tests 1
dotnet test --solution UotanStudio.slnx --no-restore
```

`--filter-class`、`--filter-method`、`--filter-namespace` 和 `--filter-trait` 属于 xUnit v3 原生简单过滤器；同一命令不得把它们与 `--filter` 或 `--filter-query` 混用。传统 `--filter` 仅为现有 VSTest 过滤表达式和 CI 脚本保留。过滤后返回零测试时先检查选择器，不得归类为测试代码失败。

测试名称使用 `Method_Scenario_ExpectedBehavior`，遵循 Arrange/Act/Assert，优先验证真实格式互操作性、失败回滚、取消和边界，而不是只验证内部对象往返。跨平台、WSL、QEMU、真实设备和真实语料测试缺少环境时应明确跳过并写出原因。

测试项目中保留的 `CS0618` 抑制只用于覆盖旧的整镜像内存型构建 API；生产代码必须使用文件路径或流式重载。不要用项目级 `NoWarn` 隐藏新的生产警告，也不要用它掩盖可修复的空引用、裁剪或异步问题。

裁剪兼容性优先修复真实调用链。避免在生产路径使用 `StackFrame.GetMethod()` 等需要保留反射元数据的 API；若确需使用，必须明确标注和验证裁剪边界，不能为了安静构建而全局禁用 IL2026。

## Git 和工作区纪律

- 当前工作区可能包含多个并行功能改动。只编辑完成当前任务所需的文件，保留无关修改。
- 禁止使用 `git reset --hard`、全量恢复或删除未知来源文件来“清理”工作区。
- 不要提交、推送、合并或重写历史，除非用户明确要求。需要提交时使用英文 Conventional Commit、`git commit -s`，并保持现有签名约定。
- 修改完成后报告实际变更、构建命令、定向测试、跳过的测试和仍未验证的边界。不要把编译通过写成真实设备或全平台运行通过。

## 当前验证基线

最近一次工作区验证结果：

- `dotnet build UotanStudio.slnx --no-restore --nologo`：0 错误、0 警告。
- whitespace/style `dotnet format --verify-no-changes`：通过。
- JAR/DEX、LP、流式构建、异常日志、Delivery 和镜像输入的定向测试均通过。
- 完整测试集可能包含需要 WSL、真实设备、外部工具或本地语料的条件测试；运行时必须按测试输出区分通过、跳过、失败和环境不可用。
