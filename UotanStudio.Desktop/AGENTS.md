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
- 提权助手由宿主 `UsbDriverConfigService` 以 `runas` 重启同一个可执行文件时进入，参数由 `UotanStudio.Services.Devices.UsbDriverElevatedCommand` 解析：在 GUI 初始化之前执行设备接管、解除接管或厂商驱动包安装/移除，把结果写入 `--usb-driver-result` 指定的临时文件，成功返回 0、其余返回 1；不要在 GUI 侧复制这套参数解析或安装逻辑。
- `--managed-device-shell` 只匹配恰好三个参数 `["--managed-device-shell", "Adb|Hdc", serial]`，不要插入额外开关；该入口只服务命令行用户，`WorkspacePanelService.CreateDeviceShellProfile` 返回不带可执行文件的标签配置，设备 shell 终端由宿主进程内的 `DeviceShellConnection` 复用自己的 ADB/HDC 会话。各启动阶段经标准输出报告，失败仍写标准错误并返回 1。
- `--mcp` 伴随在 GUI 启动前 `Task.Run`，设备经 `DesktopDeviceManagerGateway`、签名经 `DeliveryKeyProvider`、审计经 `McpToolCallLog`；不要在 Desktop 复制工具实现。`--mcp-root`/`--allow-flash` 由 `UotanStudio.Mcp` 的 `McpServerConfig.Parse` 读取。
- `--open-screen-mirror <serial>` 写入 `App.StartupScreenMirrorSerial`（`Array.IndexOf`，区分大小写且必须有后继参数）；文件参数只取 `File.Exists` 的路径，由宿主决定打开解决方案还是导入。不要从 `args` 删除参数，也不要在 `Program` 里做项目打开。
- `BuildAvaloniaApp` 保留 `[STAThread]`、`UsePlatformDetect`、默认字体 `fonts:UotanStudio/MiSans#MiSans` 与 Dock 合并范围 `ActiveDocument`；默认字体指向宿主在 `App.Initialize` 注册的 zstd 字体集合（`UotanStudio/Fonts/ZstdEmbeddedFontCollection`），这些会影响宿主资源路径与停靠行为。
- Linux 在 `UsePlatformDetect` 之后调用 `UseWaylandWithFallback`（`Avalonia.Wayland` 包），有可用 compositor 时走原生 Wayland，否则回退 X11；该调用只有 `OperatingSystem.IsLinux()` 分支可达，NativeAOT 按 RID 折叠该判断并剔除其它 RID 的 Wayland/`NWayland` 程序集。
- 持久化的 `renderingBackend`/`renderingCompositionMode` 在启动时转换成各平台的 `*PlatformOptions`；平台不接受的值回退到自动，设置页只负责持久化并提示重启。
- `Logger.Sink` 只过滤 `LogArea.Binding` 的 Warning；不要依赖它掩盖新增绑定/裁剪警告。GUI 路径没有控制台，诊断写 `LogService`，不要写 `Console.WriteLine`。

## 发布与验证

