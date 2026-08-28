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

# AOT 发布（依赖 IsAotCompatible / PublishAot；当前已临时关闭以支持 Avalonia 布局预览器，恢复方式见 §1）
dotnet publish UotanStudio.Desktop -c Release -r win-x64
```

## 解决方案结构

> 入口点唯一：`UotanStudio.Desktop/Program.cs` → 构造 `AppBuilder.Configure<App>()`。

| 项目 | 职责 |
|---|---|
| `UotanStudio.Comm` | 设备通信库：USB 多后端（WinUSB/LibUsb/Linux/MacOS/HarmonyOS）+ ADB / Fastboot / EDL / RKDL 协议会话 + TCP/UDP/串口/mDNS 传输。**零 Avalonia 依赖**，供应用层引用。 |
| `UotanStudio.Firmware` | 固件解析库：boot image、Payload、Sparse、CPIO、AVB、压缩、DAT（Android v4 transfer list 写入，支持 `.new.dat.br` Brotli 输入与分段输出）、Oppo/NTPi/Nb0/Pac 等格式处理；**EXT4/EROFS**（`Ext4/`、`Erofs/` 目录，全托管文件系统镜像解析/构建/压缩，`Ext4FormatHandler`/`ErofsFormatHandler` 经 `FirmwareFormatRegistry` 注册）；**DEX**（`DEX/` 目录，Dalvik 解析/反汇编/重建）；**AXML**（`AXml/` 目录，Android 二进制 XML 解析/重建）；**ARSC**（`Arsc/` 目录，resources.arsc 资源表解析）。**零 Avalonia 依赖**。 |
| `UotanStudio` | 应用层：Avalonia 资源/主题（`avares://UotanStudio/...`）、ViewModel（继承 `ViewModelBase` → `ObservableObject`，按特性分区于 `ViewModels/{Shell,Workspace,Editors,Devices,Nodes}/`）、视图（`Views/{Shell,Workspace,Editors,Devices,Nodes,Controls}/`，与 ViewModel 同构）、服务（按域分区于 `Services/{Devices,Firmware,Flash,Projects}/` 与根：`LanguageService`、`LogService`、`SettingsPersistenceService` 等）、消息（`WeakReferenceMessenger` 派生消息类在 `Messages/`）。 |
| `UotanStudio.Desktop` | 启动项目：`WinExe`、`PublishAot=true`、`TrimMode=partial`（当前临时关闭，见 §1）。 |

## 关键开发约定

### 1. AOT 兼容性（强制；当前临时关闭）
`UotanStudio.csproj` 与 `Desktop` 项目启用了 `IsAotCompatible` / `PublishAot`。**为支持 Avalonia 布局预览器，当前已在两个 csproj 中临时置为 `false`（`UotanStudio.csproj` 的 `IsAotCompatible`、`Desktop.csproj` 的 `PublishAot` / `IsAotCompatible` / `TrimMode`，注释中保留了恢复写法）；发布 AOT 前须恢复。** 以下约束在恢复后依然适用：
- **禁止 `Assembly.LoadFrom`、`Activator.CreateInstance(Type)` 等反射加载**。设备协议会话、USB 后端等均通过编译期类型直接构造（`UotanStudio.Comm` 提供工厂/会话类，无运行时加载）。
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
1. 节点定义与注册：所有节点视图模型继承 `FlowNodeViewModel`（`UotanStudio.ViewModels.NodeViewModels`），工厂方法 `XxxNodeViewModel.CreateXxxNode()` 在初始化器中设置稳定 `TypeKey`（如 `"Firmware.Unpack"`、`"Data.ReadFile"`）与各连接器的 `PortType`。`NodeTypeRegistry`（`NodeViewModels/NodeTypeRegistry.cs`）以 TypeKey 注册工厂；**节点工厂在 `NodeEditorViewModel.InitializeToolbox` 中集中注册**（既有节点直接 `NodeTypeRegistry.Register(key, () => CreateXxxNode())`，DEX 节点经 `DexNodeViewModel.RegisterNodes()`），同时以 `NodeToolboxItem(name, () => CreateXxxNode())` 声明工具箱，分类为 `Firmware` / `Flash` / `Data` / `Flow` / `Output` / `Dex`。**各节点 `ExecuteAsync` 一律按 `TypeKey` 分发（`switch (TypeKey)`），不得按显示标题分发。**
2. 端口类型约束：`NodePortType` 枚举（`Any`/`Flow`/`Path`/`Data`/`Text`/`Number`/`Boolean`）由 `NodeConnectorViewModel.PortType` 承载，兼容性判定在 `NodeDataUtil.CanConnect`（`Any` 通配、其余须相等），`NodeEditorViewModel.CreateConnection` 拒绝不兼容连接；端口类型徽章显示在节点图标连接器上。
3. 选择与属性面板：`NodeEditorView` 订阅 `NodifyEditor`（`SelectingItemsControl`）的 `SelectionChanged` 同步到 `SelectNode()`，驱动右侧属性面板与一键删除（删除 `IsSelected` 节点）。
4. 工作流执行由 `NodeExecutionEngine`（拓扑排序 + 异步调度）驱动，状态通过事件回传画布。
5. 工程加载：`LoadFromAsync` 按 `NodeDto.TypeKey` 经 `NodeTypeRegistry` 重建具体节点；未注册 TypeKey 时跳过该节点并记录警告日志（软件未发布，无旧工程占位兼容需求）。
6. 项目插件系统已移除：不再有 `IPlugin` / `PluginRegistry` / `UotanStudio.Abstractions` / `UotanStudio.Plugins.BuiltIn`。

