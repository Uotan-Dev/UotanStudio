# UotanStudio 项目指令

UotanStudio 是面向 Android 固件、卡刷包、线刷工具和设备通信的 .NET 10/C# 14 桌面应用。本文件只保留仓库级约束与项目地图；具体项目的规则放在各目录的 `AGENTS.md`，代理进入目录后按需读取，不要把全部子项目规则一次性堆进上下文。

## 读取顺序

1. 先读本文件，确认目标项目的边界、全局构建/验证命令与 Git 纪律。
2. 再读离目标文件最近的 `AGENTS.md`（通常是所属项目根目录的那一份）。跨项目改动时把涉及项目的文件都读一遍；子项目约束与根文件冲突时，以子项目文件为准。
3. 测试宿主、过滤器与环境门控见 [UotanStudio.Tests/AGENTS.md](UotanStudio.Tests/AGENTS.md)；实测数字、跳过项与未验证边界见 [VERIFICATION.md](VERIFICATION.md)。

## 项目地图

| 项目 | 职责 | 项目指令 |
| --- | --- | --- |
| `UotanStudio.Hash` | 校验和与摘要的唯一公共入口，叶子库 | [AGENTS.md](UotanStudio.Hash/AGENTS.md) |
| `UotanStudio.Net` | HTTP 下载、GitHub 镜像与 Release/版本查询，叶子库 | [AGENTS.md](UotanStudio.Net/AGENTS.md) |
| `UotanStudio.Compress` | 通用压缩、解压与容器编解码 | [AGENTS.md](UotanStudio.Compress/AGENTS.md) |
| `UotanStudio.Assets` | 内嵌二进制资源的统一存储、清单与准备/抽取门面，叶子下层 | [AGENTS.md](UotanStudio.Assets/AGENTS.md) |
| `UotanStudio.Firmware` | 固件格式、文件系统、Payload、启动镜像与签名 | [AGENTS.md](UotanStudio.Firmware/AGENTS.md) |
| `UotanStudio.Firmware.MagicGen` | 构建期 magic(5) 规则表生成器 | [AGENTS.md](UotanStudio.Firmware.MagicGen/AGENTS.md) |
| `UotanStudio.Comm` | ADB/HDC/Fastboot/EDL/MTK/RKDL 与 USB/TCP/UDP/串口 | [AGENTS.md](UotanStudio.Comm/AGENTS.md) |
| `UotanStudio.Mirroring` | scrcpy 投屏协议与会话管理 | [AGENTS.md](UotanStudio.Mirroring/AGENTS.md) |
| `UotanStudio.Media` | 音视频解码、投屏呈现与平台媒体后端 | [AGENTS.md](UotanStudio.Media/AGENTS.md) |
| `UotanStudio.Projects` | 工程/解决方案生命周期与持久化 | [AGENTS.md](UotanStudio.Projects/AGENTS.md) |
| `UotanStudio.Delivery` | 交付计划校验与 Super/Payload/Recovery 输出 | [AGENTS.md](UotanStudio.Delivery/AGENTS.md) |
| `UotanStudio.Mcp` | 内嵌 MCP 服务器与工具目录 | [AGENTS.md](UotanStudio.Mcp/AGENTS.md) |
| `UotanStudio` | Avalonia 宿主：界面、ViewModel、项目会话与应用服务 | [AGENTS.md](UotanStudio/AGENTS.md) |
| `UotanStudio.Desktop` | 桌面入口、平台启动与 NativeAOT 发布 | [AGENTS.md](UotanStudio.Desktop/AGENTS.md) |
| `UotanStudio.Tests` | 唯一可执行测试宿主 | [AGENTS.md](UotanStudio.Tests/AGENTS.md) |
| `UotanStudio.Benchmarks` | BenchmarkDotNet 基准与性能剖析 | [AGENTS.md](UotanStudio.Benchmarks/AGENTS.md) |
| `UotanStudio.FilesystemFixture` | 手动运行的文件系统往返验证宿主 | [AGENTS.md](UotanStudio.FilesystemFixture/AGENTS.md) |
| `UotanStudio.RegexInfer` | 从输入/输出样例确定性推导正则 | [AGENTS.md](UotanStudio.RegexInfer/AGENTS.md) |

新增或删除子项目时，同步更新这张表与对应目录的 `AGENTS.md`；项目专属规则留在子文件，根文件只保留全局规则。

## 全局不变量

