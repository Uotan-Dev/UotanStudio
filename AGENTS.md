# UotanStudio 项目指令

## 构建与运行

> 本项目面向 .NET 10.0，使用 Central Package Management，所有包版本集中在 `Directory.Packages.props`，请勿在各 `.csproj` 中重复指定版本号。

```bash
# 还原 + 编译整个解决方案（无需 IDE）
dotnet restore
dotnet build UotanStudio.slnx

# 运行测试（xunit.v3 + .NET 10 MTP 测试通道；须显式 -c Debug，不兼容 VSTest 传统参数）
dotnet test UotanStudio.slnx -c Debug

# 启动应用（Desktop 为启动项目）
dotnet run --project UotanStudio.Desktop

# AOT 发布（依赖 IsAotCompatible / PublishAot）
dotnet publish UotanStudio.Desktop -c Release -r win-x64
```

## 解决方案结构

> 入口点唯一：`UotanStudio.Desktop/Program.cs` → 构造 `AppBuilder.Configure<App>()`。

| 项目 | 职责 |
|---|---|
| `UotanStudio.Comm` | 设备通信库：USB 多后端（WinUSB/LibUsb/Linux/MacOS/HarmonyOS）+ ADB / Fastboot / EDL / RKDL 协议会话 + TCP/UDP/串口/mDNS 传输。**零 Avalonia 依赖**，供应用层引用。 |
| `UotanStudio.Firmware` | 固件解析库：boot image、Payload、Sparse、CPIO、AVB、压缩、DAT（Android v4 transfer list 写入）、Oppo/NTPi/Nb0/Pac 等格式处理。**零 Avalonia 依赖**。 |
| `UotanStudio` | 应用层：Avalonia 资源/主题（`avares://UotanStudio/...`）、ViewModel（继承 `ViewModelBase` → `ObservableObject`）、服务（单例：`LanguageService`、`LogService`、`SettingsPersistenceService`、`DeviceMonitorService`、`ProjectContextService`、`OperationHistoryService`；静态：`ProjectPersistenceService`）、消息（`WeakReferenceMessenger` 派生消息类在 `Messages/`）。 |
| `UotanStudio.Desktop` | 启动项目：`WinExe`、`PublishAot=true`、`TrimMode=partial`。 |

## 关键开发约定

### 1. AOT 兼容性（强制）
`UotanStudio.csproj` 与 `Desktop` 项目启用了 `IsAotCompatible` / `PublishAot`。这意味着：
- **禁止 `Assembly.LoadFrom`、`Activator.CreateInstance(Type)` 等反射加载**。设备协议会话、USB 后端等均通过编译期类型直接构造（`UotanStudio.Comm` 提供工厂/会话类，无运行时加载）。
- JSON 序列化使用**源生成**：`AppSettingsJsonContext : JsonSerializerContext`，配合 `AppSettingsJsonContext.Default.AppSettings`。新增需序列化的类型必须用 `[JsonSerializable]` 注册到该上下文。
- `ViewLocator` 内部仍用反射（已标注 `[RequiresUnreferencedCode]`），**不要在其他地方复制此模式**；非 `ViewModelBase` 的命名空间（如 `UotanStudio.ViewModels.Devices.DeviceViewModel`）需在 `App.axaml` 显式注册 `DataTemplate`。

**Debug 诊断工具（仅 DEBUG 启用，`#if DEBUG` 隔离 + 包引用按 `$(Configuration)` 条件剔除，不进入 Release/AOT 发布）：**
- `AvaloniaMcp.Diagnostics`（`UotanStudio.Desktop/Program.cs` 末尾 `.UseMcpDiagnostics()`）：启动命名管道 MCP 诊断服务器。
- `Dock.Avalonia.Diagnostics`（`MainWindow` 打开时 `AttachDockDebugOverlay()`）：按 F9 显示 Dock 布局覆盖诊断。