### 3. 本地化（`Locale/`）
- 资源字典 `Locale/{culture}/{Module}.axaml` 作为 `AvaloniaResource` 嵌入，运行时由 `LanguageService` 动态切换。
- 模块：`Common`、`Dock`、`Menu`、`Node`、`Settings`、`Device`。
- 新增键必须**同时在 `en-US` 和 `zh-CN` 中补齐**；查找键通过 `LanguageService.Instance.GetString("Key")`。
- 添加新语言：创建 `Locale/{culture}/` 并在 `LanguageService` 注册。

### 4. 消息（`CommunityToolkit.Mvvm.Messaging`）
跨组件通信统一用 `WeakReferenceMessenger.Default`。消息类定义在 `UotanStudio/Messages/`，命名约定 `XxxMessage` / `XxxChangedMessage`。发送用 `WeakReferenceMessenger.Default.Send(msg)`，接收方在 `App` 等处理类用 `.register<MessageType>(this, handler)`。

### 5. ViewModel 与 View 命名约定
ViewModel 类名 → 同名 `View`（替换 `ViewModel` 为 `View`），且**目录同构**：`ViewModels/{Shell,Workspace,Editors,Devices,Nodes}/` ↔ `Views/{Shell,Workspace,Editors,Devices,Nodes}/`（纯控件与布局工厂在 `Views/Controls/`、`Views/Workspace/WorkspaceDockFactory.cs`、`Views/Nodes/NodeDockFactory.cs`）。解析统一走 `ViewModelViewRegistry`（显式注册表，非反射）。ViewModel 统一继承 `ViewModelBase : ObservableObject`，属性用 `[ObservableProperty]` 自动生成。

### 6. 主题与样式
- 主题资源：`Themes/LightColors.axaml` / `DarkColors.axaml`（`ResourceDictionary`，含 `PageBackground`、`AccentColor` 等画刷）。
- 样式层：Semi.Avalonia + Ursa + Material.Icons + NodifyM，在 `App.axaml` `Application.Styles` 合并。切换主题调用 `LanguageService.Instance.ApplyTheme("DarkColors")`。

### 7. 日志
统一走 `LogService.Instance.Write(LogLevel, "Source", "Message")`，UI 日志窗口通过 `LogEnqueued` 事件或 `Subscribe` 增量消费。`LogLevel` / `IUnifiedLog` 定义在 `UotanStudio/Services/Logging/IUnifiedLog.cs`（命名空间 `UotanStudio.Services`），`LogService` 实现 `IUnifiedLog`；`UotanStudio.Comm` 的 USB 诊断通过 `UsbLogBridge` 桥接到统一日志。

