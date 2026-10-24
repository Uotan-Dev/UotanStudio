# UotanStudio 宿主项目指令

在本目录内改界面、ViewModel、项目会话或应用服务前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`；交付后端规则见 `UotanStudio.Delivery/AGENTS.md`，测试写法见 `UotanStudio.Tests/AGENTS.md`。

## 边界与组合

- 本目录只做 Avalonia 宿主；桌面入口、`[STAThread]` 与进程启动在 `UotanStudio.Desktop`，不要在这里新增 `Main`/`Program`。设备读写只经 Comm，宿主不开 USB/套接字/串口。
- 只允许一个桌面 Shell：`ShellComposition` 在已附着工作区时抛 `InvalidOperationException`；`ProjectOperationsService.Instance` 与 `ProjectContextService.Instance` 是全局单例，不要建第二套。
- 新增 ViewModel 必须登记到 `ViewModelViewRegistry`（AOT 安全、无反射）；新增编辑器还要登记 `DocumentOpenRegistry`，它同时是打开派发、会话恢复、清理与“打开方式”菜单的唯一来源。
- 新增 XAML 使用当前 Avalonia API 与编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`），避免已标记 obsolete 的属性，不要退回反射绑定。`IsRidAgnostic=false` 用于保留 Comm 原生资产的 RID，`AllowUnsafeBlocks` 用于 `FileAssociationService` 的源生成 `LibraryImport`，都不要顺手删。
- 跨 VM/View 通知走 `WeakReferenceMessenger` + `Messages/` 记录；App 注册，退出时 `UnregisterAll`。`async void` 只用于消息处理，并经 `RunMainRequestAsync` 记录异常。`AppFeatures.AiAssistantEnabled` 恒为 `false`，不要启用或删除占位。
- 容器入口：`App.Initialize` 用 `ServiceRegistration.Register` + `ServiceRegistration.BuildProvider` 建立 `Microsoft.Extensions.DependencyInjection` 容器，并交给 `ServiceHub` 保存；`App.Services` 暴露该容器，容器未建立时为 null。所有注册必须使用显式工厂，禁止反射式 DI、程序集扫描、`ActivatorUtilities` 与无工厂的 `AddSingleton<T>()`；这是裁剪与 NativeAOT 的注册契约，发布验证仍按根 `AGENTS.md` 的命令执行。
- 静态门面统一“容器优先、测试回退”：`LanguageService.Instance`、`LogService.Instance`、`ProjectOperationsService.Instance`、`SettingsPersistenceService.Instance` 先查 `ServiceHub`，容器未初始化或正在构建该注册时返回惰性默认实例；不启动容器的测试可直接运行，注册工厂不会递归。
- `ShellComposition.CreateAsync` 在 `App.Services` 可用时从容器解析 `MenuViewModel`/`NodeEditorViewModel`/`SettingsViewModel`/`WorkspaceViewModel`，容器不可用时按手工构造装配；`ShellComposition.Create(ProjectOperationsService)` 仍按传入服务显式装配，供测试与基准使用。

## 分层与依赖

- 依赖方向固定为 `Views` → `ViewModels` → 应用服务；`ViewModels` 不得引用 `Views`，`Services`（排除 `Services/Ui`）不得引用 `ViewModels` 或 `Views`。
- `Services/Ui` 仅承载 Avalonia 绑定的 UI 服务（对话框、窗口交互、文档保存/通知、工作区自动保存与文档打开登记），可以引用 `ViewModels` 与 `Views`；业务逻辑不要放进这一层，也不要让非 UI 服务继续新增对它的依赖。
- `UiAbstractions` 只放视图模型可依赖的契约（`EditorOpenMode`、`SyntaxLanguageRegistry`、`IWindowService`、`IWorkspaceDockFactory` 等），不得引用 `Views`；窗口与对话框由 `Views/UiWindowService` 实现，经 `ShellComposition`/App 注入。
- `Views` 触达宿主行为只经 `ViewModels`、`UiAbstractions` 或 `Services.Ui`；直接导入其他应用服务命名空间的例外必须逐条登记在 `UotanStudio.Tests/Architecture/HostLayeringGuardTests.FrozenViewExceptions`，只列出该文件实际允许的命名空间集合，不要放宽整条规则。

## UI、本地化与编辑器

