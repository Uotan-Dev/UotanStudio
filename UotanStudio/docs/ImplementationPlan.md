# UotanStudio 固件 IDE — 阶段 A/B/C 实施派发规格（子代理专用）

> 本文档是 3 个子代理并行实施的唯一规格来源。每个代理只允许修改其"文件所有权"列出的文件，
> 其余文件一律只读。所有契约（消息/视图/服务签名）必须以本文档为准，不得自行改名或改签名。

## 0. 全局约束（所有任务必须遵守）

1. **分层边界**：`UotanStudio.Firmware`、`UotanStudio.Comm`、`UotanStudio/Services/` 三个既有层保持稳定，
   不得重构或移动既有类型；新逻辑只放 `UotanStudio/Core/` 与 `UotanStudio/Areas/`（及新测试工程）。
2. **包版本（CPM）**：版本号只写 `Directory.Packages.props`（主代理已加 Avalonia.AvaloniaEdit / Semi.Avalonia.AvaloniaEdit）；
   `.csproj` 只写 `<PackageReference Include="..." />`（不带 Version）。
3. **构建红线**：`TreatWarningsAsErrors=true`，代码必须零警告；.editorconfig 规则多为 `:error`，
   代码必须通过 `dotnet format --verify-no-changes`。不允许 `#pragma warning disable`、`NoWarn` 或压制。
4. **C# 风格**：文件作用域命名空间；CommunityToolkit.Mvvm source generators（`[ObservableProperty]`/`[RelayCommand]`）；
   public 成员中英双语 XML `<summary>`（`<para>中文。</para>English.`）；私有字段 `_camelCase`。
5. **XAML**：每个 View 必须写 `x:DataType`（编译绑定）；主题资源用 `{DynamicResource ...}`；
   图标用 `Material.Icons.Avalonia` 的 `MaterialIcon`。
6. **文案**：所有用户可见文本走 Locale。各代理在代码中按前缀引用 key（A=`Project_`/`Explorer_`/`Properties_`/`Editor_`，
   B=`Flash_`，C=`Node_`/`Flow_`），**禁止创建或修改 Locale 文件**——具体文案由主代理在集成阶段统一写入
   `Locale/en-US/` 与 `Locale/zh-CN/`（多代理并发写同一文件有冲突风险）。
7. **消息**：进程内消息一律走 `WeakReferenceMessenger.Default`，使用主代理提供的消息类（见 §1），不得重复定义。
8. **不要运行 dotnet build/test/format**（由主代理统一集成验证）；按既有代码模式精确仿写，保证一次通过。

## 1. 主代理已提供的接缝（只使用，禁止改动）

| 项 | 位置 | 说明 |
|----|------|------|
| `OpenDocumentMessage` | `Messages/OpenDocumentMessage.cs` | `record(string Path)`，请求打开文档 |
| `SelectionChangedMessage` | `Messages/SelectionChangedMessage.cs` | `record(object? SelectedItem)`，选中对象广播 |
| `UnpackRequestMessage` | `Messages/UnpackRequestMessage.cs` | `record(string ComponentPath, string OutputDirectory)` |
| `FlashProgressMessage` | `Messages/FlashProgressMessage.cs` | `record(string? Partition, int Percent, string Status)` |
| `DocumentKindDetector` | `Core/Editor/DocumentKindDetector.cs` | `static EditorDocumentKind Detect(string path)`，按扩展名分类 |
| `EditorDocumentKind` | `Core/Editor/DocumentKindDetector.cs` | `enum { Text, Hex, Structured }` |
| AvaloniaEdit 包 | `Directory.Packages.props` + `UotanStudio.csproj` | `Avalonia.AvaloniaEdit 12.0.0`、`Semi.Avalonia.AvaloniaEdit 12.0.0` 已就绪 |
| ViewLocator 映射 | `UotanStudio/ViewLocator.cs` | 已注册 `PropertiesViewModel → PropertiesView`（阶段 A 必须实现该类型） |
| Workspace 布线 | `Areas/DockControl/WorkspaceDockFactory.cs` | 已布 `Properties` 工具（→ `PropertiesViewModel`）与 `Device` 工具（→ `DeviceViewModel.Instance`） |