### 8. 工作区面板与工程文件
工作区（`WorkspaceView` / `WorkspaceDockFactory`，`Views/Workspace/`）承载 Dock 布局：
- **面板构成**：左侧（项目资源管理器 / 设备；**打包设置面板默认隐藏**，经「窗口」菜单唤出）、中部文档区（`DocumentDock`，Id=`DocumentsPane`）、右侧（镜像管理 / 属性）；底部 `BottomPane` 保留为**空槽位**（无面板）。输出 / ADB 日志面板已移除，工作区操作日志统一走全局 `LogService`。面板显隐/聚焦由 `WorkspacePanelService`（`Services/Workspace/WorkspacePanelService.cs`，单例）统一管理，`WorkspaceDockFactory.CreateLayout` 用 `Register(key, dockable, host, visible)` 登记各面板，`CloseDockable` 关闭面板后回调 `OnDockContentsChanged` 同步可见状态。
- **顶部菜单「窗口」栏目**：`MainWindow.axaml` 在 视图 与 工具 之间提供「窗口」菜单，各面板项以 CheckBox 形式勾选当前可见性（绑定 `WorkspacePanelService` 的可观察属性），命令由 `MenuViewModel` 的 `ToggleProjectExplorer` / `ToggleProjectSetting` / `ToggleDevicePanel` / `ToggleImagesView` / `ToggleProperties`（`[RelayCommand]`）转发到 `WorkspacePanelService.Toggle(key)`。窗口菜单**不管理**设备管理器 / 日志等独立窗口（仍由「工具」菜单打开）。
- **文档编辑器**：`FileView` 使用 `Views/Controls/CodeEditor`（继承 AvaloniaEdit `TextEditor`，TextMate 语法高亮，LightPlus/DarkPlus 随主题切换）；`FileViewModel`（`ViewModels/Editors/`）承载 `Text` / `Language` / `FilePath` / `IsDirty`，`FileView` 双向绑定；语言按扩展名由 `SyntaxLanguageRegistry.GetLanguageId` 解析（`.prop/.rc→ini`、`.xml→xml`、`.json→json` 等）。`WorkspaceViewModel.OpenDocumentAsync(path)` 打开/聚焦文档，`App` 注册 `OpenDocumentRequestMessage` 转发。特殊格式走专用编辑器：DTB/DTBO → `DtbEditorViewModel`（左节点树 + 右 DTS 源码）；`.dex` → `DexEditorViewModel`（只读反汇编，经 `FirmwareFormatRegistry` 注册的 `UotanStudio.Firmware.Dex.DexFormatHandler` 解析，`DexService` 提供 Parse/Disassemble/Rebuild 服务）；AXML（`AndroidManifest.xml` 等二进制 XML）→ `ManifestEditorViewModel`（左元素结构树 + 右属性编辑与 XML 预览，经 `UotanStudio.Firmware.AXml.AXmlFormatHandler` 解析，保存时以 `AXmlWriter` 重建）；resources.arsc → `ArscEditorViewModel`（左包→类型→配置→条目资源树 + 右条目详情，经 `UotanStudio.Firmware.Arsc.ArscFormatHandler` 解析，只读浏览）；固件文件（扩展名命中格式处理器注册表，如 `.img/.pac/.ops/.nb0`）→ `FirmwareInfoViewModel`（`ViewModels/Editors/`）+ `FirmwareInfoView`，只读展示格式名/摘要/元数据/条目，数据来自 `FormatDetector.Detect` + `IFormatHandler.ReadMetadataAsync`。打开方式由 `FileOpenMode.ResolveMode`（`EditorOpenMode`：`Auto/Text/Hex/Dtb/Dex/Axml/Arsc/Apk/Firmware`，AXML 按内容魔数优先于 `.xml` 文本路由，ARSC 按内容魔数兜底；**已知固件扩展名集合由 `FormatDetector.RegisteredHandlers` 的 `FileExtensions` 聚合**，`IsFirmwareExtension` 统一驱动固件面板与 Hex 路由，避免双源维护）路由，资源管理器右键「打开方式」菜单（含「在固件信息中打开」）与双击（Auto 模式）复用同一消息。
- **镜像面板**：`ImageViewModel`（`ViewModels/Workspace/ImageViewModel.cs`）为单例（`EnsureInstance()` / `Instance`），维护 `Images`（相对工程根目录的 `ImageEntryViewModel`）与 7 个命令（提取 / 合成 img / 合成 br / 删除 / 合成 super / 合成 dat / 打包镜像），操作经 `FirmwareService`（`Services/Firmware/FirmwareService.cs`，`SynthesizeSuperAsync` 等）、`PackingService`（`Services/Firmware/PackingService.cs`，按打包设置面板的镜像类型/格式/压缩选项调 `Ext4Builder`/`ErofsBuilder` 打包系统镜像）与 `UotanStudio.Firmware.Dat.DatWriter` 落到 `intermediate/`；文件菜单含「导入镜像」「导出镜像」。`ImageView`（`Views/Workspace/ImageView.axaml(.cs)`）为镜像条目提供**右键菜单**（提取/合成 img/br/super/dat/打包镜像/删除，Click 处理器先 `SelectedImage = 菜单条目` 再执行命令）。
- **工程文件（VS 式布局）**：`SolutionPersistenceService`（`Services/Projects/`，静态，`SolutionJsonContext` 源生成 JSON）读写三类文件——解决方案 `<名称>.uotan`（`SolutionSnapshot`：解决方案名 + 引用的固件/节点项目相对路径）、固件项目 `<名称>.uotanfw`（`FirmwareProjectSnapshot`：镜像清单 `ImageEntryDto`（相对路径）+ 打包设置 `ProjectSettingsDto`）、节点项目 `<名称>.uotannode`（`NodeProjectSnapshot`：节点画布 `NodeDto`+`ConnectionDto`）。新建 / 打开 / 保存由 `MenuViewModel` 编排；`MenuViewModel.OpenProjectFromPathAsync(path)` / `ImportFilesAsync(paths)` 供菜单与命令行共用。旧单文件 `.uotan`（`ProjectSnapshot`）已废弃。
- **工程目录约定**：`ProjectContextService`（`Services/Projects/`，单例）承载当前解决方案：`RootDirectory` = 解决方案文件同名目录，`images/` 存导入镜像，`intermediate/<操作>/` 存中间产物（解包、合成、deleted），`.uotan/` 存会话状态（`settings.json` + `history.json`，类似 VS 的 `.vs/`）。镜像条目一律存相对路径。
- **操作历史**：`OperationHistoryService`（`Services/Projects/`，单例）维护内存撤销/重做栈（容量 50），工作区操作通过 `Record(description, [HistoryOperationDto,] undo, redo)` 登记；带 `HistoryOperationDto` 的条目可经 `SaveAsync`/`LoadAsync` 持久化到 `.uotan/history.json` 并跨会话重建（重建工厂由 `ImageViewModel` 注册到 `ReconstructOperation`）。`MenuViewModel` 的 Undo/Redo 优先执行历史，未命中再转发 `NodeEditorActionMessage`。
- **窗口标题**：`MainViewModel.WindowTitle`（`"<解决方案名> - UotanStudio"`）绑定 `MainWindow.Title` 与顶部标题栏 `TextBlock`，随 `ProjectContextChangedMessage` 更新。

