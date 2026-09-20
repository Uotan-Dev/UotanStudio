# Firmware 限界上下文

本项目处理固件字节、格式结构及其校验。UI、项目会话、工具下载和交付编排由宿主及对应子项目负责。

## 目录与职责

| 上下文 | 职责 |
| --- | --- |
| `BoundedContexts/AndroidMeta` | APK、DEX、AXML、ARSC 等 Android 包与元数据 |
| `BoundedContexts/Archive` | 归档成员、路径、元数据、浏览、提取与写入；CPIO 和 TAR 格式读写 |
| `BoundedContexts/Compression` | 压缩格式识别、参数、流式压缩/解压、文件操作及底层编码器 |
| `BoundedContexts/BootSecurity` | 启动镜像、AVB 签名与验证、启动补丁 |
| `BoundedContexts/DeviceTree` | 设备树解析与构建 |
| `BoundedContexts/FileSystem` | EXT4、EROFS、F2FS 的目录、inode、数据簇及重建 |
| `BoundedContexts/BlockImage` | Sparse 块映射、原始镜像转换、LP/Super 元数据与逻辑分区布局 |
| `BoundedContexts/OtaDelta` | Payload、DAT、增量操作与差分应用 |
| `BoundedContexts/VendorFirmware` | 厂商固件容器与对应封装协议 |
| `SharedKernel` | 通用格式契约、基于处理器的识别、进度、流与范围校验等共享基础能力 |
| `SharedKernel/Repacking` | 通用重打包元数据、显式上下文、原子 JSON 保存和格式扩展契约 |
| `Application/Formats` | 内置格式处理器的注册与组装 |
| `Application/Containers` | 组合 ZIP、Payload、LP/Super 能力，分析容器结构并提取选定成员 |
| `Infrastructure/Downloads` | HTTP 下载、GitHub 镜像选择与网络传输 |

通用压缩是供其他上下文调用的支撑上下文。它拥有算法选择、压缩参数与压缩流语义，不能依赖归档、文件系统、OTA、启动镜像或宿主。归档上下文拥有成员列表和成员提取语义，可以调用压缩上下文处理外层压缩。文件系统簇、Payload 操作和启动镜像 ramdisk 的专有封装仍由各自上下文解析，通用编码器不负责解释这些领域结构。

`Application/Formats/FirmwareFormatRegistry` 是内置处理器的组装入口，集中注册上下文处理器。`SharedKernel/FormatDetector` 只面向已注册的 `IReadonlyFormatHandler`，不构造具体格式实现。

依赖方向为：宿主、CLI、MCP 调用应用编排或上下文入口；应用编排调用上下文；上下文使用共享契约和基础能力。`SharedKernel` 不引用 `BoundedContexts`、`Application` 或 `Infrastructure`；上下文不引用 `Application`、`Infrastructure` 或宿主。下载基础设施由需要网络的调用方显式使用。

这些边界位于同一个程序集内，按实际格式职责组织，不统一增加 `Entity`、`Repository`、事件总线或多层转发。现有上下文之间仍有明确的格式组合，例如 Sparse 的文件系统提取与文件系统重建的 Sparse 输出；本次没有把这些依赖拆成独立程序集。

## 重打包与文件系统元数据

`SharedKernel/Repacking/RepackMetadata` 负责通用来源信息、元数据路径和操作上下文、解包后的保存顺序及有界原子发布。`IRepackMetadataProvider` 由需要重建信息的格式处理器实现：配置阶段提供该格式的配置、选项和目录打包能力，捕获阶段在 JSON 发布前完成附属数据保存。新增格式通过该契约接入，不在共享层增加具体格式类型判断。未实现契约的处理器仍保存通用来源信息，但不声明目录重建能力。

| 目录 | 职责 |
| --- | --- |
| `FileSystem/Metadata` | `fs_config`、SELinux 标签、分区信息及版本化元数据文档 |
| `FileSystem/Extraction` | Linux 镜像路径到宿主可用路径的映射与提取路径记录 |
| `FileSystem/Rebuilding` | 原始参数读取、打包选项合并、编辑计划、内容保存与镜像重建 |
| `FileSystem/Erofs/Builder` | EROFS 构建器及兼容性默认值 |

`FileSystemRepackMetadata` 统一处理 EXT4、EROFS、F2FS 和包含这些文件系统的 Sparse 输入。Boot 的配置和 AVB 限制由 Boot 处理器提供，DTBO 配置由设备树处理器提供。通用元数据对象不再依赖这些解析器或构建器。

`FileSystemByteProgress` 保留在共享契约中，因为格式接口的打包重载和 `UnpackOptions` 对所有调用方公开该进度类型。它不负责选择文件系统实现。

## 压缩能力入口