## 2. 契约（必须精确实现，集成依赖）

### 2.1 阶段 A 契约
- `Areas/Properties/PropertiesViewModel.cs`：namespace `UotanStudio.Areas.Properties`；
  `public partial class PropertiesViewModel : ViewModelBase`；无参构造；
  属性：`ObservableCollection<PropertyItem> Items`、`string? Title`；
  订阅 `SelectionChangedMessage`：按 `SelectedItem` 类型填充 Items（键值对）。
- `Areas/Properties/PropertyItem.cs`：同文件或独立文件，`sealed record`/class `PropertyItem(string Key, string Value)`。
- `Areas/Properties/PropertiesView.axaml(.cs)`：`UserControl`，`x:DataType="vm:PropertiesViewModel"`，ItemsControl 展示键值。
- `Areas/Editor/Text/TextEditorHost.axaml(.cs)`：`UserControl`，类 `TextEditorHost`；
  依赖属性 `Text`（string，默认 TwoWay 绑定）；内部用 Avalonia.AvaloniaEdit 的 `TextEditor`（包已就绪）。

### 2.2 阶段 B 契约
- `Core/Runtime/IStudioRuntime.cs`：接口，成员与现有 `StudioRuntime` 公开成员对齐并追加
  `DeviceMonitorService Devices`；`StudioRuntime` 改为实现该接口（`public sealed class StudioRuntime : IStudioRuntime`）。
- `DeviceViewModel` 增加静态属性 `public static DeviceViewModel? Instance { get; private set; }`，
  构造函数内赋值（仿 `ProjectExplorerViewModel.Instance` 模式）；`MainViewModel.DeviceVM` 改用 `DeviceViewModel.Instance!`。

### 2.3 阶段 C 契约
- `Areas/Editor/Hex/HexDocumentView.axaml(.cs)`：`UserControl`，类 `HexDocumentView`；依赖属性 `Data`（`byte[]?`），
  只读 hex 渲染（偏移/hex/ASCII 三列），大文件只显示前 64KB。
- `Areas/Editor/Structured/StructuredDocumentView.axaml(.cs)`：`UserControl`，类 `StructuredDocumentView`；
  依赖属性 `Data`（`string?`），JSON/XML 只读展示（格式化文本或树）。

## 3. 阶段 A — D1 固件解析可视化（代理 A）

### A1 结构部件树
- `Core/Inspector/FirmwareInspector.cs`（新建）：对固件部件文件生成结构树。
  - `Task<InspectorNode?> InspectAsync(string path)`：按格式分发——
    payload → 分区列表（经 `FirmwareService.ExtractPayloadAsync` 前用 `PayloadParser.GetPayloadMetadataAsync` 列分区）；
    cpio/tar → 条目（`FirmwareService.ListCpioEntriesAsync`）；Oppo(OFP/OPS/OZIP) → 条目（`FirmwareService.ReadOppoMetadataAsync`）；
    boot 镜像 → 头部摘要（`UotanStudio.Firmware.BootImage` 解析器）；无法识别 → null。
  - `InspectorNode` 模型：`Name` / `Children` / `Detail`（与文件同目录或 `Core/Inspector/` 内）。
  - 全部在后台 `Task.Run` 执行，异常捕获后返回失败摘要而非抛出。
- `Areas/ProjectExplorer/ProjectExplorerViewModel.cs`：文件树节点支持懒加载结构子节点——
  `ProjectTreeItem` 增加"可展开"标识与子节点缓存（`Dictionary<string, InspectorNode?>`）；
  文件被展开/点击时调用 `FirmwareInspector.InspectAsync` 填充子节点（payload 分区、cpio 条目等）。

### A2 真 Properties 面板
- 按 §2.1 契约实现 `Areas/Properties/`。
- `ProjectExplorerViewModel` 选中树项变化时发送 `SelectionChangedMessage`（携带选中 `ProjectTreeItem`）。

