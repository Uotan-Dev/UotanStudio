# UotanStudio 项目指令

## 构建与运行

> 本项目面向 .NET 10.0，使用 Central Package Management，所有包版本集中在 `Directory.Packages.props`，请勿在各 `.csproj` 中重复指定版本号。

```bash
# 还原 + 编译整个解决方案（无需 IDE）
dotnet restore
dotnet build UotanStudio.slnx

# 启动应用（Desktop 为启动项目）
dotnet run --project UotanStudio.Desktop

# AOT 发布（依赖 IsAotCompatible / PublishAot）
dotnet publish UotanStudio.Desktop -c Release -r win-x64
```

## 解决方案结构

> 入口点唯一：`UotanStudio.Desktop/Program.cs` → 构造 `AppBuilder.Configure<App>()`。

| 项目 | 职责 |
|---|---|
| `UotanStudio.Abstractions` | 契约层：插件接口 `IPlugin`、节点 `INodeDefinition` / `INodeInstance`、日志 `IUnifiedLog`、设备 `IDeviceManager`。**零第三方依赖**，供其他项目引用。 |
| `UotanStudio` | 应用层：Avalonia 资源/主题（`avares://UotanStudio/...`）、ViewModel（继承 `ViewModelBase` → `ObservableObject`）、服务（单例：`LanguageService`、`LogService`、`SettingsPersistenceService`）、消息（`WeakReferenceMessenger` 派生消息类在 `Messages/`）。 |
| `UotanStudio.Plugins.BuiltIn` | 内置插件：`Firmware` / `Flash` / `Data` / `Flow` / `Output`。**编译期注册**，禁止运行时反射加载。 |
| `UotanStudio.Desktop` | 启动项目：`WinExe`、`PublishAot=true`、`TrimMode=partial`。 |

## 关键开发约定

### 1. AOT 兼容性（强制）
`UotanStudio.csproj` 与 `Desktop` 项目启用了 `IsAotCompatible` / `PublishAot`。这意味着：
- **禁止 `Assembly.LoadFrom`、`Activator.CreateInstance(Type)` 等反射加载**。内置插件通过 `PluginRegistry.RegisterBuiltIn(() => new XxxPlugin())` **工厂委托**注册（见 `App.axaml.cs` `RegisterBuiltInPlugins`）。
- JSON 序列化使用**源生成**：`AppSettingsJsonContext : JsonSerializerContext`，配合 `AppSettingsJsonContext.Default.AppSettings`。新增需序列化的类型必须用 `[JsonSerializable]` 注册到该上下文。
- `ViewLocator` 内部仍用反射（已标注 `[RequiresUnreferencedCode]`），**不要在其他地方复制此模式**。

### 2. 插件系统（内置插件）
1. 在 `UotanStudio.Plugins.BuiltIn` 创建 `XxxPlugin : IPlugin`，用 `[Plugin("reverse.dns.id", ...)]` 标注。
2. 在 `InitializeAsync` 中通过 `context.RegisterNodeDefinition(new XxxNodeDefinition())` 注册节点。
3. 节点定义继承 `NodeDefinitionBase`，通过 `AddInput(title, type, allowMultiple)` / `AddOutput(...)` 声明端口；实例继承 `NodeInstanceBase`，重写 `ExecuteAsync`。
4. **最后**：在 `App.axaml.cs` 的 `RegisterBuiltInPlugins` 里加一行 `registry.RegisterBuiltIn(() => new XxxPlugin());`。顺序即加载顺序，先注册者先初始化。

### 3. 本地化（`Locale/`）
- 资源字典 `Locale/{culture}/{Module}.axaml` 作为 `AvaloniaResource` 嵌入，运行时由 `LanguageService` 动态切换。
- 模块：`Common`、`Dock`、`Menu`、`Node`、`Settings`。
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
统一走 `LogService.Instance.Write(LogLevel, "Source", "Message")`，UI 日志窗口通过 `LogEnqueued` 事件或 `Subscribe` 增量消费。`IUnifiedLog` 实例可从 `IPluginContext.Log` 获取。

### 8. 设备管理器
设备管理器采用**多后端统一抽象**架构，位于 `UotanStudio/Services/Devices/`：

**核心组件：**
- `DeviceManager`：统一入口，实现 `IDeviceManager` 接口。首次启动执行 `FullScanAsync()`（全量扫描），后续通过 `IncrementalScanAsync(protocol)`（增量扫描）。
- `DeviceCache`：单例，持久化设备固定硬件配置（`%AppData%/UotanStudio/device_cache.json`），避免重复查询。使用源生成 JSON（`DeviceCacheJsonContext`）。
- 枚举器：
  - `UsbDeviceEnumerator`：基于 `FirmwareKit.Comm` 原生 USB 枚举，按 VID:PID 分类协议（Qualcomm 9008、MTK 0E8D、Rockchip 2207、Allwinner 1F3A 等）。
  - `SerialPortEnumerator`：基于 `System.IO.Ports.SerialPort.GetPortNames()`，AOT 友好（无 WMI）。
  - `MdnsEnumerator`：基于 `Zeroconf` 包，扫描 `_adb._tcp.local.` 等协议。
  - `NetworkDeviceEnumerator`：TCP/UDP 手动连接，支持协议探测和手动指定覆盖。
  - `AdbServerClient`：通过 TCP 5037 端口与 ADB server 通信（原生协议，无外部进程）。

**协议处理器（`IProtocolHandler`）：**
每种协议（ADB/Fastboot/EDL/MTK/Rockchip）实现 `IProtocolHandler`，由 `DeviceManager.RegisterHandler` 注册。协议处理器通过外部工具进程（`ProcessRunner`）或原生协议通信。

**设备协议（`DeviceProtocol`）：**
包含 `Adb`、`Fastboot`、`Fastbootd`、`Recovery`、`EdlQcom`、`MtkCom`、`RkBootrom`、`Fex`（Allwinner）、`Tcp`、`Udp`、`Mdns`、`Unknown`。新增协议需同时更新枚举器的 `ClassifyDevice` 逻辑和 `DeviceProtocolExtensions` 显示名称。

**设备信息（`DeviceInfo`）：**
统一抽象，包含 `Serial`、`Protocol`、`State`、`Capabilities` 等。`Capabilities` 记录支持的操作（刷写/擦除/重启/ADB Shell 等），UI 据此动态调整按钮。

**关键约定：**
- USB 枚举得到的设备使用 `VID:PID:SerialNumber` 作为唯一键。
- 网络设备使用 `tcp:IP:Port` 或 `udp:IP:Port` 作为唯一键。
- 串口设备使用 `serial:COMx` 作为唯一键。
- mDNS 设备使用 `mdns:IP` 作为唯一键。
- 后端错误通过 `IDeviceManager.BackendError` 事件报告，UI 订阅并显示人机友好提示。

**AOT 注意事项：**
- 禁止使用反射加载协议处理器。
- `FirmwareKit.Comm` 使用源生成枚举，AOT 兼容。
- `Zeroconf` 使用纯托管 mDNS 实现（`Makaretu.Dns.Multicast`），AOT 兼容。
- `System.IO.Ports` 在 .NET 10 下完全支持 AOT。

## 维护规则

当以下任何一项发生变更时，**必须在同一次改动中同步更新本文件**：

- 新增 / 移除项目或项目引用关系。
- 构建命令、目标框架、AOT/裁剪配置变化。
- 插件注册流程、节点定义基类、消息约定变化。
- 本地化模块、主题资源键、样式层变化。
- 本文件记录的任何事实性信息。
