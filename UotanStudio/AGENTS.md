# UotanStudio 宿主项目指令

在本目录内改界面、ViewModel、项目会话或应用服务前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`；工程与交付后端规则见 `UotanStudio.Pipeline/AGENTS.md`，测试写法见 `UotanStudio.Tests/AGENTS.md`。

## 边界与组合

- 本目录只做 Avalonia 宿主；桌面入口、`[STAThread]` 与进程启动在 `UotanStudio.Desktop`，不要在这里新增 `Main`/`Program`。设备读写只经 Comm，宿主不开 USB/套接字/串口。
- 只允许一个桌面 Shell：`ShellComposition` 在已附着工作区时抛 `InvalidOperationException`；`ProjectOperationsService.Instance` 与 `ProjectContextService.Instance` 是全局单例，不要建第二套。
- 新增 ViewModel 必须登记到 `ViewModelViewRegistry`（AOT 安全、无反射）；新增编辑器还要登记 `DocumentOpenRegistry`，它同时是打开派发、会话恢复、清理与“打开方式”菜单的唯一来源。
- 新增 XAML 使用当前 Avalonia API 与编译绑定（`AvaloniaUseCompiledBindingsByDefault=true`），避免已标记 obsolete 的属性，不要退回反射绑定。`IsRidAgnostic=false` 用于保留 Comm 原生资产的 RID，`AllowUnsafeBlocks` 用于 `FileAssociationService` 的源生成 `LibraryImport`，都不要顺手删。
- 跨 VM/View 通知走 `WeakReferenceMessenger` + `Messages/` 记录；App 注册，退出时 `UnregisterAll`。`async void` 只用于消息处理，并经 `RunMainRequestAsync` 记录异常；ViewModel 层 async void 方法若方法体全程自捕获异常并送入统一日志，亦属合法形态。`AppFeatures.AiAssistantEnabled` 恒为 `false`，不要启用或删除占位。
- 容器入口：`App.Initialize` 用 `ServiceRegistration.Register` + `ServiceRegistration.BuildProvider` 建立 `Microsoft.Extensions.DependencyInjection` 容器，并交给 `ServiceHub` 保存；`App.Services` 暴露该容器，容器未建立时为 null。`Register` 是应用级单例的完整目录，新增应用级单例要同时补注册，否则 `ServiceHub` 解析不到它。所有注册必须使用显式工厂，禁止反射式 DI、程序集扫描、`ActivatorUtilities`、无工厂的 `AddSingleton<T>()` 与开放泛型注册：宿主视图模型的内部构造函数与公共无参构造函数并存，类型映射会静默选中无参重载而丢掉已注册依赖；开放泛型在 NativeAOT 下到运行时才失败且没有编译诊断。`BuildProvider` 仅在 Debug 下开 `ValidateOnBuild`，它只校验描述符级映射，工厂注册的依赖完整性由 `ServiceRegistrationTests.Register_EveryRegistration_Resolves` 覆盖，注册形态由 `DependencyInjectionBoundaryGuardTests` 冻结；发布验证仍按根 `AGENTS.md` 的命令执行。
- 静态门面统一“容器优先、测试回退”：`LanguageService.Instance`、`LogService.Instance`、`ProjectOperationsService.Instance`、`SettingsPersistenceService.Instance` 先查 `ServiceHub`，容器未初始化或正在构建该注册时返回惰性默认实例；不启动容器的测试可直接运行。四个门面各自的 `Default` 只返回惰性默认实例、不查容器，`ServiceRegistration` 的注册工厂必须使用 `Default`：容器直接解析该类型时，工厂若调用 `Instance`，门面会经 `ServiceHub` 再次解析同一类型，被 DI 判为循环依赖。
- USB 树的「启用接管」「安装驱动」等驱动操作都以管理员令牌执行：`Services/Devices/UsbDriverConfigService` 在未提权时用 `runas` 重启自身进入 `--usb-driver-takeover`/`--usb-driver-release`/`--usb-driver-package-install`/`--usb-driver-package-uninstall` 助手模式（参数与结果文件格式见 `UsbDriverElevatedCommand`），等待子进程并读回状态；UAC 被取消或宿主无法重启自身时返回 `ElevationRequired`，界面沿用原有提示。
- 厂商驱动包只在 USB 树安装：行内「安装驱动」按钮由 `DriverPackageCatalog.FindMatch` 按设备硬件/兼容 ID 与包内 INF 声明的 ID 相交匹配，且仅在设备尚未绑定驱动时显示；安装走 Comm 的厂商包暂存入口，只对无驱动设备生效，不做危险确认。接管走 Comm 的自签 WinUSB 包（设备级 INF、目录签名、证书写入 `LocalMachine` 的 Root 与 TrustedPublisher），宿主不生成证书也不持有私钥；取消接管按绑定的 provider 校验后卸载包并删除证书。设置页不提供驱动安装界面，也不要把厂商包安装或移除加回设置页。
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
- 烧录页面以**禁用**表达“当前设备不提供该动作”：`FlashControlGate` 只决定启用状态，不隐藏控件，也没有设备可见性条件；切入页面时全部侧边分类收起；切换设备只展开该协议对应的分类，不收起其它分类，未选中设备时保留已持久化的展开状态。新增控件时按同样方式接入门控，不要用 `IsVisible` 表达设备条件。唯一的显隐联动是“禁用 AVB”与“添加 Root”两个二级选项组：它们跟随各自复选框的勾选状态展开或收起（`IsVisible` 绑定同级复选框），因此不登记门控，运行期间的禁用由被禁用的父面板承担。七个选项复选框（禁用 AVB、添加 Root、清空数据、重启、显示所有分区、删除 COW、进迭时空校验）**不携带设备条件**，一律登记为 `BusyOnly`：勾选是用户在选设备之前表达的意图，选项是否生效仍由各烧录路径自己判定，没有路径时由烧录动作报告协议不匹配；只有页面操作运行期间才关闭它们。“发送引导程序”按钮在 EDL 端点上还要等高通分类给出 programmer（下拉框选中的 ELF，或自选 Programmer 且地址栏指向仍存在的文件）：`CanSendBootLoader` 把该条件叠加到 `LoaderOnly` 门控的启用意图上，点击后经 `RunEdlAsync` → `ConfigureEdlAsync` 上传 programmer，使 bootrom 从 Sahara 切到 Firehose，绝不自行打开选择框；设备进入 Firehose 阶段后按钮按 `IsLoaderStageLoadable` 关闭。设备信息读取只发布到通用部分的 `DeviceStatus` 表（`FlashDeviceStatusRowKeys.EdlInfo` 分组、按设备键缓存、按世代号丢弃过期读取）；高通分类不维护独立的信息栏目、标题或列表集合。
- MiSans 字形由 `UotanStudio.Assets` 的 `fonts` 资源包随包（随包可变字形 `MiSansVF.ttf`，压缩帧与清单在 `UotanStudio.Assets/Assets/fonts`），字体帧只由 Assets 的 fonts 包提供；启动序列的 `SplashPhase.Resources` 阶段由 `ZstdEmbeddedFontCollection.DecompressFaces` 经 `AssetStore.Read` 取已校验字节并注册到 `FontManager`，宿主按请求字重设置其 `wght` 轴。启动窗在此之前使用平台默认字体（`SplashViewModel.SplashFontFamily`），字体注册不放在 `App.Initialize`，否则启动窗会先于资源解码解析空的 `fonts:` 集合。注册失败只记录错误日志并回退平台字体，不能把用户困在启动窗上。
- `.dat`、`.new.dat`、`.patch.dat`、`.br`、`.transfer.list` 的识别复用现有格式注册与判定（`ImageInputService`、`ImageOutputService`、`ProjectExplorerView`）；不要在 View 复制后缀判断。

## 项目、快照与交付

- 新建、打开、关闭、另存为必须经过 `ProjectOperationsService`。保存失败保留源上下文与原目标内容；另存为成功后才切换项目根目录。自动保存失败要放行项目操作门，不能静默卡住。
- 解决方案快照中的交付计划、面板可见性与项目资源管理器展开状态只写 `SolutionSnapshot.State`（键 `delivery`/`panels`/`explorer`）；解决方案根目录不存放 `delivery.json`。子项目自己的 `.uotan/` 语义保持不变。
- 交付页面保持现有控件与布局语义；新行为优先复用 `DeliveryViewModel`、`DeliveryCompositionView`、`DeliveryView.Operations.cs`，不要在 View 层维护第二套镜像归属、确认状态或输出集合。`DeliveryPlan` 仍是交付后端唯一输入。
- 可写持久化位置统一经 `AppPaths`（`Services/AppPaths.cs`）解析：根目录在 Windows 为 `%APPDATA%\UotanStudio`、macOS 为 `~/Library/Application Support/UotanStudio`、Linux 为 XDG 数据目录（`~/.local/share/UotanStudio`）；新增持久化目录必须挂在 `AppPaths.Root` 下，不得从本体目录或进程工作目录推导。`AppPaths.MigrateLegacyLocations` 在 `Program.Main` 启动早期调用一次，把遗留的 `%LOCALAPPDATA%` 子目录（`apple-lockdown`、`delivery-tools`）与 macOS 旧 XDG 根收编进统一根；`%TEMP%` 下的一次性产物规则不变，adb/hdc 密钥保留 `~/.android`、`~/.hdc` 生态约定路径。安装目录只读是 MSIX 与 macOS .app 发布的硬约束：`AppContext.BaseDirectory` 只允许只读探测，例外清单冻结在 `UotanStudio.Tests` 的 `InstallDirectoryBoundaryTests`。 HarmonyOS 安装链路：设备页对未签名 `.hap/.hsp` 提供"签名并安装"，编排经 `Services/Harmony/HapSigningService`（材料在 `AppPaths.Root/harmony/hap-signing/<包名>/`）与 `AgcSessionService`（DevEco 风格回环浏览器登录，会话在 `AppPaths.Root/harmony/session.json`，仅存 JWT）；AGC 端点与格式细节见交接文档 `HAP_SIGNING_HANDOFF.md`。
- 设置写入 `%AppData%/UotanStudio/settings.json`，使用临时文件 + 原子替换；新设置项同时加 `AppSettings` 与 `AppSettingsJsonContext`。未知或归档键留在 `AdditionalSettings`，损坏文件回退默认值。主窗口尺寸、位置与状态存为 `mainWindowPlacement`（`MainWindowPlacement`），烧录页面侧边分类（`edl`/`mtk`/`rockchip`/`spacemit`/`sunxi`/`image`）的展开状态存为 `expandedFlashSections`；`SettingsViewModel.SaveSettings` 保存前必须从磁盘最新设置合并这两个键，窗口几何只存 `settings.json` 的 `mainWindowPlacement`。
- 渲染后端与合成模式是启动期设置：设置页只写 `renderingBackend`/`renderingCompositionMode` 并提示重启，桌面入口在下一次启动时转换平台选项；"自动"项展示当前进程实际使用的后端与合成宿主（`RenderingRuntimeProbe`，经保留的解析器反射读取），Wayland 下不提供可选项。
- MCP 暴露由 `McpExposureService` 启动：只监听 `127.0.0.1`；沙箱根为空或无法规范化时回退不受限沙箱，并强制关闭刷写门控；签名经 `DeliveryKeyProvider`。
- `KeyLibraryService`（`%AppData%/UotanStudio/keys`）是密钥生成/导入/导出的唯一路径；发布与导出使用不覆盖语义，不得覆盖已有密钥或证书。
- 节点身份与工厂只来自 `NodeFactoryRegistrar`；内置节点数被 `NodeCatalogReductionTests` 冻结为 97，增删节点要同步基线并通过 `NodeSchemaPortCoverageTests`。

## 启动与呈现

- 启动顺序保持：`FirmwareFormatRegistry.RegisterAll()` → 语言初始化 → 启动窗 → 内嵌资源解压（`SplashPhase.Resources`，MiSans 字体）→ 设置/主题/语言 → `McpExposureService.Apply` → 首次 USB 扫描（结果行 `DevicesFound`）→ `ShellComposition.CreateAsync` → 启动文件/投屏 → `Ready`。启动文件失败只记录日志，不阻断主窗口。
- 启动窗必须尽早显示：`App.Initialize` 不得构造设备监视器（容器登记 `App.DefaultDeviceMonitor` 工厂）；`FirmwareFormatRegistry.RegisterAll()` 与插件能力探测移到设置加载之后执行。`RegisterBundledFonts()` 必须保持在首个窗口之前：全局文本样式引用 MiSans，缺失字体会让启动窗首次布局直接抛异常。冷启动阶段以 `Debug` 级别写入 `[cold-start]` 行（含进程创建后的毫秒数），新增阻塞步骤要同步维护。
- 设备枚举（USB 总线清单与 adb/HDC 可用性探测）不得出现在启动关键路径：启动序列不等待扫描，`DeviceViewModel` 构造后只在后台线程解析并启动监视器、首次扫描完成再回填设备列表；停靠布局中的 `AdbLogViewModel`、`ImageViewModel` 等不得在构造函数里直接引用 `App.DeviceMonitor`，否则会把约 4 秒的监视器构造拉回 UI 线程。
- 启动文件经 `LaunchRequestApplier`（`Services/Ui/Launch`）打开：`.uotan` 按工程打开，`.uotanfw`/`.uotannode` 打开其所属解决方案并切到对应子工程/节点工程，其余文件走 `WorkspaceVM.OpenDocumentAsync` 的文档链路；不得再把这些文件导入镜像清单（未打开工程时导入会被直接丢弃）。`DesktopInstanceRelay` 转发的后续启动复用同一条路径。
- 文件关联由设置页显式应用；启动序列在后台调用 `FileAssociationService.EnsureRegistration`，仅当此前已注册且命令或扩展名映射与当前可执行文件不符时重注册，使搬移或重建后的可执行文件仍能打开关联文件。
- 启动窗文本只由 `SplashViewModel.AdvanceAsync` 的 `SplashPhase` 驱动，阶段只能前进；状态文案写在 `Common.resx`/`Common.zh-CN.resx`，新增阶段要同时改 `LocaleKeySets` 与两份 resx。进度条为确定态：`AdvanceAsync` 依 `SplashPhase` 单调写入 `TargetProgress`（0..1），`MainSplashWindow` 用合成层 `Scale` 补间动画平滑填充，阶段跳变不表现为长度突变；动画跑在合成线程，UI 线程解压资源或预构建主窗口时仍持续推进。不要把它退回 `IsIndeterminate`，也不要恢复第二条“完成到 100%”的百分比进度条。
- `App.Initialize` 先建立容器并调用 `ServiceHub.Initialize`，`OnFrameworkInitializationCompleted` 的既有启动顺序不变；`App.DeviceMonitor` 在容器已注册实例时取容器实例，否则回退组合根持有的默认监视器。
- 投屏的 GPU 呈现只在 `ScreenMirrorGpuPresenter` 真实取得合成 GPU 上下文并准备好呈现路径后启用；失败时关闭 GPU 导出并保留 CPU 位图回退。不要把“取得 surface”写成“已启用 GPU 解码”。
- GUI 路径没有控制台；诊断写 `LogService`，不要写 `Console.WriteLine`。文件关联注册失败只记日志。
- 日志文本的构建统一按级别惰性执行：插值消息直接用 `$"..."` 传给 `IUnifiedLog.Write`，由 `UnifiedLogMessage` 处理器在条目落不到任何目标时跳过格式化；已经提前拼好字符串或需要遍历、走栈、序列化等昂贵前置计算时，先用 `IsEnabled(level)` 守卫。不要用 `IsEnabled` 包住插值调用，也不要用 `string.Format` + 参数数组替代插值重载。
- 条目级元数据只在确有来源时填充：`Scope` 用于一组条目的关联（工作流按阶段名附加），`Fields` 用于结构化键值（MEL 桥接从生产者状态提取），渲染统一走 `LogMetadataFormat`，不要在窗口、导出或文件接收端各自拼格式。

## 验证

```text
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.LocaleDictionaryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.ProjectOperationsBoundaryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.NodeCatalogReductionTests --minimum-expected-tests 1
```

- 触摸全局窗口、Dock、XAML 渲染或真实 FolderPicker 的改动没有可靠的自动化覆盖，必须人工验证，不能把编译通过写成界面已验证。