`CompressionService` 提供 `Compress`、`Decompress` 的流及字节数组重载，负责选择和调用编码器。`Files/CompressionFile` 拥有 `CompressFile`、`DecompressFile`、`DecompressFileAuto`、`DetectFile` 及原子发布实现，负责文件生命周期和进度。生产调用优先使用流/文件重载；已有字节数组重载保留用于兼容。文件级操作的取消、进度和输出发布策略沿用现有实现。

`CompressionFormats` 是格式名称、扩展名和能力的统一来源；`Detection/CompressionDetector` 负责字节数组、字节跨度及流头部识别，不根据文件名猜测魔数。`CompressFormatHandler` 将这些能力接入固件格式处理流程。不要在调用方复制格式列表或头部探测逻辑。

| 格式或策略 | 实现入口 | 说明 |
| --- | --- | --- |
| Brotli | `Compressors/BrotliCompressor.cs` | 压缩、解压；无可靠固定魔数，需要显式格式或外部上下文 |
| LZ4 / LZ4HC | `Compressors/Lz4Compressor.cs` | 同一种 LZ4 格式；默认快速压缩，HC 通过 `CompressionOptions.Level` 选择，例如 9 或 12，无需独立解码器 |
| LZ4 Legacy | `Compressors/Lz4Compressor.cs` | 独立的 legacy 帧封装 |
| Zstandard | `Compressors/ZstdCompressor.cs` | 压缩、解压 |
| DEFLATE | `Compressors/DeflateCompressor.cs` | 原始 DEFLATE 流；不能依赖固定魔数自动识别 |
| gzip / zlib | `Compressors/GzipCompressor.cs`、`ZlibCompressor.cs` | 各自封装 DEFLATE，头部与校验语义不同 |
| LZMA / XZ | `Compressors/LzmaCompressor.cs`、`XzCompressor.cs` | LZMA 编码与 XZ 容器 |
| bzip2 | `Compressors/Bzip2Compressor.cs` | 压缩、解压 |
| lzop / LZO | `Compressors/LzopCompressor.cs`、`Codecs/Lzo/Lzo1x.cs` | lzop 容器与 LZO 块编码分层 |
| Zopfli | `CompressionFormats` / `CompressionService` | 现有兼容枚举仅支持解压路由；未实现 Zopfli 压缩 |

`Codecs/Lz4/` 与 `Codecs/Lzo/` 分别存放 LZ4 目标长度编码与 LZO1X；`Internal/` 存放压缩上下文内部的等级映射、有界并行和 XZ 实现辅助代码。LZ4 编码器许可证随构建和发布输出复制。

## 归档能力入口

CPIO 和 TAR 是归档格式，描述文件成员及其元数据；它们本身不等同于压缩算法。`.cpio.gz`、`.tar.xz` 等输入由归档与压缩能力组合处理。

- `Browsing/ArchiveBrowser.cs`：统一浏览和选择性提取入口，包括现有第三方归档读取路径及单文件压缩浏览。
- `Models/`、`Extraction/`、`Writing/`：归档条目、提取策略和写入选项。
- `Formats/Cpio/`、`Formats/Tar/`：分别实现 CPIO 与 TAR 读写，TAR 不再嵌套在 CPIO 目录下。
- `CpioArchive.cs`：沿用已有类名及 CPIO/TAR 统一读写行为；`Editing/CpioArchiveEditor.cs` 负责编辑，`CpioFormatHandler.cs` 负责格式接入。

## 内部职责分类

目录决定命名空间，例如 `BlockImage/Sparse/IO/SparseReader.cs` 使用 `UotanStudio.Firmware.BoundedContexts.BlockImage.Sparse.IO`。同一类型的 partial 文件放在同一职责目录，保持相同命名空间。与格式状态紧密相关的私有方法留在所属类型中，不为每个方法建立转发类。