### 2. 节点编辑器（NodifyM）
1. 节点定义与注册：所有节点视图模型继承 `FlowNodeViewModel`（`UotanStudio.ViewModels.NodeViewModels`），工厂方法 `XxxNodeViewModel.CreateXxxNode()` 在初始化器中设置稳定 `TypeKey`（如 `"Firmware.Unpack"`、`"Data.ReadFile"`）与各连接器的 `PortType`。`NodeTypeRegistry`（`NodeViewModels/NodeTypeRegistry.cs`）以 TypeKey 注册工厂；`NodeEditorViewModel.InitializeToolbox` 通过 `NodeToolboxItem(name, () => CreateXxxNode())` 声明工具箱，分类为 `Firmware` / `Flash` / `Data` / `Flow` / `Output`。
2. 端口类型约束：`NodePortType` 枚举（`Any`/`Flow`/`Path`/`Data`/`Text`/`Number`/`Boolean`）由 `NodeConnectorViewModel.PortType` 承载，兼容性判定在 `NodeDataUtil.CanConnect`（`Any` 通配、其余须相等），`NodeEditorViewModel.CreateConnection` 拒绝不兼容连接；端口类型徽章显示在节点图标连接器上。
3. 选择与属性面板：`NodeEditorView` 订阅 `NodifyEditor`（`SelectingItemsControl`）的 `SelectionChanged` 同步到 `SelectNode()`，驱动右侧属性面板与一键删除（删除 `IsSelected` 节点）。
4. 工作流执行由 `NodeExecutionEngine`（拓扑排序 + 异步调度）驱动，状态通过事件回传画布。
5. 工程加载：`LoadFromAsync` 按 `NodeDto.TypeKey` 经 `NodeTypeRegistry` 重建具体节点；未注册 TypeKey 时创建 `IsMissing=true` 占位节点并记录警告日志（本地化键 `Node_MissingNode`）。
6. 项目插件系统已移除：不再有 `IPlugin` / `PluginRegistry` / `UotanStudio.Abstractions` / `UotanStudio.Plugins.BuiltIn`。

### 3. 本地化（`Locale/`）
- 资源字典 `Locale/{culture}/{Module}.axaml` 作为 `AvaloniaResource` 嵌入，运行时由 `LanguageService` 动态切换。
- 模块：`Common`、`Dock`、`Menu`、`Node`、`Settings`、`Device`。
- 新增键必须**同时在 `en-US` 和 `zh-CN` 中补齐**；查找键通过 `LanguageService.Instance.GetString("Key")`。
- 添加新语言：创建 `Locale/{culture}/` 并在 `LanguageService` 注册。

### 4. 消息（`CommunityToolkit.Mvvm.Messaging`）
跨组件通信统一用 `WeakReferenceMessenger.Default`。消息类定义在 `UotanStudio/Messages/`，命名约定 `XxxMessage` / `XxxChangedMessage`。发送用 `WeakReferenceMessenger.Default.Send(msg)`，接收方在 `App` 等处理类用 `.register<MessageType>(this, handler)`。

### 5. ViewModel 与 View 命名约定
`ViewModel` 类名 → 同名 `View`（替换 `ViewModel` 为 `View`），`ViewLocator` 用此约定按名称解析。ViewModel 统一继承 `ViewModelBase : ObservableObject`，属性用 `[ObservableProperty]` 自动生成。

### 6. 主题与样式
- 主题资源：`Themes/LightColors.axaml` / `DarkColors.axaml`（`ResourceDictionary`，含 `PageBackground`、`AccentColor` 等画刷）。
- 样式层：Semi.Avalonia + Ursa + Material.Icons + NodifyM，在 `App.axaml` `Application.Styles` 合并。切换主题调用 `LanguageService.Instance.ApplyTheme("DarkColors")`。

### 7. 日志
统一走 `LogService.Instance.Write(LogLevel, "Source", "Message")`，UI 日志窗口通过 `LogEnqueued` 事件或 `Subscribe` 增量消费。`LogLevel` / `IUnifiedLog` 定义在 `UotanStudio/Services/Logging/IUnifiedLog.cs`（命名空间 `UotanStudio.Services`），`LogService` 实现 `IUnifiedLog`；`UotanStudio.Comm` 的 USB 诊断通过 `UsbLogBridge` 桥接到统一日志。