- 依赖只向下：`Hash` 与 `Net` 是最底层叶子（`Net` 不引用任何项目）；`Compress` 只引用 `Hash`；`Assets` 只引用 `Compress` 与 `Hash`；`Firmware` 引用 `Assets`、`Compress` 与 `Hash`；`Comm` 引用 `Assets`、`Firmware` 与 `Hash`；`Mirroring` 引用 `Assets` 与 `Comm`；`Media` 不引用 `Comm`；`Projects` 只引用 `Hash` 与 `Compress`；`Delivery` 不引用宿主 UI、项目服务或 `KeyLibraryService`。新增 `ProjectReference` 前先确认方向，项目文件是最终事实。
- `Assets` 不引用 `Net`，也不做网络 I/O；`Firmware`、`Comm`、`Mirroring`、`Delivery` 与宿主可引用 `Assets`。
- 设备读写（USB 句柄、TCP/UDP、串口、adb/HDC server 协议）只能由 `UotanStudio.Comm` 持有；宿主只消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与公开协议客户端。scrcpy 归 `UotanStudio.Mirroring`，不属于 Comm 协议层。
- Apple 相关协议（usbmuxd、lockdownd、AFC、syslog 中继与受信隧道）不接设备扫描：`DeviceControllerFactory` 只注册 USB/adb/HDC 发现源，扫描不探 usbmuxd 守护进程、不生成 usbmuxd 端点，`CommDeviceTransportOpener` 也不识别 usbmuxd 路由。不要为扫描恢复 usbmuxd 探针、端点映射或新增 Apple 发现源。
- `DeliveryPlan` 是交付后端唯一输入契约；宿主负责采集与恢复界面状态，后端负责校验与生成输出，模式互斥只在现有校验路径维护。
- 固件、ZIP、Payload、文件系统镜像和设备返回数据都按不可信输入处理：流式与有界缓冲、偏移/长度/条目/解压大小上限、整数溢出防护、贯穿 `CancellationToken`、临时文件校验后原子替换；修改镜像要分别报告字节、结构、内容与容量等价性。
- 测试夹具、诊断产物和一次性生成物必须创建在主机 `%TEMP%` 下并在使用后删除；不要把大镜像、语料或临时目录留在仓库、盘符根目录或用户目录。生产写入为原子替换而创建的临时文件仍放在目标同目录/同卷，完成或失败后删除。
- 外部工具、WSL、QEMU、真实设备和 GPU 的验证边界必须单独说明；未执行的硬件、原生挂载、商店或性能验证不能写成已通过。

## 代码与文档

- 目标 .NET 10/C# 14；`global.json` 固定 SDK `10.0.401` 并 `rollForward: latestMinor`。遵循根 `.editorconfig`：4 空格缩进、禁止 Tab、file-scoped namespace、使用 `var` 与现有规则要求的模式匹配/集合表达式/对象初始化等现代写法、控制流必须有花括号、`insert_final_newline = false`。公共和内部 API 保持明确可空标注，DTO 必需值使用 `required` 或合理默认值。
- 指针声明与 `&`、`fixed`、`sizeof`、`stackalloc`→指针都必须在 `unsafe` 上下文中书写，生产代码的 `unsafe` 要求保持不变。
- 设置了 `GenerateDocumentationFile` 的公共类型与成员必须有中英双语 XML 注释：`<summary>` 内英文整句在前，中文整句写在同一 `<summary>` 的 `<para>` 中。具体项目清单、`CS1591` 与 `TreatWarningsAsErrors` 的差异写在对应项目文件；公共形态只由代码与 XML 注释声明，不维护 `PublicAPI.*.txt`。
- 生产代码不得用项目级 `NoWarn` 隐藏新的生产警告，也不得用它掩盖可修复的空引用、裁剪或异步问题。裁剪兼容性优先修复真实调用链；避免在生产路径使用 `StackFrame.GetMethod()` 等需要保留反射元数据的 API，确需使用时要明确标注并验证裁剪边界，不能为了安静构建而全局禁用 IL2026。
- 代码注释和仓库文档只描述当前状态，不描述历史、草稿或演进。

### 公开函数/方法注释

- 在设置了 `GenerateDocumentationFile` 的项目中，每个 public/protected 方法、构造函数与公共属性都要有 XML 注释；接口实现可以沿用 `<inheritdoc/>`，但接口声明、公共门面与重载仍需写清差异。
- 顺序固定为 `<summary>` → `<typeparam>`（如有）→ `<param>`（按签名顺序）→ `<returns>`（非 void）→ `<exception>`（只写已证实路径）→ `<example>`（仅非平凡入口）。
- `<summary>` 先写英文整句，中文整句写在同一 `<summary>` 的 `<para>` 中；`<param>`、`<returns>`、`<value>` 也按“英文在前、中文在 `<para>` 中”组织，不另起第二个 `<summary>`。
- 用 `<paramref>`、`<c>`、`<see cref>` 标注标识符与类型；`<see cref>` 必须能单独解析，重载或同名成员要写具体签名或在中文里说明。
- 单个函数的注释保持简短：`summary` 用 1–2 句说明做什么与返回什么，`param`/`returns` 只写调用方需要的取值、单位、边界和失败条件；不要粘贴实现步骤、重复类型名或展开长篇背景。
- 只描述当前契约，不写历史演进、版本沿革、草稿、废弃计划或“以前/以后”的对比；复杂背景放设计文档，不堆进单个方法注释。
- 注释说明契约、边界、失败条件与副作用，不逐行复述实现；公共 API 不用行间 `//` 代替 XML 注释。vendored 源文件保留头部来源与许可证说明。

