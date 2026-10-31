# UotanStudio.Desktop 项目指令

在本目录内改进程入口、平台启动或发布配置前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本目录只放进程入口与平台启动：`Program.cs`、`app.manifest`、`Assets/*.ico` 与发布配置。界面、业务逻辑、服务与命令处理都在 `UotanStudio`，不要在这里复制。
- csproj 只引用 `UotanStudio` 与 `UotanStudio.Mcp`；需要固件、交付或设备能力时加到宿主，不要绕过宿主直接引用。
- 保持 `OutputType=WinExe`、`PublishAot=true`、`TrimMode=partial`、`IsAotCompatible`；不要引入反射式注册或动态加载，界面工厂走宿主的 `ViewModelViewRegistry`。
- `app.manifest` 必须保留；文件关联的扩展名/ProgID 表在宿主 `FileAssociationService`，本目录只提供图标。新增关联要同时改宿主表并补 `Assets/<name>.ico`。
- `.csproj.user`、`*.pubxml.user` 是机器本地文件，不要编辑或提交。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。

## 启动行为

- `Main` 的分发顺序固定：USB 驱动提权助手（`--usb-driver-takeover`/`--usb-driver-release`/`--usb-driver-package-install`/`--usb-driver-package-uninstall`）→ `--managed-device-shell` → `--mcp-headless` → `--mcp` 伴随 → GUI。managed shell 失败写 stderr 并返回 1，其余模式返回 0。
- 提权助手由宿主 `UsbDriverConfigService`/`DriverPackageService` 以 `runas` 重启同一个可执行文件时进入，参数由 `UotanStudio.Services.Devices.UsbDriverElevatedCommand` 解析：在 GUI 初始化之前执行设备接管、解除接管或厂商驱动包安装/移除，把结果写入 `--usb-driver-result` 指定的临时文件，成功返回 0、其余返回 1；不要在 GUI 侧复制这套参数解析或安装逻辑。
- `--managed-device-shell` 只匹配恰好三个参数 `["--managed-device-shell", "Adb|Hdc", serial]`，不要插入额外开关；该入口只服务命令行用户，`WorkspacePanelService.CreateDeviceShellProfile` 不再启动本程序，而是返回不带可执行文件的标签配置，设备 shell 终端由宿主进程内的 `DeviceShellConnection` 复用自己的 ADB/HDC 会话。各启动阶段经标准输出报告，失败仍写标准错误并返回 1。
- `--mcp` 伴随在 GUI 启动前 `Task.Run`，设备经 `DesktopDeviceManagerGateway`、签名经 `DeliveryKeyProvider`、审计经 `McpToolCallLog`；不要在 Desktop 复制工具实现。`--mcp-root`/`--allow-flash` 由 `UotanStudio.Mcp` 的 `McpServerConfig.Parse` 读取。
- `--open-screen-mirror <serial>` 写入 `App.StartupScreenMirrorSerial`（`Array.IndexOf`，区分大小写且必须有后继参数）；文件参数只取 `File.Exists` 的路径，由宿主决定打开解决方案还是导入。不要从 `args` 删除参数，也不要在 `Program` 里做项目打开。
- `BuildAvaloniaApp` 保留 `[STAThread]`、`UsePlatformDetect`、默认字体 `fonts:UotanStudio/MiSans#MiSans` 与 Dock 合并范围 `ActiveDocument`；默认字体指向宿主在 `App.Initialize` 注册的 zstd 字体集合（`UotanStudio/Fonts/ZstdEmbeddedFontCollection`），这些会影响宿主资源路径与停靠行为。
- Linux 在 `UsePlatformDetect` 之后调用 `UseWaylandWithFallback`（`Avalonia.Wayland` 包），有可用 compositor 时走原生 Wayland，否则回退 X11；该调用只有 `OperatingSystem.IsLinux()` 分支可达，NativeAOT 按 RID 折叠该判断并剔除其它 RID 的 Wayland/`NWayland` 程序集。
- 持久化的 `renderingBackend`/`renderingCompositionMode` 在启动时转换成各平台的 `*PlatformOptions`；平台不接受的值回退到自动，设置页只负责持久化并提示重启。
- `Logger.Sink` 只过滤 `LogArea.Binding` 的 Warning；不要依赖它掩盖新增绑定/裁剪警告。GUI 路径没有控制台，诊断写 `LogService`，不要写 `Console.WriteLine`。

## 发布与验证

- 发布以 `.github/workflows/release.yml` 为准：六个 RID（`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`），`dotnet publish ... -c Release -r <rid> --self-contained`。`PublishAot` 由 csproj 声明，不要再用命令行 `-p:PublishAot=true` 传入：那会把属性变成全局属性传给所有被引用项目。Release 默认 `DebugType=none`（不产 PDB），与 CI 一致。`Properties/PublishProfiles/FolderProfile.pubxml` 是本机 win-x64/单文件配置，不代表 CI。
- 发布经 csproj 顶部的 `PublishWithSatoriGC` SDK 用 Satori GC 替换默认 GC：仅自包含发布生效（CI 六个 RID 均自包含，NativeAOT 支持）；SDK 版本跟随上游开发更新，升级时直接改 csproj 中的版本号。运行时可经 `GC.GetConfigurationVariables()` 的 `SatoriGC` 键检测；设置页的「垃圾回收」区块展示当前进程的 GC 实现与配置。
- `WindowsCompositionRoots.xml` 只在 win RID 发布时作为 `TrimmerRootDescriptor` 注入，保留 `Avalonia.Win32.WindowImpl.CompositionEffectsSurface` 供设置页报告实际合成宿主；不要改成无条件引用，否则 Linux/macOS 发布会保留 Win32 平台实现。
- `UotanStudio.Tests` 不引用 Desktop，`Program.cs` 与命令行没有自动化测试。改动后必须 Release 构建 + 对应 RID 发布，并手工冒烟 GUI、`--managed-device-shell`、`--mcp-headless`、`--mcp` 四种模式；不能写成“已测试”。
- 文件关联只能人工验证：Windows 首次启动后检查 `HKCU\Software\Classes\.uotan` 指向 `UotanStudio.uotan`，图标来自输出目录 `Assets/`。

```text
dotnet restore UotanStudio.slnx -p:Configuration=Debug
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
```