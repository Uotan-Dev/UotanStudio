# UotanStudio 项目指令

## 构建与运行

> 本项目面向 .NET 10.0，使用 Central Package Management，所有包版本集中在 `Directory.Packages.props`，请勿在各 `.csproj` 中重复指定版本号。

```bash
# 还原 + 编译整个解决方案（无需 IDE）
dotnet restore
dotnet build UotanStudio.slnx

# 运行测试（xunit.v3 + .NET 10 MTP 测试通道；须显式 -c Debug，不兼容 VSTest 传统参数）
dotnet test UotanStudio.slnx -c Debug
# 若 dotnet test 报「运行了零个测试」（MTP 发现偶发），可直接运行测试宿主：
./UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe

# 启动应用（Desktop 为启动项目）
dotnet run --project UotanStudio.Desktop

# AOT 发布（IsAotCompatible / PublishAot / TrimMode=partial 均已启用，`dotnet publish -r win-x64` 发布冒烟通过；仅有 Dock.Avalonia 的 IL2026 裁剪警告，属第三方 XAML trampoline，TrimMode=partial 下可接受）
dotnet publish UotanStudio.Desktop -c Release -r win-x64
```

### 固件 CLI（`firmware` 子命令）

`UotanStudio.Desktop/Program.cs` 的 `Main` 在启动 Avalonia **之前**分发 `firmware` 子命令（单二进制即提供无 UI 的固件解包/打包 CLI；`Main` 返回 `int` 退出码）。CLI 逻辑位于 `UotanStudio/Services/Cli/`：

| 文件 | 职责 |
|---|---|
| `Services/Cli/FirmwareCliCommand.cs` | 命令模型：`FirmwareCliVerb`（List/Info/Unpack/Pack/Flash/DisableVerification）、`FirmwareCliCommand`、`FirmwareCliParseResult` |
| `Services/Cli/FirmwareCliParser.cs` | 手写纯解析器（AOT 友好，无反射）：`Parse(string[]? args) → FirmwareCliParseResult` |
| `Services/Cli/FirmwareCli.cs` | 入口 `Run(string[]? args) → int`：注册 `FirmwareFormatRegistry.RegisterAll()` → `FormatDetector.Detect` / `ResolveByFormatName` → 分发到 `IFormatHandler` / `FlashService` / `FirmwareService`；进度经 `IFirmwareProgress` 打印到 stdout |
| `Services/Cli/WorkflowCli.cs` | `workflow` 子命令入口：无头运行节点工作流（见下「工作流 CLI」） |

命令面（中英双语帮助）：

```
UotanStudio firmware list                          # 列出已注册格式与扩展名
UotanStudio firmware info <file>                   # 检测格式 + 读元数据
UotanStudio firmware unpack <file> -o <dir> [--no-overwrite] [--format <名称>]
UotanStudio firmware pack <input> -o <file> [--format <名称>]   # 目录→镜像；镜像→重打包
UotanStudio firmware flash <image> -p <分区> [--serial <序列号>] # 无头刷写（Fastboot/EDL/RKDL/ADB）
UotanStudio firmware disable-verification <vbmeta> -o <file>    # 置位禁校验标志并清零认证块（无需私钥）
UotanStudio workflow run <file.uotannode> [--set 节点.端口=值 ...] # 无头执行节点工作流
UotanStudio firmware --help | --version
```

退出码：`0` 成功 / `1` 失败（处理器错误、不支持格式、文件未找到）/ `2` 用法错误（缺参数/多参数/未知标志/未知子命令）。

### 工作流 CLI（`workflow` 子命令）与节点流控

- `Program.cs` 的 `Main` 同样在启动 Avalonia 前分发 `workflow` 子命令：`WorkflowPersistenceService.LoadAsync` 加载 `.uotannode` 快照 → `NodeFactoryRegistrar.RegisterAll()` 注册工厂 → 按 TypeKey 重建节点 → 构建引擎连接 → `NodeExecutionEngine.ExecuteAsync` 执行 → 按退出码约定返回。`--set 节点.端口=值`（可重复，节点按 Title 或 InstanceId 匹配）覆盖输入端口初始值。
- **节点工厂注册**：`NodeFactoryRegistrar.RegisterAll()`（`ViewModels/Nodes/NodeFactoryRegistrar.cs`）集中登记全部 TypeKey → 工厂映射，UI 工具箱初始化与无头 CLI 共用；新增节点类型必须在此登记（`InitializeToolbox` 仅负责本地化工具箱条目）。
- **连接持久化**：`ConnectionDto` 携带可选 `SourceInstanceId`/`TargetInstanceId`（`WorkflowPersistenceService.BuildConnectionDtos` 解析连接器所属节点写入）；`NodeEditorViewModel.LoadFromAsync` 与 WorkflowCli 优先按实例 ID 精确重建拓扑，缺失时回退按连接器标题匹配（旧版工程兼容）。
- **真实流控**：`NodeExecutionEngine` 识别源端口类型为 `Flow` 的连接为控制流边——`Flow.Branch` 把未选中分支的下游子图标记为 `Skipped`；`Flow.Loop` 按 `Count` 输入迭代执行 Body 子图并逐次写 `Index` 输出（上限 `NodeExecutionEngine.MaxLoopIterations = 10000`）；`Flow.Parallel` 经 `Task.WhenAll` 并发执行各 Flow 输出端口的分支子图（分支内保持拓扑顺序）。
- **数据流注入**：`FlowInputs` 先注入未连接输入端口的 `InitialValue`，再仅以上游**非空**输出值覆盖（Flow 端口等无数据连接不会清掉初始值）。

`--format` 显式指定格式（按 `IFormatHandler.FormatName` 不区分大小写匹配，如 `CPIO/TAR Archive`、`Android OTA Payload (payload.bin)`、`EXT4`、`EROFS`、`Boot`）；省略时 `unpack` 自动检测，`pack` 目录默认走 EXT4、文件按内容检测。

示例：

```bash
# 列出全部已注册固件格式
dotnet run --project UotanStudio.Desktop -- firmware list

# 解包 boot 镜像到目录
dotnet run --project UotanStudio.Desktop -- firmware unpack boot.img -o boot_out/

# 解包 payload.bin，显式指定格式
dotnet run --project UotanStudio.Desktop -- firmware unpack payload.bin -o payload_out/ -f "Android OTA Payload (payload.bin)"

# 把目录打包为 ext4 镜像
dotnet run --project UotanStudio.Desktop -- firmware pack system_dir/ -o system.img -f ext4
```

## 解决方案结构

> 入口点唯一：`UotanStudio.Desktop/Program.cs` → 构造 `AppBuilder.Configure<App>()`。