```csharp
/// <summary>
/// Reads one bounded record and returns its payload.
/// <para>读取一条有界记录并返回其负载。</para>
/// </summary>
/// <param name="source">Record stream; must be readable.<para>记录流；必须可读。</para></param>
/// <param name="maximumBytes">Payload upper bound; negative values are rejected.<para>负载上限；负值会被拒绝。</para></param>
/// <returns>Payload bytes, or an empty array at end of stream.<para>负载字节；流结束时为空数组。</para></returns>
/// <exception cref="InvalidDataException">The record length exceeds <paramref name="maximumBytes"/>.<para>记录长度超过 <paramref name="maximumBytes"/>。</para></exception>
public static byte[] ReadRecord(Stream source, int maximumBytes)
```

## 构建与验证

```text
dotnet restore UotanStudio.slnx -p:Configuration=Debug
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet format UotanStudio.slnx whitespace --no-restore --verify-no-changes
dotnet format UotanStudio.slnx style --no-restore --verify-no-changes
```

日常改宿主或固件时用项目级构建，不必每次编全量 slnx：`dotnet build UotanStudio.Desktop/UotanStudio.Desktop.csproj --no-restore --nologo -c Debug` 覆盖宿主与全部库（约 12 个项目），`dotnet build UotanStudio.Firmware/UotanStudio.Firmware.csproj --no-restore --nologo -c Debug` 只到固件层；全量 slnx 构建多出的尾段是 `UotanStudio.Tests`（650 个源文件）与 `UotanStudio.Benchmarks`。提交前与最终验证仍以全量 slnx 构建、`dotnet format` 与测试为准。

Debug 构建必须显式写 `-c Debug`，还原也要带 `-p:Configuration=Debug`：`UotanStudio.csproj` 中 `Dock.Avalonia.Diagnostics` 的 `IncludeAssets` 条件按 `$(Configuration)` 求值，还原阶段该属性为空会剔除编译资产，使 `#if DEBUG` 里的 `AttachDockDebugOverlay` 调用以 `CS1061` 失败。CI 的 Release 配置不受影响。

测试命令、过滤器和跳过门控见 `UotanStudio.Tests/AGENTS.md`；性能基准见 `UotanStudio.Benchmarks/AGENTS.md`。项目硬性要求 NativeAOT：最终验证必须包含一次 `dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained`，且不得出现新的裁剪/AOT 警告；`PublishAot` 由 `UotanStudio.Desktop.csproj` 声明，Release 默认 `DebugType=none`，发布的原生镜像不产 PDB；反射式 DI 注册、未标注的 JSON 反射序列化或动态程序集扫描都不被接受。修改完成后报告实际变更、构建命令、定向测试、跳过的测试和仍未验证的边界。

所有内嵌二进制资源集中在 `UotanStudio.Assets/Assets/<bundle>/` 下（bundle 为 `fonts`、`ohos/device`、`drivers/rockusb`、`drivers/mediatek-vcom`、`drivers/mediatek-full`、`drivers/google-usb`、`drivers/rockchip-adb`、`drivers/qualcomm-qud`、`drivers/silabs-cp210x`、`drivers/ftdi-cdm`、`delivery`、`firmware/magic`，另有可选 `scrcpy`），每个负载是同目录的压缩帧 `<path>.zst`，唯一清单是 `UotanStudio.Assets/Assets/manifest.tsv`（`bundle`/`path`/长度/SHA-256/编码/来源六列）。改原始负载后运行 `dotnet run tools/compress-embedded-assets.cs` 重新生成帧与清单（`dotnet run tools/compress-embedded-assets.cs -- --check` 只校验），生成物与代码一起提交。`firmware/magic` 的帧由 `UotanStudio.Firmware.MagicGen` 生成，该脚本只负责索引。驱动包与字体的原始负载留在 `reference/` 下，脚本按 `specs` 的显式文件表（可直接从 zip 读取）生成帧，`Assets/drivers/<packageId>/` 只提交帧。消费方一律经 `UotanStudio.Assets` 的 `AssetCatalog`/`AssetStore` 准备与抽取，不得再各自读取程序集资源。

## Git 与工作区

- 当前工作区可能包含多个并行功能改动。只编辑完成当前任务所需的文件，保留无关修改。
- 禁止使用 `git reset --hard`、全量恢复或删除未知来源文件来“清理”工作区。
- 不要提交、推送、合并或重写历史，除非用户明确要求。需要提交时使用英文 Conventional Commit、`git commit -s`，并保持现有签名约定。