- 发布以 `.github/workflows/release.yml` 为准：六个 RID（`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`），`dotnet publish ... -c Release -r <rid> --self-contained`。`PublishAot` 由 csproj 声明，不要再用命令行 `-p:PublishAot=true` 传入：那会把属性变成全局属性传给所有被引用项目。Release 默认 `DebugType=none`（不产 PDB），与 CI 一致。`Properties/PublishProfiles/FolderProfile.pubxml` 是本机 win-x64/单文件配置，不代表 CI。
- `workflow_dispatch` 的 `rid` 输入可指定单个 RID 或逗号分隔的多个 RID，留空与 tag 推送都全量发布六个 RID；`prepare` job 校验 RID、去重并生成 `include` 矩阵交给 `build`，非法 RID 直接失败。
- `linux-riscv64` 与 `linux-loongarch64` 是实验 RID，只接受规范 RID、不设别名（csproj 的 `UseSatoriGC=false` 回退靠 `riscv64`/`loongarch64` 子串命中）：仅在 `rid` 输入显式指定时进入矩阵，留空全量与 tag 推送都不跑；矩阵条目带 `experimental` 标记，`build` job 对它们允许失败并打印警告。
- 实验 RID 的发布走 QEMU：`docker/setup-qemu-action` 注册 binfmt，在 `--platform linux/riscv64` / `linux/loong64` 的目标架构容器里用社区 SDK 执行 `dotnet publish`，产物回传 `$RUNNER_TEMP/publish` 复用现有打包步骤。社区 SDK 不钉定版本：每次运行经 GitHub releases API 从 `filipnavara/dotnet-riscv` / `loongson/dotnet` 解析版本前缀匹配的最新 `dotnet-sdk-*-linux-<arch>.tar.gz`（排除 musl 与 `Private.SourceBuilt.Artifacts`），并用该 release 自带的 `sha256` digest 校验下载完整性（同源校验，不是独立信任锚）。默认前缀由 `Directory.Build.props` 的 TFM 主版本推出（`net10.0` → `10.0`，目前两边解析到 10.0.114），`workflow_dispatch` 的 `sdk_version` 输入可覆盖为任意版本前缀（如 `11.0.100-rc.3`）以试验更新的 SDK。按主版本过滤的原因：两个仓库当前的「最新」release 都是 .NET 11 RC3，且 `loongson/dotnet` 全部 release 都标为 prerelease、`v8.0.133` 的发布时间反而晚于 11.0 RC3。官方 SDK 在这两个 RID 上还原即报 `NU1101`（NuGet 无对应 runtime pack / NativeAOT 包），因此不能复用 `actions/setup-dotnet` 装的 SDK；`loongson/dotnet` 的 release 说明还要求额外配置 `https://lnuget.loongnix.cn/v3/index.json` 才能取到 `linux-loongarch64` 的 nupkg：容器脚本仅在 `RID=linux-loongarch64` 时用 `-p:RestoreAdditionalProjectSources` 追加该源（不改 `NuGet.Config`，其他 RID 完全不启用），并用 `curl` 预检 feed 可达性，不可达只告警、还原仍可能报 `NU1301`；该源是第三方 feed，只依赖 HTTPS 与 CPM 钉定版本，没有独立校验。实验 job 只在临时源码副本里去掉 `global.json` 的 `sdk` 节以容纳与仓库钉定版本不同的社区 SDK；`UseSatoriGC=false` 由 csproj 条件自动生效。QEMU 全链路未在 GitHub Actions 实测，容器镜像 tag、`PublishWithSatoriGC` SDK 与社区 SDK 的兼容性、模拟下的构建时长都未验证。
- 发布经 csproj 顶部的 `PublishWithSatoriGC` SDK 用 Satori GC 替换默认 GC：仅自包含发布生效（CI 六个 RID 均自包含，NativeAOT 支持）；SDK 版本跟随上游开发更新，升级时直接改 csproj 中的版本号。运行时可经 `GC.GetConfigurationVariables()` 的 `SatoriGC` 键检测；设置页的「垃圾回收」区块展示当前进程的 GC 实现与配置。`riscv64`/`loongarch64` RID 在 csproj 中显式 `UseSatoriGC=false` 回落默认 GC（Satori 无对应运行时包）。csproj 还把 `NETCoreSdkVersion` 烘焙为 `AssemblyMetadata` 的 `DotNetSdkVersion`，设置页据此展示「.NET SDK 版本」，元数据缺失时回退运行时描述。
- `WindowsCompositionRoots.xml` 只在 win RID 发布时作为 `TrimmerRootDescriptor` 注入，保留 `Avalonia.Win32.WindowImpl.CompositionEffectsSurface` 供设置页报告实际合成宿主；不要改成无条件引用，否则 Linux/macOS 发布会保留 Win32 平台实现。
- `UotanStudio.Tests` 不引用 Desktop，`Program.cs` 与命令行没有自动化测试。改动后必须 Release 构建 + 对应 RID 发布，并手工冒烟 GUI、`--managed-device-shell`、`--mcp-headless`、`--mcp` 四种模式；不能写成“已测试”。
- 文件关联只能人工验证：Windows 首次启动后检查 `HKCU\Software\Classes\.uotan` 指向 `UotanStudio.uotan`，图标来自输出目录 `Assets/`。

```text
dotnet restore UotanStudio.slnx -p:Configuration=Debug
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
```