| 项目 | 职责 |
|---|---|
| `UotanStudio.Comm` | 设备通信库：USB 多后端（WinUSB/LibUsb/Linux/MacOS/HarmonyOS）+ ADB / Fastboot / EDL / RKDL 协议会话 + TCP/UDP/串口/mDNS 传输 + super 镜像构建/刷写（`SuperImageBuilder.Build` / `SuperFlashHelper.FlashSuper`，LP 元数据读写）。**零 Avalonia 依赖**，供应用层引用。 |
| `UotanStudio.Firmware` | 固件解析库：boot image（V0–V4 解包；V0–V3 boot 打包与 vendor_boot V3/V4 打包，`BootImagePacker`/`VendorBootPacker`）、Payload、Sparse、CPIO、AVB（`AvbVerifier` 校验 + `AvbSigner` 签名：vbmeta RSASSA-PSS 签名、v2 hash footer 与 `DisableVbmetaVerificationAsync` 禁校验补丁（置位 flags 0x1|0x2 并清零认证块，无需私钥））、压缩、DAT（Android v4 transfer list 写入，支持 `.new.dat.br` Brotli 输入与分段输出）、Oppo/NTPi/Nb0/Pac 等格式处理；**OTA 差分**（`Diff/` 目录，`DeltaEngine` 基于 BsDiff 生成/应用镜像差分；`DatFormatHandler`/`DiffFormatHandler` 已注册进 `FirmwareFormatRegistry`，DAT 镜像对与 bsdiff 补丁可自动识别）；**Payload 增量提取**（`PayloadParser.Extract/ExtractAsync` 支持**源基座注入** `sourcePayloadPath`/`sourceImages`：含源依赖操作（SOURCE_COPY/SOURCE_BSDIFF/BROTLI_BSDIFF/PUFFDIFF/BSDIFF/LZ4DIFF_*）的分区须提供基座镜像，未提供时抛 `PartitionExtractionException`；LZ4DIFF 经 `ProcessLz4diffBsdiff/ProcessLz4diffPuffdiff` 用 K4os LZ4 解压后复用 bsdiff/puffdiff 应用，解压输出经 `CountingWriteStream` 有界防炸弹；**Zucchini 全托管实现**（`Zucchini/` 目录，`ZucchiniPatchApplier`：补丁容器解析 + LEB128 编码 + CRC-32 校验 + RAW 元素应用，`ProcessZucchini` 接入顺序分发；非 RAW 可执行元素抛 `ZucchiniUnsupportedExecutableTypeException`；魔数 `0x0350635A` 与上游一致但无真实样本交叉验证，测试为写入器/应用器回环验证））；**EXT4/EROFS/F2FS**（`Ext4/`、`Erofs/`、`F2fs/` 目录，全托管文件系统镜像解析/构建/压缩，`Ext4FormatHandler`/`ErofsFormatHandler`/`F2fsFormatHandler` 经 `FirmwareFormatRegistry` 注册；**Ext4**：解析侧支持外部 xattr 块（`i_file_acl`，含 64-bit 高低位合并）与 ea_inode 大值读取、htree 未知哈希版本回退线性扫描；构建侧支持 htree(dx_dir) 大目录索引（`Ext4Htree` 原语，half-md4 哈希）、metadata_csum（superblock/GDT/inode 尾部 csum + 目录块 dir_tail 校验和，crc32c 与 e2fsprogs 算法一致）、JBD2 journal 写端（`Journal/`：`JournalSuperblock.CreateEmpty/ToBytes` 干净空日志 + csum v3，`Ext4Options.IncludeJournal` 默认开，内部日志 inode 方案）、INLINE_DATA 特性位、确定性默认 UUID（`Ext4Options.Uuid` 覆盖，相同输入两次 Build 逐字节一致）；**EROFS**：多设备设备表写端（`ErofsDeviceSlot`，`ExtraDeviceCount`）、fragments/dedup 写端（`Fragments`/`Dedup` 开关，整文件级去重）、metabox 写端（`Metabox` 开关，compact inode 槽表方案；fsck.erofs 1.8.6 不识别 METABOX 特性 0x100 属 1.9.x，metabox 镜像走自回环验证、flat 布局走 fsck 验证）、LZMA 压缩（MicroLZMA 属性字节与 `SetCoderProperties` 同源）；**F2FS**：superblock/checkpoint/NAT/SIT/dnode 寻址与 inline data、**double indirect 读写双向**（`F2fsParser.ReadDoubleIndirectAddress` + `F2fsBuilder.PlanDnodes`，Nid[3]/Nid[4] 槽位）、**压缩簇读写**（`F2fsCompression`/`F2fsCompressedCluster`：LZ4/ZSTD 压缩与有界解压，LZO NotSupported，`EnableCompression`/`CompressionAlgorithm`/`CompressionLevel` 选项，读侧按簇探测簇头、写侧逐簇压缩不值得压缩整文件回退）；WSL 工具级交叉验证（`WslCrossValidationTests`：e2fsck -fn / fsck.erofs / fsck.f2fs / dump.erofs / debugfs rdump，按工具粒度 `Assert.Skip`）；**DEX**（`DEX/` 目录，Dalvik 解析/反汇编/重建；**轻量化反编译**在 `DEX/Decompile/`：`BasicBlock`/`CfgBuilder`/`RegisterAnalyzer`/`MethodResolver`（重载解析：过滤 bridge/synthetic、按推断实参类型选最特定重载并对实参施加显式转型）/`ControlFlowStructuring`/`JavaCodegen`/`LightweightDecompiler`，参考 jadx 的 CFG 结构化思路，输出伪 Java 或字节码指导（BytecodeGuide 模式），明确不做 SSA/跨方法内联/lambda/泛型还原；`DexService.DecompileDexAsync` 供应用层调用）；**AXML**（`AXml/` 目录，Android 二进制 XML 解析/重建）；**ARSC**（`Arsc/` 目录，resources.arsc 资源表解析）。**零 Avalonia 依赖**。 |
| `UotanStudio` | 应用层：Avalonia 资源/主题（`avares://UotanStudio/...`）、ViewModel（继承 `ViewModelBase` → `ObservableObject`，按特性分区于 `ViewModels/{Shell,Workspace,Editors,Devices,Nodes,Compare}/`）、视图（`Views/{Shell,Workspace,Editors,Devices,Nodes,Compare,Controls}/`，与 ViewModel 同构）、服务（按域分区于 `Services/{Devices,Firmware,Flash,Projects,Compare,Mcp}/` 与根：`LanguageService`、`LogService`、`SettingsPersistenceService` 等）、消息（`WeakReferenceMessenger` 派生消息类在 `Messages/`）；**MCP 能力**（`Services/Mcp/`：`McpClientService` 懒连接并调用外部 MCP server 工具（stdio/HTTP，实参为原始 JSON，AOT 安全）、`McpHostService` 把固件能力暴露为 MCP server（TCP 流传输，`UotanStudioMcpTools`（partial，`[McpServerToolType]`）经 DI 发现全部 `[McpServerTool]` 方法：`firmware.info` / `firmware.unpack`（按格式分派解包）/ `firmware.pack`（cpio/boot/ext4/erofs 打包）/ `flash.image`（按 serial 或单设备刷写，15 分钟超时），输入输出为结构化 JSON）、`McpServerConfig`/`McpTransportKind` 配置模型；设置页「MCP」区块维护服务器列表与暴露开关/端口并持久化到 `AppSettings`（`AppSettingsJsonContext` 已注册）；工具箱「MCP 工具」分类含 `MCP.ListTools`/`MCP.CallTool` 节点；基于官方 `ModelContextProtocol` SDK 2.2.0 与 `Microsoft.Extensions.DependencyInjection`，均 AOT 兼容）。 |
| `UotanStudio.Desktop` | 启动项目：`WinExe`、`PublishAot=true`、`TrimMode=partial`（当前临时关闭，见 §1）。`Main` 返回 `int`，启动 Avalonia 前分发 `firmware` 子命令（无 UI 固件 CLI，见上「固件 CLI」）。 |