### A3 右键菜单与联动
- `Areas/ProjectExplorer/ProjectExplorerView.axaml`：TreeView 节点 `ContextMenu`：
  「打开」「解包到工程目录」「添加到工程部件」「重命名」。
  - 打开 → 发 `OpenDocumentMessage`；解包 → 发 `UnpackRequestMessage`（或直接调
    `FirmwareService.UnpackCpioAsync`/`ExtractPayloadAsync`，带 `IsBusy` + 日志）；重命名 → `File.Move` 后重建树。
- `Areas/Editor/EditorViewModel.cs`：订阅 `OpenDocumentMessage`，收到后调用现有 `OpenFileAsync(Path)`。

### A5 AvaloniaEdit 文本编辑
- 按 §2.1 实现 `TextEditorHost`；`Areas/Editor/EditorView.axaml` 文档区把现有 `TextBox`
  替换为 `TextEditorHost`（绑定 `SelectedDocument.Text`），保留标签页/保存逻辑不变。

### A6 测试工程
- 新建 `UotanStudio.Core.Tests/UotanStudio.Core.Tests.csproj`：xUnit（CPM 已有 xunit/Test.Sdk 版本），
  `ProjectReference → UotanStudio`；在 `UotanStudio.slnx` 中注册（唯一允许修改 slnx 的代理）。
- `FirmwareInspector` 单元测试：构造最小 payload/cpio 样本或复用 Firmware 测试工程已有样本，验证分区/条目提取。

### 代理 A 文件所有权（只允许写这些）
```
UotanStudio/Areas/ProjectExplorer/**
UotanStudio/Areas/Properties/**            （新建）
UotanStudio/Core/Inspector/**              （新建）
UotanStudio/Areas/Editor/EditorView.axaml
UotanStudio/Areas/Editor/EditorView.axaml.cs
UotanStudio/Areas/Editor/EditorViewModel.cs
UotanStudio/Areas/Editor/Text/**           （新建）
UotanStudio/UotanStudio.Core.Tests/**      （新建）
UotanStudio.slnx                           （仅注册 Core.Tests）
```
**禁止**：`Areas/Editor/Hex/**`、`Areas/Editor/Structured/**`（阶段 C）、`Locale/**`；`Core/Runtime`、`Core/Projects`、
`Core/Flash`、`Core/Flow`、`Core/Verification`、`Messages/`、`Directory.Packages.props`、
`Areas/DockControl/`、`ViewLocator.cs`、`Areas/Device/`、`Areas/Analyzer/`、`Areas/NodeEditor/`。

## 4. 阶段 B — N1 设备烧录闭环（代理 B）

### B1 IStudioRuntime 接口化
- 按 §2.2 契约：新建 `Core/Runtime/IStudioRuntime.cs`，现有 `StudioRuntime` 实现它并追加
  `DeviceMonitorService Devices => DeviceMonitorService.Instance;`。
- **消除副作用**：`StudioRuntime.CurrentProject` 改为内部缓存（打开/保存工程时刷新），
  不得在每次访问时调用 `FirmwareProjectService.LoadProject`（该方法内含 `RecordRecent` 写盘副作用）。
  可给 `FirmwareProjectService` 增加 `OpenPath` 变更事件或在 `SaveProject/LoadProject` 成功处更新缓存；
  若需微调 `Core/Projects/FirmwareProjectService.cs` 也属于本代理所有权。
- `Areas/Analyzer/AnalyzerViewModel.cs`：`_runtime` 字段类型改为 `IStudioRuntime`；顺带把扫描时
  硬编码的后缀清单改为复用 `DocumentKindDetector` 或保持现状均可（不改也可）。

### B2 设备面板嵌入工作区
- 按 §2.2 契约：`DeviceViewModel` 增加 `Instance` 静态属性（ctor 内赋值）；
  `ViewModels/MainViewModel.cs` 的 `DeviceVM` 改为 `DeviceViewModel.Instance!`（避免双实例双订阅）。
- 主代理已在 `WorkspaceDockFactory` 布线 `ContextLocator["Device"] = () => DeviceViewModel.Instance`；
  本代理无需改 Dock 工厂。