### 9. 设备管理器
设备监视与协议交互**统一基于 `UotanStudio.Comm`**，由 `DeviceMonitorService`（`UotanStudio/Services/Devices/DeviceMonitorService.cs`，单例）驱动：

**核心组件：**
- `DeviceMonitorService`：启动时执行全量 USB 扫描并监听热插拔（Windows 原生 WM_DEVICECHANGE，其他平台轮询快照）与 adb server 设备变更；结果缓存至下一次变更，提供 `Scan()` / `ForceRefreshAsync()` / `FindDeviceBySerial()` / `OpenUsbSession()` / `OpenAdbSession()` / `OpenFastbootSession()` / `OpenEdlSession()` / `OpenRkdlSession()` / `RebootAsync()`。与 Google 官方 adb server（127.0.0.1:5037）交互使用 `AdvancedSharpAdbClient`（探测结果缓存 3 秒，server 未运行时静默降级为仅 USB 源）。
- `DetectedDevice` / `DeviceProtocolKind`（Adb/Fastboot/Edl/Rkdl）/ `DeviceTransportKind`（Usb/Tcp/Serial）：枚举结果模型，稳定键为 USB `DeviceKey` 或 `"tcp:host:port"`。
- `DeviceViewModel`（`UotanStudio/ViewModels/Devices/`）为全局单例（`EnsureInstance()` / `Instance`），主窗口底部 Device 选项卡与 Workspace 停靠面板共享同一实例，避免双实例双订阅；`DeviceInfo` 包装枚举设备并承载拉取到的属性（ADB getprop / Fastboot getvar）。除基础的刷新/连接/断开/重启（system/bootloader/recovery/edl）外，还提供**快速固件操作**命令：`OpenAdbShellCommand`（仅 ADB，新控制台执行 `adb -s <serial> shell`）、`FastbootReadInfoCommand`（仅 Fastboot，读 getvar 分区信息）、`QuickFlashCommand`（ADB/Fastboot/EDL/RKDL，选镜像经 `FlashService.FlashImageAsync` 刷写，进度回报 `StatusText`，刷写前经 `FlashConfirmationRequested` 请求确认）；`DeviceView` 工具栏按钮与设备条目**右键菜单**（`Views/Devices/DeviceView.axaml(.cs)`，Click 处理器先 `SelectedDevice = 菜单设备` 再执行命令）均绑定这些命令。
- 刷写：`UotanStudio/Services/Flash/FlashService` 统一封装 Fastboot 与 EDL（`Firehose`）两条路径，进度经 `FlashProgressMessage` 广播。
- 固件：`UotanStudio/Services/Firmware/FirmwareService` 基于 `UotanStudio.Firmware` 封装 CPIO 解包/打包、Payload 提取、AVB 校验、Oppo 全格式读取等。
- USB 诊断：`UsbLogBridge`（`UotanStudio/Services/Devices/UsbLogBridge.cs`，`IUsbLogger`）把 `UotanStudio.Comm` 的 `UsbTrace` 输出桥接到统一日志，`UsbLogBridge.Install()` 安装。