## 关键开发约定

### 1. AOT 兼容性（强制）
`UotanStudio.csproj` 与 `Desktop` 项目已启用 `IsAotCompatible` / `PublishAot` / `TrimMode=partial`（曾为支持 Avalonia 布局预览器临时关闭，现已恢复并经 `dotnet publish -r win-x64` 发布冒烟验证）。以下约束在 AOT 下必须遵守：
- **禁止 `Assembly.LoadFrom`、`Activator.CreateInstance(Type)` 等反射加载**。设备协议会话、USB 后端等均通过编译期类型直接构造（`UotanStudio.Comm` 提供工厂/会话类，无运行时加载）。
- **禁止运行时 `new ResourceInclude(uri)` 动态加载 XAML**（AOT 裁剪下抛 "No precompiled XAML found"）。主题/语言资源字典一律带 `x:Class` + 代码后置 partial 类（`ThemeDictionaries.cs`/`EnUsDictionaries.cs`/`ZhCnDictionaries.cs`，构造函数 `AvaloniaXamlLoader.Load(this)`），切换经 `LanguageService.ApplyTheme` / `CreateLocaleDictionary` 类型实例化；`.axaml` 由 Avalonia 默认 glob（`**/*.axaml`）纳入，csproj 不得再显式 `AvaloniaResource Include="Locale\**"`/`Themes\**`（与默认项重复触发 AVLN2002 重复 x:Class）。
- JSON 序列化使用**源生成**：`AppSettingsJsonContext : JsonSerializerContext`，配合 `AppSettingsJsonContext.Default.AppSettings`。新增需序列化的类型必须用 `[JsonSerializable]` 注册到该上下文。
- View 解析无反射：`ViewLocator` 经 `ViewModelViewRegistry`（`UotanStudio/ViewModelViewRegistry.cs`）按 VM 类型显式查表构造视图，**不依赖反射**；新增 ViewModel 须在该注册表登记（View 与 ViewModel 均按特性分区，见 §5）。

**Debug 诊断工具（仅 DEBUG 启用，`#if DEBUG` 隔离 + 包引用按 `$(Configuration)` 条件剔除，不进入 Release/AOT 发布）：**
- `AvaloniaMcp.Diagnostics`（`UotanStudio.Desktop/Program.cs` 末尾 `.UseMcpDiagnostics()`）：启动命名管道 MCP 诊断服务器。
- `Dock.Avalonia.Diagnostics`（`MainWindow` 打开时 `AttachDockDebugOverlay()`）：按 F9 显示 Dock 布局覆盖诊断。

### 1.1 有界流式 IO（固件库强制）
`UotanStudio.Firmware` 处理的固件、分区与压缩数据均视为**不可信且可能达到数十 GB**；新增或修改解析、提取、构建、压缩/解压代码时必须遵循：
- **Stream-first / FileStream-first**：文件路径 API 内部直接打开流；禁止以 `File.ReadAllBytes`、无界 `ReadToEnd`、`MemoryStream.ToArray` 或整镜像 `byte[]` 作为大文件主路径。`byte[]` API 仅作为明确的小型元数据或便利包装。
- **所有范围必须有界**：外部长度、偏移、块数、extent、chunk count 在使用前以 `long`/`ulong` 和 `checked` 验证；范围检查使用 `offset <= length && size <= length - offset`，避免 `offset + size` 溢出。
- **精确复制与截断失败**：固定长度数据使用 `Common/Streams/StreamCopy.CopyExactly[Async]` 或等价循环；提前 EOF 必须抛出格式异常，不得静默生成部分输出。
- **解压必须限制输出**：调用 `CompressionService` 的有界重载并由格式语义提供 `maximumOutputBytes`（如 Payload 目标 extents、EROFS cluster、NTPi OriginalSize）；禁止对不可信数据执行无上限解压。
- **随机访问使用范围流**：优先用 `Common/Streams/BoundedReadStream` 或格式专用 bounded stream 表示 `(offset,length)`；解析器只保留元数据与文件范围，提取时再流式复制。
- **多 extent 输出必须严格映射**：Payload 等格式使用 `ExtentWriteStream` 映射目标区间，拒绝超长输出并在完成时校验精确长度。
- **构建器采用两遍模型**：第一遍扫描并计算布局，第二遍 `FileStream.SetLength` 后定位写入/回填；禁止为 EXT4、EROFS、Sparse、super 等大型输出构造完整镜像数组。
- **输出原子提交**：不可信输入的提取/解压先写同目录临时文件，全部长度、哈希和格式校验成功后再原子替换；失败或取消时清理临时文件。
- 公共流包装器位于 `UotanStudio.Firmware/Common/Streams/`；新增 public 类型/成员须提供中英文 XML 文档并保持 AOT 兼容。