- 本地化资源固定为 `Locale/` 下 7 个 `.resx` 模块；中英 key 集合必须一致，同一语言内 key 必须唯一。改 key 要同步 `Locale/LocaleKeySets.cs`；`LocaleDictionaryTests` 会比对生成索引与 resx。
- `.resx` 只作为 `AdditionalFiles` 并从 SDK 默认 `EmbeddedResource` glob 移除，否则会生成 satellite assembly。UI 可见文本只走资源字典，不能在界面中拼接中英文。
- 已生成脚本文本只在生成时确定语言；切换界面语言不得改写已生成内容。
- 编辑器明确选择“按文本打开”时必须保持文本模式（`EditorOpenMode.Text`），即使文件含 NUL 或使用二进制扩展名；只有 `Auto` 才允许按扩展名或内容探测。
- `Fonts/` 下的 MiSans 字体以 zstd 帧存放，在启动序列的 `SplashPhase.Resources` 阶段由 `ZstdEmbeddedFontCollection` 解码到内存并注册到 `FontManager`；启动窗在此之前使用平台默认字体（`SplashViewModel.SplashFontFamily`），所以不要把字体注册移回 `App.Initialize`，否则启动窗会先于资源解压解析空的 `fonts:` 集合。注册失败只记录错误日志并回退平台字体，不能把用户困在启动窗上。这些 `.zst` 产物由上游 TTF 经资源压缩步骤生成。
- `.dat`、`.new.dat`、`.patch.dat`、`.br`、`.transfer.list` 的识别复用现有格式注册与判定（`ImageInputService`、`ImageOutputService`、`ProjectExplorerView`）；不要在 View 复制后缀判断。

## 项目、快照与交付

- 新建、打开、关闭、另存为必须经过 `ProjectOperationsService`。保存失败保留源上下文与原目标内容；另存为成功后才切换项目根目录。自动保存失败要放行项目操作门，不能静默卡住。
- 解决方案快照中的交付计划与面板可见性只写 `SolutionSnapshot.State`（键 `delivery`/`panels`）；解决方案根目录不存放 `delivery.json`。子项目自己的 `.uotan/` 语义保持不变。
- 交付页面保持现有控件与布局语义；新行为优先复用 `DeliveryViewModel`、`DeliveryCompositionView`、`DeliveryView.Operations.cs`，不要在 View 层维护第二套镜像归属、确认状态或输出集合。`DeliveryPlan` 仍是交付后端唯一输入。
- 设置写入 `%AppData%/UotanStudio/settings.json`，使用临时文件 + 原子替换；新设置项同时加 `AppSettings` 与 `AppSettingsJsonContext`。未知或归档键留在 `AdditionalSettings`，损坏文件回退默认值。
- 渲染后端与合成模式是启动期设置：设置页只写 `renderingBackend`/`renderingCompositionMode` 并提示重启，桌面入口在下一次启动时转换平台选项；"自动"项展示当前进程实际使用的后端与合成宿主（`RenderingRuntimeProbe`，经保留的解析器反射读取），Wayland 下不提供可选项。
- MCP 暴露由 `McpExposureService` 启动：只监听 `127.0.0.1`；沙箱根为空或无法规范化时回退不受限沙箱，并强制关闭刷写门控；签名经 `DeliveryKeyProvider`。
- `KeyLibraryService`（`%AppData%/UotanStudio/keys`）是密钥生成/导入/导出的唯一路径；发布与导出使用不覆盖语义，不得覆盖已有密钥或证书。
- 节点身份与工厂只来自 `NodeFactoryRegistrar`；内置节点数被 `NodeCatalogReductionTests` 冻结为 97，增删节点要同步基线并通过 `NodeSchemaPortCoverageTests`。

## 启动与呈现

- 启动顺序保持：`FirmwareFormatRegistry.RegisterAll()` → 语言初始化 → 启动窗 → 内嵌资源解压（`SplashPhase.Resources`，MiSans 字体）→ 设置/主题/语言 → `McpExposureService.Apply` → 首次 USB 扫描（结果行 `DevicesFound`）→ `ShellComposition.CreateAsync` → 启动文件/投屏 → `Ready`。启动文件失败只记录日志，不阻断主窗口。
- 启动窗文本只由 `SplashViewModel.AdvanceAsync` 的 `SplashPhase` 驱动，阶段只能前进；状态文案写在 `Common.resx`/`Common.zh-CN.resx`，新增阶段要同时改 `LocaleKeySets` 与两份 resx。进度条恒为 `IsIndeterminate="True"`，不要绑定具体进度或恢复“完成到 100%”的第二条进度条。
- `App.Initialize` 先建立容器并调用 `ServiceHub.Initialize`，`OnFrameworkInitializationCompleted` 的既有启动顺序不变；`App.DeviceMonitor` 在容器已注册实例时取容器实例，否则回退组合根持有的默认监视器。
- 投屏的 GPU 呈现只在 `ScreenMirrorGpuPresenter` 真实取得合成 GPU 上下文并准备好呈现路径后启用；失败时关闭 GPU 导出并保留 CPU 位图回退。不要把“取得 surface”写成“已启用 GPU 解码”。
- GUI 路径没有控制台；诊断写 `LogService`，不要写 `Console.WriteLine`。文件关联注册失败只记日志。

## 验证

```text
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.LocaleDictionaryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.ProjectOperationsBoundaryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.NodeCatalogReductionTests --minimum-expected-tests 1
```

- 触摸全局窗口、Dock、XAML 渲染或真实 FolderPicker 的改动没有可靠的自动化覆盖，必须人工验证，不能把编译通过写成界面已验证。