### 8. 工作区面板与工程文件
工作区（`WorkspaceView` / `WorkspaceDockFactory`，`Pages/DockControl/`）承载 Dock 布局：
- **面板构成**：左侧（项目资源管理器 / 打包设置 / 设备）、中部文档区（`DocumentDock`，Id=`DocumentsPane`）、右侧（镜像管理 / 属性）；底部 `BottomPane` 保留为**空槽位**（无面板）。输出 / ADB 日志面板已移除，工作区操作日志统一走全局 `LogService`。
- **文档编辑器**：`FileView` 使用 `Pages/DockControl/CodeEditor`（继承 AvaloniaEdit `TextEditor`，TextMate 语法高亮，LightPlus/DarkPlus 随主题切换）；`FileViewModel` 承载 `Text` / `Language` / `FilePath` / `IsDirty`，`FileView` 双向绑定；语言按扩展名由 `SyntaxLanguageRegistry.GetLanguageId` 解析（`.prop/.rc→ini`、`.xml→xml`、`.json→json` 等）。`WorkspaceViewModel.OpenDocumentAsync(path)` 打开/聚焦文档，`App` 注册 `OpenDocumentRequestMessage` 转发。
- **镜像面板**：`ImageViewModel`（`ViewModels/ImageViewModel.cs`）为单例（`EnsureInstance()` / `Instance`），维护 `Images`（相对工程根目录的 `ImageEntryViewModel`）与 6 个命令（提取 / 合成 img / 合成 br / 删除 / 合成 super / 合成 dat），操作经 `FirmwareService`（`SynthesizeSuperAsync` 等）与 `UotanStudio.Firmware.Dat.DatWriter` 落到 `intermediate/`；文件菜单含「导入镜像」「导出镜像」。
- **工程文件（`.uotan`）**：`ProjectPersistenceService`（静态，`ProjectJsonContext` 源生成 JSON）读写 `ProjectSnapshot`（FormatVersion=2：工程名 / 镜像清单 `ImageEntryDto`（相对路径）/ 打包设置 `ProjectSettingsDto` / 节点画布 `NodeDto`+`ConnectionDto`）。新建 / 打开 / 保存由 `MenuViewModel` 编排。
- **工程目录约定**：`ProjectContextService`（单例）承载当前工程：`RootDirectory` = 工程文件同名目录，`images/` 存导入镜像，`intermediate/<操作>/` 存中间产物（解包、合成、deleted）。镜像条目一律存相对路径。
- **操作历史**：`OperationHistoryService`（单例）维护内存撤销/重做栈（容量 50），工作区操作通过 `Record(description, undo, redo)` 登记；`MenuViewModel` 的 Undo/Redo 优先执行历史，未命中再转发 `NodeEditorActionMessage`。

### 9. 设备管理器
设备监视与协议交互**统一基于 `UotanStudio.Comm`**，由 `DeviceMonitorService`（`UotanStudio/Services/DeviceMonitorService.cs`，单例）驱动：

**核心组件：**
- `DeviceMonitorService`：启动时执行全量 USB 扫描并监听热插拔（Windows 原生 WM_DEVICECHANGE，其他平台轮询快照）与 adb server 设备变更；结果缓存至下一次变更，提供 `Scan()` / `ForceRefreshAsync()` / `FindDeviceBySerial()` / `OpenUsbSession()` / `OpenAdbSession()` / `OpenFastbootSession()` / `OpenEdlSession()` / `OpenRkdlSession()` / `RebootAsync()`。与 Google 官方 adb server（127.0.0.1:5037）交互使用 `AdvancedSharpAdbClient`（探测结果缓存 3 秒，server 未运行时静默降级为仅 USB 源）。
- `DetectedDevice` / `DeviceProtocolKind`（Adb/Fastboot/Edl/Rkdl）/ `DeviceTransportKind`（Usb/Tcp/Serial）：枚举结果模型，稳定键为 USB `DeviceKey` 或 `"tcp:host:port"`。
- `DeviceViewModel`（`UotanStudio/ViewModels/Devices/`）为全局单例（`EnsureInstance()` / `Instance`），主窗口底部 Device 选项卡与 Workspace 停靠面板共享同一实例，避免双实例双订阅；`DeviceInfo` 包装枚举设备并承载拉取到的属性（ADB getprop / Fastboot getvar）。
- 刷写：`UotanStudio/Core/Flash/FlashService` 统一封装 Fastboot 与 EDL（`Firehose`）两条路径，进度经 `FlashProgressMessage` 广播。
- 固件：`UotanStudio/Services/FirmwareService` 基于 `UotanStudio.Firmware` 封装 CPIO 解包/打包、Payload 提取、AVB 校验、Oppo 全格式读取等。
- USB 诊断：`UsbLogBridge`（`IUsbLogger`）把 `UotanStudio.Comm` 的 `UsbTrace` 输出桥接到统一日志，`UsbLogBridge.Install()` 安装。

**关键约定：**
- USB 设备按 VID:PID 分类协议（Qualcomm 9008 / MTK 0E8D / Rockchip 2207 / Allwinner 1F3A 等），与工具无关的设备（摄像头/键鼠）变更不触发重扫。
- adb server 未运行时，USB 设备序列号回退为 VID:PID；ADB 条目按序列号去重（多后端重复枚举场景）。
- `DeviceViewModel` 只能在 `App.axaml` 的 DataTemplate 中显式绑定 `DeviceView`（`ViewLocator` 反射无法命中 `ViewModels.Devices` 命名空间）。

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