### 2. 节点编辑器（NodifyM）
1. 节点定义与注册：所有节点视图模型继承 `FlowNodeViewModel`（`UotanStudio.ViewModels.NodeViewModels`），工厂方法 `XxxNodeViewModel.CreateXxxNode()` 在初始化器中设置稳定 `TypeKey`（如 `"Firmware.Unpack"`、`"Data.ReadFile"`）与各连接器的 `PortType`。`NodeTypeRegistry`（`NodeViewModels/NodeTypeRegistry.cs`）以 TypeKey 注册工厂；**节点工厂在 `NodeEditorViewModel.InitializeToolbox` 中集中注册**（既有节点直接 `NodeTypeRegistry.Register(key, () => CreateXxxNode())`，DEX 节点经 `DexNodeViewModel.RegisterNodes()`），同时以 `NodeToolboxItem(name, () => CreateXxxNode())` 声明工具箱，分类为 `Firmware` / `Flash` / `Data` / `Flow` / `Output` / `Dex` / `Mcp`（Dex 分类含 `Dex.Parse` / `Dex.Disassemble` / `Dex.Rebuild` / `Dex.Decompile`——反编译节点输入 Dex Path/Output Path，输出 Result，把 DEX 还原为伪 Java 源码；MCP 分类含 `MCP.ListTools` / `MCP.CallTool`——列举服务器工具、以 JSON 实参调用工具并输出结果，经 `McpNodeViewModel` + `McpClientService` 调用设置页配置的 MCP 服务器）。**各节点 `ExecuteAsync` 一律按 `TypeKey` 分发（`switch (TypeKey)`），不得按显示标题分发。**
2. 端口类型约束：`NodePortType` 枚举（`Any`/`Flow`/`Path`/`Data`/`Text`/`Number`/`Boolean`）由 `NodeConnectorViewModel.PortType` 承载，兼容性判定在 `NodeDataUtil.CanConnect`（`Any` 通配、其余须相等），`NodeEditorViewModel.CreateConnection` 拒绝不兼容连接；端口类型徽章显示在节点图标连接器上。
3. 选择与属性面板：`NodeEditorView` 订阅 `NodifyEditor`（`SelectingItemsControl`）的 `SelectionChanged` 同步到 `SelectNode()`，驱动右侧属性面板与一键删除（删除 `IsSelected` 节点）。
4. 工作流执行由 `NodeExecutionEngine`（拓扑排序 + 异步调度 + **真实流控**：Branch 标记 Skipped 子图 / Loop 迭代执行 Body 子图（上限 `MaxLoopIterations`）并逐次写 Index / Parallel 经 `Task.WhenAll` 并发执行分支子图；Flow 类型连接即控制流边）驱动，状态通过事件回传画布。
5. 工程加载：`LoadFromAsync` 按 `NodeDto.TypeKey` 经 `NodeTypeRegistry` 重建具体节点；未注册 TypeKey 时跳过该节点并记录警告日志（软件未发布，无旧工程占位兼容需求）。
6. 项目插件系统已移除：不再有 `IPlugin` / `PluginRegistry` / `UotanStudio.Abstractions` / `UotanStudio.Plugins.BuiltIn`。

### 3. 本地化（`Locale/`）
- 资源字典 `Locale/{culture}/{Module}.axaml` 作为 `AvaloniaResource` 嵌入，运行时由 `LanguageService` 动态切换。**每个字典带 `x:Class`**（`Locale/en-US|zh-CN/*.axaml` → `UotanStudio.Locale.EnUs|ZhCn.*`，代码后置集中在 `UotanStudio/EnUsDictionaries.cs` / `ZhCnDictionaries.cs` 的 `partial class : ResourceDictionary`，构造函数 `AvaloniaXamlLoader.Load(this)`）；`LanguageService.CreateLocaleDictionary(culture, module)` 以类型实例化切换（AOT 安全），**禁止运行时 `new ResourceInclude(uri)` 反射加载**（裁剪下报 "No precompiled XAML found"）。
- 模块：`Common`、`Dock`、`Menu`、`Node`、`Settings`、`Device`。
- 新增键必须**同时在 `en-US` 和 `zh-CN` 中补齐**；查找键通过 `LanguageService.Instance.GetString("Key")`。
- 添加新语言：创建 `Locale/{culture}/` 并在 `LanguageService` 注册。
- **设置页开源致谢区块**：`SettingsView.axaml` 底部「开源致谢」区块展示本项目使用/参考的开源项目，数据源为 `SettingsViewModel.OpenSourceProjects`（`UotanStudio/Models/OpenSourceProject.cs`，静态只读集合，AOT 友好），每条含名称/描述/仓库链接/协议/作者 GitHub 链接与头像（GitHub avatar URL），「仓库」「作者」按钮经 `SettingsView.OnOpenLinkClick` 用系统浏览器打开链接；区块标题与商业许可说明文案来自 `Settings_OpenSourceCredits` / `Settings_OpenSourceNotice` / `Settings_OpenRepository` 本地化键。

### 4. 消息（`CommunityToolkit.Mvvm.Messaging`）
跨组件通信统一用 `WeakReferenceMessenger.Default`。消息类定义在 `UotanStudio/Messages/`，命名约定 `XxxMessage` / `XxxChangedMessage`。发送用 `WeakReferenceMessenger.Default.Send(msg)`，接收方在 `App` 等处理类用 `.register<MessageType>(this, handler)`。

### 5. ViewModel 与 View 命名约定
ViewModel 类名 → 同名 `View`（替换 `ViewModel` 为 `View`），且**目录同构**：`ViewModels/{Shell,Workspace,Editors,Devices,Nodes,Compare}/` ↔ `Views/{Shell,Workspace,Editors,Devices,Nodes,Compare}/`（纯控件与布局工厂在 `Views/Controls/`、`Views/Workspace/WorkspaceDockFactory.cs`、`Views/Nodes/NodeDockFactory.cs`）。解析统一走 `ViewModelViewRegistry`（显式注册表，非反射）。ViewModel 统一继承 `ViewModelBase : ObservableObject`，属性用 `[ObservableProperty]` 自动生成。

### 6. 主题与样式
- 主题资源：`Themes/LightColors.axaml` / `DarkColors.axaml`（`ResourceDictionary`，含 `PageBackground`、`AccentColor` 等画刷）。**两字典带 `x:Class`**（`UotanStudio.Themes.LightColors|DarkColors`，代码后置在 `UotanStudio/ThemeDictionaries.cs`，构造函数 `AvaloniaXamlLoader.Load(this)`）；`LanguageService.ApplyTheme` 以类型实例化切换（`new LightColors()` / `new DarkColors()`），**禁止运行时 `new ResourceInclude(uri)` 反射加载**。
- **Fluent 兼容键**：主题字典额外定义 `TextFillColorSecondaryBrush`（`SolidColorBrush`，取 `SecondaryTextForeground` 颜色）——Semi 主题不提供该键，视图多处 `DynamicResource` 引用，缺失时解析回退默认色导致不随主题切换。
- **比对高亮键**：主题字典定义 `DiffBackgroundBrush`（`SolidColorBrush`：浅色主题 `#FFF3D9`，深色主题 `#5A3A00`），`BinaryCompareViewer` 双栏 hex 与文本比较的差异行高亮使用。
- 样式层：Semi.Avalonia + Ursa + Material.Icons + NodifyM，在 `App.axaml` `Application.Styles` 合并。切换主题调用 `LanguageService.Instance.ApplyTheme("DarkColors")`。
- **浮动文档标题栏**：`App.axaml` 对 `HostWindow[IsToolWindow=False]` 统一启用 `ExtendClientAreaToDecorationsHint=True`、`ExtendClientAreaTitleBarHeightHint=32`、`WindowDecorations=Full`，使用 Semi 的 `WindowDrawnDecorations` 绘制标题栏与窗口按钮，覆盖 Dock 默认的原生标题栏设置。`Title` 通过 `DynamicResource Dock_Documents` 统一显示“文档”（英文为“Documents”），文档标签仍显示各自名称。按属性匹配，不使用 Dock 当前未设置的 `:documentwindow` 伪类。工作区与节点编辑器的文档浮动窗口共用此样式；工具面板保留 Dock 的工具标题栏行为。
- **浮动文档图标**：同一全局样式将 `HostWindow.Icon` 设为 `/Assets/UotanToolbox.png`，与主窗口共用系统图标。通过 `WindowDrawnDecorations` 模板中的 `PART_TitleTextPanel.Background` 使用 `ImageBrush` 在左侧绘制 25×25 的同一图片，标题文字左侧留白 40、右侧留白 136 并启用省略，保留 Semi 原有窗口按钮与拖动区域；主窗口已有的顶栏图片不受影响。
- **全局紧凑菜单**：`App.axaml` 统一设置 `MenuItem` 字号 12、最小高度 24、内边距 `8,2`、外边距 0，`ContextMenu` 内边距 4，菜单内 `Separator` 外边距 `4,2`；通过 `ContextMenu, ContextMenu MenuItem, MenuItem MenuItem` 将右键菜单与下拉菜单项的最小宽度设为 200，顶部菜单栏标题按内容宽度显示；顶部菜单栏、下拉子菜单和右键菜单共用全局样式，视图中不再单独指定菜单字号与间距。