| 范围 | 职责归属 |
| --- | --- |
| Android APK | `Parsing` 解析、`Models` 元数据、`Resources` 图形资源、`Packing` 重打包、`Signing` 签名、`Keys` 密钥材料 |
| AXML / ARSC | `Parsing`、`Writing`、`Models`；AXML 文本转换归 `Text`；共同使用的字符串池与有界读取分别归 `AndroidMeta/Resources`、`AndroidMeta/IO` |
| DEX | `Reading`、`Writing`、`Models`、`IO`、`Smali`、`Containers`；指令与反编译保持 `Instructions`、`Decompile` |
| AVB | `Models` 头部/描述符、`Signing` 签名、`Verification` 验证、`Build` 哈希树构建 |
| BootImage | `Models` 格式模型、`Detection` 探测、`Parser` 解析、`Packer` 构建、`Components` ramdisk/bootconfig/内嵌 DTB 组合、`Formats` 特殊封装、`Elf` ELF 处理 |
| Boot Patcher | `Kernel` 内核补丁及版本识别、`Ksud` ksud ELF 与提取、`Models` 补丁输入输出；服务编排留在根层 |
| DeviceTree | `Models` 设备树及头部、`Dtb` 二进制、`Dts` 文本、`Dtbo` overlay 容器、`Overlay` 合并操作 |
| EXT4 / EROFS / F2FS | `Models` 磁盘模型、`Parser` 读取、`Builder` 构建；EXT4 索引与日志归 `Index` / `Journal`，EROFS/F2FS 专有压缩簇归各自 `Compression`，EROFS 校验算法归 `Checksums` |
| Sparse | `Models` 块模型、`IO` 镜像流读写、`DataProviders` 数据来源、`Conversion` 镜像转换 |
| LP/Super | `Models` 磁盘布局、`Reading`、`Writing`、`Build` 元数据构建 |
| DAT | `Models` transfer list 模型、`Serialization` 序列化、`Reader`、`Writer`、`Files` 文件组匹配 |
| Payload | `Models` 头部及元数据、`Proto` 协议模型、`Parser` 解析、`Builder` 构建、`Extraction` 分区提取、`Operations` 操作执行、`IO` extent/校验流、`Verification` 哈希校验 |
| OTA 差分 | `Diff/Bsdiff`、`Diff/ImgDiff`、`Diff/PuffDiff`、`Diff/Lz4Diff`、`Diff/Zucchini` 按差分格式分组；`Engines` 负责差分编排 |
| 厂商容器 | 原有厂商边界内按 `Models`、`Extraction`、`Parsing`、`Packing`、`Crypto` 等实际职责归类；既有 NTPi Reader/Writer 和 OPPO Parsers 等分层保留 |

Sparse 描述块映射，LP/Super 描述分区布局，二者不拥有目录和 inode，因此统一归 `BlockImage`。Zucchini 是差分协议，归 OTA 差分而非厂商容器。原 BootImage `PayloadTool` 的 OTA 探测和提取方法归 `OtaDelta/Payload/Extraction`。

NTPi 的 `Compression/Lzma2Compressor` 实际还处理 `NTDECOMP` 头部、长度和填充约束，因此保留在 NTPi 边界。PuffDiff 本次保留原有 `PayloadException` 异常契约；其对 Payload 异常命名空间的依赖仍存在，不能将本次分类理解为已经实现程序集级的完全依赖隔离。

## 迁移说明

压缩命名空间由 `UotanStudio.Firmware.CrossCutting.Compress` 改为 `UotanStudio.Firmware.BoundedContexts.Compression`；LZO 块实现移入该上下文的 `Codecs`。归档命名空间去掉原有 `Archive.Cpio` 的多余外层，使用 `UotanStudio.Firmware.BoundedContexts.Archive` 及其子命名空间。

仓库内调用方同步迁移。外部直接引用这些公开类型的源码需要更新命名空间并重新编译；本次未提供旧命名空间转发层。格式枚举值、算法行为及固件序列化格式不因此改变。

原 `FileSystem.Sparse`、`LogicalPartition.Lp` 分别改为 `BlockImage.Sparse`、`BlockImage.Lp`，并按具体职责使用子命名空间。各格式原本堆叠在根命名空间的类型也改用对应职责目录的命名空间。

文件操作调用从 `CompressionService.CompressFile/DecompressFile/DecompressFileAuto/DetectFile` 改为 `CompressionFile` 的同名方法；头部识别调用从 `CompressionFormats.Detect` 或 `CompressionService.Detect` 改为 `CompressionDetector.Detect`。取消、大小限制、进度上报、异常及发布策略保持原调用语义。

共享层整理后的主要命名空间迁移：

| 原位置 | 新位置 |
| --- | --- |
| `SharedKernel.FirmwareFormatRegistry` | `Application.Formats.FirmwareFormatRegistry` |
| `SharedKernel.Containers` | `Application.Containers` |
| `SharedKernel.RepackMetadata` / `RepackMetadataContext` | `SharedKernel.Repacking` |
| `SharedKernel.FileSystemMetadataGenerator` / `FileSystemMetadataOverrides` / `FsConfigEntry` | `BoundedContexts.FileSystem.Metadata` |
| `SharedKernel.FileSystemPreservation` | `BoundedContexts.FileSystem.Rebuilding` |
| `SharedKernel.ErofsBuildDefaults` | `BoundedContexts.FileSystem.Erofs.Builder` |
| `SharedKernel.HttpDownload` / `GitHubMirrorSite` | `Infrastructure.Downloads` |

表中命名空间均省略 `UotanStudio.Firmware.` 前缀。仓库内调用同步迁移；外部源码需要更新引用并重新编译。元数据文件名、JSON schema、版本和字段保持原契约，已有项目元数据不需要迁移。只按名称伪装成内置格式的自定义处理器不再获得该格式的重建策略；自定义处理器应显式实现 `IRepackMetadataProvider`。