### B3 FlashService 真实刷写（含真实写分区，按用户决策不做预检/确认防线）
- `Core/Flash/FlashService.cs`（新建）：
  - `Task<FlashResult> FlashImageAsync(DeviceInfo device, string partition, string imagePath, IProgress<FlashProgress>? progress, CancellationToken ct)`
  - `FlashResult`：`Success` / `Summary`；`FlashProgress`：`Partition` / `Percent` / `Status`（与 `FlashProgressMessage` 对齐）。
  - Fastboot 路径：用 `UotanStudio.Comm.Protocols.Fastboot.FastbootSession`（`FastbootFactory` 获取会话），
    执行 `flash <partition> <image>`；EDL 路径：参考 `UotanStudio.Comm.Protocols.Edl` 的
    `FirehoseProtocol` / `FlashBundleOrchestrator` 能力（Comm 已有 EDL 会话与 FlashBundle 编排，按需选用）。
  - 进度：每步/每块报告 `IProgress<FlashProgress>`，并发送 `FlashProgressMessage`（Output 面板 Flash 通道自动展示）。
  - 错误：捕获异常返回 `FlashResult { Success=false, Summary=ex.Message }`，并写 `LogService`（LogLevel.Error）。
  - **禁止直接操作 USB 细节**：一律经由 Comm 协议层。
- `Areas/Device/DeviceView.axaml`：增加最小刷写 UI——分区名输入框 + 镜像路径输入框 + 「刷写」按钮
  （绑定 `DeviceViewModel` 新增的 `FlashPartition` / `FlashImagePath` 属性与 `FlashCommand`）；
  刷写期间 `IsBusy` 禁用按钮，进度经 `FlashProgressMessage` 展示在 Output 面板。

### 代理 B 文件所有权（只允许写这些）
```
UotanStudio/Core/Runtime/**                 （IStudioRuntime.cs 新建 + StudioRuntime.cs 修改）
UotanStudio/Core/Projects/FirmwareProjectService.cs   （仅副作用修复需要时）
UotanStudio/Core/Flash/**                   （新建）
UotanStudio/Areas/Device/**                 （DeviceViewModel.cs / DeviceView.axaml(.cs)）
UotanStudio/Areas/Analyzer/**               （AnalyzerViewModel.cs 类型更新）
UotanStudio/ViewModels/MainViewModel.cs
```
**禁止**：`Areas/ProjectExplorer/**`、`Areas/Properties/**`、`Areas/Editor/**`、`Areas/NodeEditor/**`、`Locale/**`、
`Core/Inspector/**`、`Core/Flow/**`、`Core/Verification/**`、`Messages/`、`Directory.Packages.props`、
`UotanStudio.slnx`、`Areas/DockControl/`、`ViewLocator.cs`。

## 5. 阶段 C — N2 流程复用 + L1 验证闭环（代理 C）

### C1 .uotflow 流程持久化
- `Core/Flow/FlowModel.cs`（新建）：可序列化 DTO——
  `FlowDocumentDto`（`Nodes` / `Connections`）、`FlowNodeDto`（`Id` / `Title` / `NodeCategory` / `X` / `Y` / 输入值）、
  `FlowConnectionDto`（`SourceNodeId` / `SourcePort` / `TargetNodeId` / `TargetPort`）。
- `Core/Flow/FlowSerializer.cs`（新建）：JSON 序列化/反序列化，**必须 AOT 兼容**——
  用 `[JsonSerializable]` source-generated context（仿 `FirmwareProjectJsonContext` 模式，CamelCase）。
- `Areas/NodeEditor/NodeEditorViewModel.cs`：新增 `SaveFlowCommand` / `LoadFlowCommand`：
  保存到当前工程目录（`*.uotflow`，路径经 `FirmwareProjectService.OpenPath` 推导），
  加载时重建节点（按 `NodeCategory` + 标题映射回工厂方法）与连接；
  未打开工程时提示（Locale key）。`Areas/NodeEditor/NodeEditorView.axaml` 工具栏加「保存/加载」按钮。

### C2 节点本地化 + 失败暂停
- 节点标题本地化：`FirmwareNodeViewModel` 等节点工厂方法（`CreateUnpackNode` 等）与工具箱条目标题
  改用 `LanguageService.Instance.GetString("Node_...")`（新增 `Node_` 前缀 key，zh-CN/en-US 双语）；
  执行分派仍按标题字符串匹配（标题是本地化后的，执行分派需改为按 `NodeCategory`+固定键，避免本地化破坏逻辑）。