### 7. 日志
统一走 `LogService.Instance.Write(LogLevel, "Source", "Message")`，UI 日志窗口通过 `LogEnqueued` 事件或 `Subscribe` 增量消费。`LogLevel` / `IUnifiedLog` 定义在 `UotanStudio/Services/Logging/IUnifiedLog.cs`（命名空间 `UotanStudio.Services`），`LogService` 实现 `IUnifiedLog`；`UotanStudio.Comm` 的 USB 诊断通过 `UsbLogBridge` 桥接到统一日志。**分通道日志**：`LogService.ChannelOf(source)` 按 Source 前缀把日志归入 `LogChannel`（`General`/`Device`/`Flash`/`Firmware`/`Build`，`LogEntry` record 不变），`LogViewModel` 提供通道 Tab（`Log_Channel_*` 键）与既有级别/来源筛选叠加。

### 8. 工作区面板与工程文件
工作区（`WorkspaceView` / `WorkspaceDockFactory`，`Views/Workspace/`）承载 Dock 布局：
- **面板构成**：左侧（项目资源管理器 / 设备；**打包设置面板默认隐藏**，经「窗口」菜单唤出）、中部文档区（`DocumentDock`，Id=`DocumentsPane`）、右侧（镜像管理 / 属性）；底部 `BottomPane` 保留为**空槽位**（无面板）。输出 / ADB 日志面板已移除，工作区操作日志统一走全局 `LogService`。面板显隐/聚焦由 `WorkspacePanelService`（`Services/Workspace/WorkspacePanelService.cs`，单例）统一管理，`WorkspaceDockFactory.CreateLayout` 用 `Register(key, dockable, host, visible)` 登记各面板，`CloseDockable` 关闭面板后回调 `OnDockContentsChanged` 同步可见状态。
- **顶部菜单「窗口」栏目**：`MainWindow.axaml` 在 视图 与 工具 之间提供「窗口」菜单，各面板项以 CheckBox 形式勾选当前可见性（绑定 `WorkspacePanelService` 的可观察属性），命令由 `MenuViewModel` 的 `ToggleProjectExplorer` / `ToggleProjectSetting` / `ToggleDevicePanel` / `ToggleImagesView` / `ToggleProperties`（`[RelayCommand]`）转发到 `WorkspacePanelService.Toggle(key)`。窗口菜单**不管理**设备管理器 / 日志等独立窗口（仍由「工具」菜单打开）。
- **文档编辑器**：`FileView` 使用 `Views/Controls/CodeEditor`（继承 AvaloniaEdit `TextEditor`，TextMate 语法高亮，LightPlus/DarkPlus 随主题切换）；`FileViewModel`（`ViewModels/Editors/`）承载 `Text` / `Language` / `FilePath` / `IsDirty`，`FileView` 双向绑定；语言按扩展名由 `SyntaxLanguageRegistry.GetLanguageId` 解析（`.prop/.rc→ini`、`.xml→xml`、`.json→json` 等）。`WorkspaceViewModel.OpenDocumentAsync(path)` 打开/聚焦文档，`App` 注册 `OpenDocumentRequestMessage` 转发。特殊格式走专用编辑器：DTB/DTBO → `DtbEditorViewModel`（左节点树 + 右 DTS 源码）；`.dex` → `DexEditorViewModel`（只读反汇编，经 `FirmwareFormatRegistry` 注册的 `UotanStudio.Firmware.Dex.DexFormatHandler` 解析，`DexService` 提供 Parse/Disassemble/Rebuild 服务）；AXML（`AndroidManifest.xml` 等二进制 XML）→ `ManifestEditorViewModel`（左元素结构树 + 右属性编辑与 XML 预览，经 `UotanStudio.Firmware.AXml.AXmlFormatHandler` 解析，保存时以 `AXmlWriter` 重建）；resources.arsc → `ArscEditorViewModel`（左包→类型→配置→条目资源树 + 右条目详情，经 `UotanStudio.Firmware.Arsc.ArscFormatHandler` 解析，只读浏览）；文件系统镜像（raw img：扩展名命中 `.img/.ext4/.erofs/.f2fs` 且内容魔数命中 ext4 `0xEF53`@1080 / EROFS `0xE0F5E1E2`@1024 / F2FS `0xF2F52010`@1024）→ `FileManagerEditorViewModel`（`ViewModels/Editors/`，左内部文件系统目录树 + 右条目详情，经 `Ext4Parser`/`ErofsParser`/`F2fsParser` 流式解析，支持刷新与提取选中条目；`FileOpenMode.ResolveMode` 在固件分支前按内容魔数路由到 `EditorOpenMode.FileSystem`）；分区表（LP super 元数据或 Rockchip parameter 内容）→ `PartitionEditorViewModel`（`ViewModels/Editors/`，左分区树 + 右属性编辑（分区名/起始/大小/类型），LP 经 `UotanStudio.Firmware.Lp` 元数据读写，Rockchip 经 `RockchipParameter` 解析并重建 `CMDLINE: mtdparts=…` 保存，`FileOpenMode.ResolveMode` 按内容路由到 `EditorOpenMode.Partition`）；固件文件（扩展名命中格式处理器注册表，如 `.img/.pac/.ops/.nb0`）→ `FirmwareInfoViewModel`（`ViewModels/Editors/`）+ `FirmwareInfoView`，展示格式名/摘要/元数据/条目（数据来自 `FormatDetector.Detect` + `IFormatHandler.ReadMetadataAsync`），工具栏含**动作按钮**：「校验」（AVB 格式走 `FirmwareService.VerifyAvbAsync`，其余流式 SHA-256）与「解包到工程目录」（按格式分派解包到 `intermediate/<文件名>/`，经 `ProjectContextService.EnsureIntermediateDirectory`）。打开方式由 `FileOpenMode.ResolveMode`（`EditorOpenMode`：`Auto/Text/Hex/Dtb/Dex/Axml/Arsc/Apk/Firmware/FileSystem/Partition`，AXML 按内容魔数优先于 `.xml` 文本路由，ARSC 按内容魔数兜底，文件系统镜像按内容魔数优先于固件面板；**已知固件扩展名集合由 `FormatDetector.RegisteredHandlers` 的 `FileExtensions` 聚合**，`IsFirmwareExtension` 统一驱动固件面板与 Hex 路由，避免双源维护）路由，资源管理器右键「打开方式」菜单（含「在固件信息中打开」）与双击（Auto 模式）复用同一消息。**OS 文件/目录拖拽**：`WorkspaceView` 根容器挂 `DragDrop`（Avalonia 12 `DataTransfer`/`DataFormat.File` 双保险，与 CompareView 一致），拖入文件按 `FileOpenMode.ResolveMode` 路由打开、拖入目录则枚举直接子级中的固件扩展名文件经 `MenuViewModel.ImportFilesAsync` 导入工程，混合拖拽只取文件。
- **文件/文件夹比较（BC 式）**：服务层在 `Services/Compare/`（`FolderCompareService` 递归目录比对：先比大小、再流式 SHA256，状态 Equal/Different/LeftOnly/RightOnly，含复制/删除动作；`TextDiffService` 基于 **DiffPlex** 的行级 diff + 行号对齐，大文件有界流式；`BinaryDiffService` 流式单遍差异统计（不物化区间列表）+ 可选行差异位图（每 16 字节行 1 bit，内存与文件长度成正比）/`IsTextFile` NUL 探测）。**对比面板为工作区文档区中的统一双栏视图**（`ViewModels/Compare/` ↔ `Views/Compare/`，底部不再提供对比入口）：`CompareView` 左右分栏（左栏 | 分隔线 | 右栏），由 `CompareViewModel : Document`（`ViewModels/Compare/`，经 `ViewModelViewRegistry` 显式注册 `CompareView`）承载三种内容模式 `CompareMode`（`Folder`/`Text`/`Binary`）；每栏经新建 `ComparePaneView` 或直接双 `ListBox` 渲染同一行模型 `CompareRowViewModel`（`ViewModels/Compare/`，含 `Status`/`Depth`/左右单元格文本/`IsLeftDiff` 等），但**左右各自绑定独立行列表**（`CompareViewModel.LeftRows`/`RightRows`，文件夹模式由 `RebuildSideRows` 按单元格空态过滤：仅左侧条目不显示在右侧、仅右侧条目不显示在左侧，不跨栏重复；文本/十六进制模式两侧共享 `Rows`）；仅单侧路径时该侧渲染内容、另一侧置空态提示（`Compare_DropRightFile`/`Compare_DropRightFolder`）；相同两侧都显示无高亮、不同两侧都显示并整行 `DiffBackgroundBrush` 差异高亮，仅单侧条目在所属侧整行淡色背景（`CompareRowViewModel.LeftTintBrush` 橙调 / `RightTintBrush` 蓝调，`ForFolder` 对 Equal/Different 两侧填名称与大小、LeftOnly/RightOnly 缺失侧单元格置空）；点击任一侧条目按引用在另一侧查找同一行对象联动选中（找不到则不联动，`_syncingSelection` 防回环）；两侧 `ListBox` 内 `ScrollViewer` 经代码后置同步滚动（`_syncing` 防回环）。**二进制/固件文件比较**：`OpenCompareFilesAsync` 内容嗅探——文本 → `Text` 模式（`TextDiffService` 流式行 diff）；二进制（含可识别固件 `.img` 等）→ `Binary` 模式默认展示解析元数据（`FormatDetector.Detect` + `IFormatHandler.ReadMetadataAsync` 得 `FirmwareOpResult`，左右并排显示格式名/摘要/键值/条目/组件），工具栏「`Compare_HexRaw`」按钮切换十六进制原始数据对比（`BinaryViewKind` `Metadata`↔`Hex`，hex 行经 `CompareHexRowCollection` 懒加载集合流式按需读取 + 行差异位图（`BinaryDiffService.CompareStats` 可选输出，每 16 字节行 1 bit）差异高亮，不整读大文件）。**已删除旧结构**：`CompareFolderView`/`CompareFileView`/`CompareBinaryView`（及各自 `.axaml.cs`）、`CompareFolderViewModel`/`CompareFileViewModel`/`CompareBinaryViewModel`、`FolderCompareItemViewModel`、`Views/Controls/BinaryCompareViewer.cs`（`HexViewer`/`HexMetrics` 仍保留供 `HexEditor` 使用）。文本比较（`TextDiffService`）与文件夹比较（`FolderCompareService`）服务层不变。**OS 文件拖拽**：`CompareView` 挂载 `DragDrop`（Avalonia 12 的 `DataTransfer`/`DataFormat.File` 双保险），拖入两个文件 → 按内容嗅探进入 `Text` 或 `Binary` 模式比较，两个目录 → `Folder` 模式文件夹比较，单个路径 → 替换当前比较的单侧（该侧渲染内容、另一侧置空态提示）。入口统一经 `WorkspaceViewModel.OpenCompareDocument()` 创建或聚焦工作区中的同一对比文档并导航至 Workspace；「视图」→「对比」发送 `OpenCompareRequestMessage` 打开空文档或聚焦已有文档；目录树「开始对比」以右键菜单的 `ExplorerNode` 为准发送带 Path 的同一消息，`CompareViewModel.AddComparisonPathAsync` 首次填左侧，后续同类路径填充/替换右侧，文件与目录互换时重开左侧（按已设置的路径配对，支持首次加载中再次选择）。「工具」菜单 `Menu_CompareFiles` / `Menu_CompareFolders` → `MenuViewModel.CompareFilesCommand` / `CompareFoldersCommand`（两次选择器）→ `OpenFileCompareRequestMessage` / `OpenFolderCompareRequestMessage` → `App.axaml.cs` 在同一文档中加载双侧。关闭对比文档由 `WorkspaceDockFactory` 调用 `Cleanup()` 取消加载并释放十六进制文件流；再次打开创建空文档。底栏只保留 Workspace=3、NodeEditor=4、Settings=5 的导航映射；`MainViewModel.CompareVM` 与 Compare 导航枚举已移除。`Compare_*` 本地化键（空态提示/工具栏文案等）保留。DiffPlex 依赖集中在 `Directory.Packages.props`。
- **镜像面板**：`ImageViewModel`（`ViewModels/Workspace/ImageViewModel.cs`）为单例（`EnsureInstance()` / `Instance`），维护 `Images`（相对工程根目录的 `ImageEntryViewModel`）与 8 个命令（提取 / 合成 img / 合成 br / 删除 / 合成 super / 合成 dat / 打包镜像 / 全选刷入），操作经 `FirmwareService`（`Services/Firmware/FirmwareService.cs`，`SynthesizeSuperAsync` 等）、`PackingService`（`Services/Firmware/PackingService.cs`，按打包设置面板的镜像类型/格式/压缩选项调 `Ext4Builder`/`ErofsBuilder` 打包系统镜像）与 `UotanStudio.Firmware.Dat.DatWriter` 落到 `intermediate/`；文件菜单含「导入镜像」「导出镜像」。`ImageView`（`Views/Workspace/ImageView.axaml(.cs)`）以 `DataGrid` 展示镜像（勾选列 / 名称 / 格式 / 大小），当前行 `SelectedImage` 用于单文件操作与属性面板，`ImageEntryViewModel.IsSelected` 复选框独立维护批量选择集（提取、删除、合成 super、批量刷入消费全部勾选项）；表格行为条目提供**右键菜单**（提取/合成 img/br/super/dat/打包镜像/删除/全部刷入，Click 处理器先 `SelectedImage = 菜单条目` 再执行命令）。批量刷入经 `BatchFlashService`（`Services/Flash/BatchFlashService.cs`，新建）按勾选分区列表逐分区复用 `FlashService.FlashImageAsync`，单分区失败继续，进度回报 `StatusText`。镜像面板顶部「向导刷写」按钮（`ImageView_FlashWizard` 键）打开 **`FlashWizardView`**（`FlashWizardViewModel`，`Messages/FlashWizardMessages.cs` 的 `FlashWizardConfirmRequestMessage` 请求确认）：选分区镜像 → 选目标设备（跟随 `DeviceViewModel` 选中）→ 模式白名单（Fastboot/RKDL 分区名非空即可刷、EDL 需分区名在 `rawprogram*.xml` 命中（缺失时回退基础校验）、ADB 禁用）→ `FlashService.FlashImageAsync` 刷写（进度回报 `StatusText` + `ProgressPercent` 并转发 `FlashProgressMessage`）→ 可选 `RebootAsync` 重启。
- **工程文件（VS 式布局）**：`SolutionPersistenceService`（`Services/Projects/`，静态，`SolutionJsonContext` 源生成 JSON）读写三类文件——解决方案 `<名称>.uotan`（`SolutionSnapshot`：解决方案名 + 引用的固件/节点项目相对路径）、固件项目 `<名称>.uotanfw`（`FirmwareProjectSnapshot`：镜像清单 `ImageEntryDto`（相对路径）+ 打包设置 `ProjectSettingsDto`）、节点项目 `<名称>.uotannode`（`NodeProjectSnapshot`：节点画布 `NodeDto`+`ConnectionDto`）。新建 / 打开 / 保存由 `MenuViewModel` 编排；`MenuViewModel.OpenProjectFromPathAsync(path)` / `ImportFilesAsync(paths)` 供菜单与命令行共用。旧单文件 `.uotan`（`ProjectSnapshot`）已废弃。
- **工程目录约定**：`ProjectContextService`（`Services/Projects/`，单例）承载当前解决方案：`RootDirectory` = 解决方案文件同名目录，`images/` 存导入镜像，`intermediate/<操作>/` 存中间产物（解包、合成、deleted），`.uotan/` 存会话状态（`settings.json` + `history.json`，类似 VS 的 `.vs/`）。镜像条目一律存相对路径。
- **操作历史**：`OperationHistoryService`（`Services/Projects/`，单例）维护内存撤销/重做栈（容量 50），工作区操作通过 `Record(description, [HistoryOperationDto,] undo, redo)` 登记；带 `HistoryOperationDto` 的条目可经 `SaveAsync`/`LoadAsync` 持久化到 `.uotan/history.json` 并跨会话重建（重建工厂由 `ImageViewModel` 注册到 `ReconstructOperation`）。`MenuViewModel` 的 Undo/Redo 优先执行历史，未命中再转发 `NodeEditorActionMessage`。
- **窗口标题**：`MainViewModel.WindowTitle`（`"<解决方案名> - UotanStudio"`）绑定 `MainWindow.Title` 与顶部标题栏 `TextBlock`，随 `ProjectContextChangedMessage` 更新。

