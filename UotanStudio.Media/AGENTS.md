# UotanStudio.Media 项目指令

在本目录内改音视频解码、像素转换或平台媒体后端前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目是纯媒体库：csproj 没有任何 `PackageReference` 或 `ProjectReference`，不得引用 `UotanStudio.Comm`、`UotanStudio.Firmware`、`UotanStudio.Projects`、宿主 `UotanStudio` 或 Avalonia。平台后端全部在本项目内实现。
- 目录职责固定为 `Decoding/`、`Acceleration/Linux/`、`GStreamer/`、`Audio/`、`Native/Linux/`、`Encoding/`；新代码按现有职责落位，不造转发层。
- 本项目启用 `AllowUnsafeBlocks`、`TreatWarningsAsErrors` 与 `GenerateDocumentationFile`；公共类型和成员缺中英双语 XML 注释会直接构建失败。
- “零依赖”与“零 Comm 依赖”没有架构测试守护，只能靠评审；改 csproj 时不要为了省事引入新的项目或 NuGet 引用。

## 解码与回退

- 统一入口是 `VideoDecoderFactory.Create`。只有 `VideoDecoderBackend.Auto` 允许平台回退：Windows 上 Media Foundation GPU → CPU，Linux 上 VA-API → GStreamer 硬件 → GStreamer 软件；macOS 走 VideoToolbox，AV1 转 GStreamer 硬件。显式选择的后端不得静默切换，不可用时抛 `NotSupportedException`。
- `FallbackVideoDecoder` 只在 `Initialize` 阶段回退：先释放首选再建备选，两侧都失败时保留 `AggregateException` 作为内部异常。码流开始处理之后不得换后端，错误交调用方重连或重选。
- CPU 回退必须保留：未初始化或没有真实 GPU 导出时，`GetFramePixelFormat` 必须报 `NV12`，不得虚报 `BGRA`。消费方按该值决定转换路径，不要靠 `GetBackendName` 的显示名称判断。
- `IVideoDecoder` 非线程安全，应由单一解码循环持有；帧回调可能在原生线程触发，帧在回调返回后释放，消费方必须在回调内完成复制或转换。
- 所有原生帧边界复用 `VideoFrameLimits`（最大 16384 维、帧 ≤256 MiB、访问单元 1 B–32 MiB、NV12 长度 checked）与 `AudioFormatLimits`（包 ≤16 MiB、1–8 声道、7.35–192 kHz、16/32 bit）；不要在新后端里另写魔数。
- NV12→BGRA 的矩阵与范围来自显式参数；`Auto` 解析为 BT.601 limited，支持 BT.601/709/2020。CPU 转换与 GPU shader 共用 `Nv12ConversionTable` 的定点常量，改动要同步两侧。

## 平台与原生边界

- P/Invoke 优先使用 `[LibraryImport]` 源生成；GStreamer 真实库名由 `GstNative` 的 `DllImportResolver` 映射，缺失时抛带安装指引的异常，不要改回 `DllImport` 或静默返回 false。
- Linux VA-API 桥接 `libuotan_media_va.so` 的 ABI、像素格式约束、线程绑定与导出/复制语义以 `Native/Linux/README.md` 为准；改 C ABI 或像素路径要同步 `uotan_va.h`、`CMakeLists.txt` 与 `bridge_tests.c`。
- csproj 的 `ValidateUotanVaNativeLibrary` 在 `UotanVaNativeLibrary` 指向的文件不存在时直接报错；不要删掉这道构建期检查。
- 硬件解码、GPU 零拷贝和平台原生行为不能只凭托管契约测试或软件参考解码写成已通过；本机缺少 `/dev/dri` 等环境时按未验证处理。

## 验证

```text
dotnet build UotanStudio.Media/UotanStudio.Media.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Media.VideoDecoderFactoryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Media.FallbackVideoDecoderTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Media.Nv12ToBgraConverterTests --minimum-expected-tests 1
```