**关键约定：**
- USB 设备按 VID:PID 分类协议（Qualcomm 9008 / MTK 0E8D / Rockchip 2207 / Allwinner 1F3A 等），与工具无关的设备（摄像头/键鼠）变更不触发重扫。
- adb server 未运行时，USB 设备序列号回退为 VID:PID；ADB 条目按序列号去重（多后端重复枚举场景）。
- `DeviceViewModel` / `DeviceView` 在 `ViewModels/Devices/` ↔ `Views/Devices/` 分区，经 `ViewModelViewRegistry` 显式注册解析（见 §5）。

**AOT 注意事项：**
- `UotanStudio.Comm` 无反射：USB 后端（WinUSB/LibUsb/Linux/MacOS/HarmonyOS）按平台编译期选择，协议会话直接构造。
- `LibUsbDotNet` 通过 `LibUsbNativeLoader` 托管加载，`Makaretu.Dns.Multicast` 为纯托管 mDNS，`System.IO.Ports` 在 .NET 10 下完全支持 AOT。
- `AdvancedSharpAdbClient` 用于 adb server 交互（网络设备/状态/重启），与 `UotanStudio.Comm` 原生协议并存。

## 维护规则

当以下任何一项发生变更时，**必须在同一次改动中同步更新本文件**：

- 新增 / 移除项目或项目引用关系。
- 构建命令、目标框架、AOT/裁剪配置变化。
- 插件注册流程、节点定义基类、消息约定变化。
- 本地化模块、主题资源键、样式层变化。
- 本文件记录的任何事实性信息。