### 9. 设备管理器
设备监视与协议交互**统一基于 `UotanStudio.Comm`**，由 `DeviceMonitorService`（`UotanStudio/Services/Devices/DeviceMonitorService.cs`，单例）驱动：

**核心组件：**
- `DeviceMonitorService`：启动时执行全量 USB 扫描并监听热插拔（Windows 原生 WM_DEVICECHANGE，其他平台轮询快照）与 adb server 设备变更；结果缓存至下一次变更，提供 `Scan()` / `ForceRefreshAsync()` / `FindDeviceBySerial()` / `OpenUsbSession()` / `OpenAdbSession()` / `OpenFastbootSession()` / `OpenEdlSession()` / `OpenRkdlSession()` / `RebootAsync()`。与 Google 官方 adb server（127.0.0.1:5037）交互使用 `AdvancedSharpAdbClient`（探测结果缓存 3 秒，server 未运行时静默降级为仅 USB 源）。
- `DetectedDevice` / `DeviceProtocolKind`（Adb/Fastboot/Edl/Rkdl）/ `DeviceTransportKind`（Usb/Tcp/Serial）：枚举结果模型，稳定键为 USB `DeviceKey` 或 `"tcp:host:port"`。
- `DeviceViewModel`（`UotanStudio/ViewModels/Devices/`）为全局单例（`EnsureInstance()` / `Instance`），主窗口底部 Device 选项卡与 Workspace 停靠面板共享同一实例，避免双实例双订阅；`DeviceInfo` 包装枚举设备并承载拉取到的属性（ADB getprop / Fastboot getvar）。除基础的刷新/连接/断开/重启（system/bootloader/recovery/edl）外，还提供**快速固件操作**命令：`OpenAdbShellCommand`（仅 ADB，新控制台执行 `adb -s <serial> shell`）、`FastbootReadInfoCommand`（仅 Fastboot，读 getvar 分区信息）、`QuickFlashCommand`（ADB/Fastboot/EDL/RKDL，选镜像经 `FlashService.FlashImageAsync` 刷写，进度回报 `StatusText`，刷写前经 `FlashConfirmationRequested` 请求确认）、`BackupPartitionCommand`/`RestorePartitionCommand`（仅 Fastboot，经 `BackupService.BackupFastbootPartition`/`RestoreFastbootPartition` 备份/恢复分区镜像，进度回报 `StatusText`，进度经 `BackupProgressMessage` 转发统一日志）；`DeviceView` 工具栏按钮与设备条目**右键菜单**（`Views/Devices/DeviceView.axaml(.cs)`，Click 处理器先 `SelectedDevice = 菜单设备` 再执行命令）均绑定这些命令。
- 刷写：`UotanStudio/Services/Flash/FlashService` 统一封装 Fastboot 与 EDL（`Firehose`）两条路径，进度经 `FlashProgressMessage` 广播；Fastboot 路径支持 **sparse 感知分片刷写**（`FlashImageChunked`：镜像 ≤ max-download-size 时单次 download+flash（稀疏原样交由 bootloader）；超过时转/拆为稀疏分片顺序刷写，行为对齐 AOSP fastboot）；Fastboot 路径在设置 `AppSettings.AutoBackupBeforeFlash`（`SettingsPersistenceService`）开启时，刷写前自动备份目标分区到工程 `.uotan/backup/`（失败仅记警告，不中断刷写）。
- 固件：`UotanStudio/Services/Firmware/FirmwareService` 基于 `UotanStudio.Firmware` 封装 CPIO 解包/打包、Payload 提取、AVB 校验与签名（`VerifyAvbAsync` / `SignVbmetaAsync` / `AddHashFooterAsync`，经 `UotanStudio.Firmware.Avb.AvbSigner`）、Oppo 全格式读取（OFP/OPS/OZIP，见下）等。
- **Oppo OZIP 解包**：`UotanStudio.Firmware/Oppo/` 提供 OFP/OPS/OZIP 三种 Oppo 固件格式的统一解析与解包。OZIP（`OPPOENCRYPT!` 魔数）Mode 1 每个加密单元由 16 字节 AES-128-ECB 密文前缀和随后的 0x4000 字节明文组成，总步长为 0x4010；解密后得到内部 ZIP 文件，包含 `.img` 分区镜像、`.dat.br` OTA 数据与配置文件。密钥自动检测遍历 `OzipKeyDatabase`（`Oppo/Crypto/KeyDatabase/OzipKeyDatabase.cs`，含35个已知设备密钥）。`OzipBlockDecryptStream` 提供可定位的按需解密视图，解析与提取通过 `ZipArchive` 直接读取大文件，无需生成完整临时 ZIP；条目输出使用有界写入与同目录临时文件原子提交，解压错误会明确失败而非静默只留下早期配置文本。
- USB 诊断：`UsbLogBridge`（`UotanStudio/Services/Devices/UsbLogBridge.cs`，`IUsbLogger`）把 `UotanStudio.Comm` 的 `UsbTrace` 输出桥接到统一日志，`UsbLogBridge.Install()` 安装。