- 失败暂停：`NodeEditorViewModel` 现有执行命令（ExecuteCommand 区域）改造——执行失败节点时
  停止后续节点执行，错误写入执行日志；`FlowControl` 节点支持按输入值选择输出分支（条件执行最小实现）。

### C3 hex / 结构化视图
- 按 §2.3 契约：`Areas/Editor/Hex/HexDocumentView`（只读 hex，偏移/hex/ASCII 三列，大文件限前 64KB）；
  `Areas/Editor/Structured/StructuredDocumentView`（JSON/XML 只读展示）。
- 集成路由由主代理完成（EditorViewModel 按 `DocumentKindDetector` 分发）；本代理只交付两个控件与依赖属性。

### C4 验证服务（构建/验证闭环的服务层）
- `Core/Verification/HashService.cs`（新建）：`Task<string> Sha256Async(string path, CancellationToken)`（BCL SHA256）。
- `Core/Verification/VerificationService.cs`（新建）：`VerifyAvbAsync`（包装 `FirmwareService.VerifyAvbAsync`）
  + `VerifyIntegrityAsync`（对部件列表做 SHA-256 哈希并返回清单）。
- UI 接入复用节点编辑器已有的 Verify Signature 节点；本阶段只交付服务层 + 测试。

### C5 固件差异对比
- `Core/Verification/FirmwareDiffService.cs`（新建）：
  `Task<FirmwareDiffResult> CompareDirectoriesAsync(string baseline, string modified, CancellationToken)`，
  输出 `新增/修改/删除` 列表（按文件名 + SHA-256 + 大小），供后续 UI 使用。

### 代理 C 测试（可选，放到 `UotanStudio.Core.Tests`）
- 若 `UotanStudio.Core.Tests` 已由代理 A 创建则追加用例（HashService / FlowSerializer 序列化往返）；
  若不确定工程状态，跳过测试、由主代理集成后补齐。

### 代理 C 文件所有权（只允许写这些）
```
UotanStudio/Core/Flow/**                  （新建）
UotanStudio/Core/Verification/**          （新建）
UotanStudio/Areas/NodeEditor/**           （NodeEditorViewModel.cs / NodeEditorView.axaml(.cs) / ViewModels/**）
UotanStudio/Areas/Editor/Hex/**           （新建）
UotanStudio/Areas/Editor/Structured/**    （新建）
```
**禁止**：`Areas/ProjectExplorer/**`、`Areas/Properties/**`、`Areas/Editor/EditorView*`、`Areas/Editor/EditorViewModel.cs`、
`Areas/Editor/Text/**`、`Areas/Device/**`、`Areas/Analyzer/**`、`Core/Inspector/**`、`Core/Runtime/**`、
`Core/Projects/**`、`Core/Flash/**`、`Locale/**`、`Messages/`、`Directory.Packages.props`、`UotanStudio.slnx`、
`Areas/DockControl/`、`ViewLocator.cs`、`ViewModels/MainViewModel.cs`。

## 6. 验收与验证流程（主代理执行）

1. **每阶段完成后**：`dotnet format UotanStudio.slnx --no-restore --verify-no-changes`
   （检查代码是否符合 .editorconfig；失败则修正后复跑）。
2. `dotnet build UotanStudio.slnx`：必须零警告（TreatWarningsAsErrors）。
3. `dotnet test`：全部通过。
4. 阶段 A 完成后：`dotnet publish UotanStudio.Desktop -c Release -r win-x64 --self-contained`
   真实 AOT 发布验证（AvaloniaEdit 兼容性），并运行启动确认。
5. 设备相关（B3/C4）：本地可复原测试机实测（用户已确认无安全防线要求）。

## 7. 交付顺序

- 三个代理并行执行；集成时按 A → B → C 依次合并各自文件所有权，无交叉文件冲突。
- 冲突/编译失败由主代理在集成阶段修复，代理只负责其所有权内文件的正确性。