**关键约定：**
- USB 设备按 VID:PID 分类协议（Qualcomm 9008 / MTK 0E8D / Rockchip 2207 / Allwinner 1F3A 等），与工具无关的设备（摄像头/键鼠）变更不触发重扫。
- adb server 未运行时，USB 设备序列号回退为 VID:PID；ADB 条目按序列号去重（多后端重复枚举场景）。
- `DeviceViewModel` / `DeviceView` 在 `ViewModels/Devices/` ↔ `Views/Devices/` 分区，经 `ViewModelViewRegistry` 显式注册解析（见 §5）。

**AOT 注意事项：**
- `UotanStudio.Comm` 无反射：USB 后端（WinUSB/LibUsb/Linux/MacOS/HarmonyOS）按平台编译期选择，协议会话直接构造。
- `LibUsbDotNet` 通过 `LibUsbNativeLoader` 托管加载，`Makaretu.Dns.Multicast` 为纯托管 mDNS，`System.IO.Ports` 在 .NET 10 下完全支持 AOT。
- `AdvancedSharpAdbClient` 用于 adb server 交互（网络设备/状态/重启），与 `UotanStudio.Comm` 原生协议并存。

## 测试
- `UotanStudio.Tests`（xunit.v3 + .NET 10 MTP 测试通道，`dotnet test -c Debug`；若报「零测试」直接运行 `./UotanStudio.Tests/bin/Debug/net10.0/UotanStudio.Tests.exe`）覆盖 Comm / Firmware / Compare / Services / ViewModels / Views。Comm 侧经 `UotanStudio.Comm/Properties/AssemblyInfo.cs` 的 `InternalsVisibleTo("UotanStudio.Tests")` 访问内部接缝（USB 抽象层 `UsbDevice` 派生、`UsbDeviceMonitor`/`UsbDeviceIdentity`/`UsbProviderProjection` 纯逻辑、Hdlc/Hsuart/TCP/UDP 传输、Firehose/Sahara XML 命令与响应、FlashBundleOrchestrator 编排、SuperImageBuilder/SuperFlashHelper、UsbTrace 事件）；真实硬件 I/O（USB 后端、热插拔真实枚举、mDNS 组播）不测。
- **文件系统特性测试**（`UotanStudio.Tests/Firmware/`，均为 xunit.v3）：`Ext4HtreeAndCsumTests`（htree 大目录全条目列全、inode csum 回算匹配）、`Ext4JournalTests`（JBD2 写端 round-trip、csum v3 校验、IncludeJournal 默认开/关）、`Ext4ExternalXattrTests`（`i_file_acl` 高低位解析、外部 xattr 块读取、未知哈希回退线性扫描）、`ErofsFragmentTests`（Fragments/Dedup 读回一致、共享块）、`ErofsMultiDeviceTests`（设备表槽位校验、fsck.erofs）、`ErofsMetaboxTests`（metabox 自回环 + flat 回归 + WSL 交叉验证；metabox 特性 0x100 被 fsck.erofs 1.8.6 文档化拒绝）、`F2fsCompressionReadTests`/`F2fsCompressionWriteTests`（压缩簇读写回环、LZ4/ZSTD、LZO NotSupported）、`F2fsDoubleIndirectTests`（>single-indirect 上限大文件流式读回 SHA-256 对比、稀疏洞+尾数据）、`BuilderStreamingTests`（byte[]/Path/Stream 三 API 逐字节一致、非 seekable 流、确定性 UUID）。
- **WSL 交叉验证**（`WslCrossValidationTests`）：构建 EXT4/EROFS/F2FS 镜像后经 WSL 工具实测——`e2fsck -fn`（exit 0）、`debugfs -R 'rdump /'`（内容一致）、`fsck.erofs`/`dump.erofs`、`fsck.f2fs`/`dump.f2fs`；按工具粒度可用性探测 + `Assert.Skip`（WSL 不可用或工具缺失时跳过，不静默 return）。当前 WSL（Debian trixie）为 WSL1 内核：免密 sudo 可用但 loop 设备 setup 失败、f2fs/erofs 内核模块缺失，故**挂载级验证不可用**，仅工具级验证生效。

## 维护规则

当以下任何一项发生变更时，**必须在同一次改动中同步更新本文件**：

- 新增 / 移除项目或项目引用关系。
- 构建命令、目标框架、AOT/裁剪配置变化。
- 插件注册流程、节点定义基类、消息约定变化。
- 本地化模块、主题资源键、样式层变化。
- 本文件记录的任何事实性信息。
