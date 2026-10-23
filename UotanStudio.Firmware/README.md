# Firmware 限界上下文

本项目处理固件字节、格式结构及其校验。UI、项目会话、工具下载和交付编排由宿主及对应子项目负责。

## 目录与职责

| 上下文 | 职责 |
| --- | --- |
| `AndroidMeta` | APK、DEX、AXML、ARSC 等 Android 包与元数据 |
| `Archive` | 归档成员、路径、元数据、浏览、提取与写入；CPIO 和 TAR 格式读写 |
| `UotanStudio.Compress` | 通用压缩已外移为独立项目；本项目只保留各领域的块级压缩簇封装 |
| `BootSecurity` | 启动镜像、AVB 签名与验证、启动补丁 |
| `DeviceTree` | 设备树解析与构建 |
| `FileSystem` | EXT4、EROFS、F2FS 的目录、inode、数据簇及重建 |
| `BlockImage` | Sparse 块映射、原始镜像转换、LP/Super 元数据与逻辑分区布局 |
| `OtaDelta` | Payload、DAT、增量操作与差分应用 |
| `VendorFirmware` | 厂商固件容器与对应封装协议 |
| `Executable` | ELF 可执行文件读取与 Qualcomm 镜像分析、认证与元数据解析 |
| `Patching` | 针对 fstab、init.rc、property、INI、JSON、XML、fs_config 等镜像配置与文本内容的补丁应用 |
| `Core` | 流包装、校验和、格式探测契约、Magic 识别引擎、通用格式契约、基于处理器的识别、进度与范围校验等共享基础能力 |
| `Core/Repacking` | 通用重打包元数据、显式上下文、原子 JSON 保存和格式扩展契约 |
| `Application/Formats` | 内置格式处理器的注册与组装 |
| `Application/Containers` | 组合 ZIP、Payload、LP/Super 能力，分析容器结构并提取选定成员 |

通用压缩是独立的支撑项目 `UotanStudio.Compress`，本项目通过项目引用调用其能力。本项目负责归档、文件系统、OTA、启动镜像与厂商容器中的领域封装，包括成员列表、块级压缩簇和专有容器头部；通用编码器不解释这些领域结构。

`Application/Formats/FirmwareFormatRegistry` 是内置处理器的组装入口，集中注册上下文处理器。`Core/FormatDetector` 只面向已注册的 `IReadonlyFormatHandler`，不构造具体格式实现。

依赖方向为：宿主、CLI、MCP 调用应用编排或上下文入口；应用编排调用上下文；上下文使用共享契约和基础能力。`Core` 不引用其他模块、`Application` 或 `Infrastructure`；各模块不引用 `Application`、`Infrastructure` 或宿主。下载、GitHub 镜像与 Release 元数据由叶子库 `UotanStudio.Net` 提供，Firmware 不再持有下载基础设施。

这些边界位于同一个程序集内，按实际格式职责组织，不统一增加 `Entity`、`Repository`、事件总线或多层转发。现有上下文之间仍有明确的格式组合，例如 Sparse 的文件系统提取与文件系统重建的 Sparse 输出；这类跨上下文依赖保持在同一程序集内。

## 重打包与文件系统元数据

`Core/Repacking/RepackMetadata` 负责通用来源信息、元数据路径和操作上下文、解包后的保存顺序及有界原子发布。`IRepackMetadataProvider` 由需要重建信息的格式处理器实现：配置阶段提供该格式的配置、选项和目录打包能力，捕获阶段在 JSON 发布前完成附属数据保存。新增格式通过该契约接入，不在共享层增加具体格式类型判断。目录重建能力只来自该契约：未实现契约的处理器仍保存通用来源信息，但不声明重建能力，仅 `FormatName` 与内置格式相同的自定义处理器也不会获得对应格式的重建策略。元数据文件名、JSON schema、版本和字段构成当前持久化契约。

| 目录 | 职责 |
| --- | --- |
| `FileSystem/Metadata` | `fs_config`、SELinux 标签、分区信息及版本化元数据文档 |
| `FileSystem/Extraction` | Linux 镜像路径到宿主可用路径的映射与提取路径记录 |
| `FileSystem/Rebuilding` | 原始参数读取、打包选项合并、编辑计划、内容保存与镜像重建 |
| `FileSystem/Erofs/Builder` | EROFS 构建器及兼容性默认值 |

`FileSystemRepackMetadata` 统一处理 EXT4、EROFS、F2FS 和包含这些文件系统的 Sparse 输入。Boot 的配置和 AVB 限制由 Boot 处理器提供，DTBO 配置由设备树处理器提供。通用元数据对象不依赖这些解析器或构建器。

`FileSystemByteProgress` 位于共享契约中，因为格式接口的打包重载和 `UnpackOptions` 对所有调用方公开该进度类型。它不负责选择文件系统实现。

## 压缩能力入口（已外移）

通用压缩由 `UotanStudio.Compress` 提供：`CompressionService` 负责算法选择与流/字节数组的压缩解压，`Files/CompressionFile` 负责文件生命周期、取消、大小上限、进度与原子发布，`CompressionFormats` 是格式名称、别名、扩展名与能力的统一来源，`Detection/CompressionDetector` 负责字节与流头部识别。不要在调用方复制格式列表或头部探测逻辑。

`CompressFormatHandler` 位于 `Application/Formats`，把这些能力接入固件格式处理流程，并走 `Core/Repacking/RepackMetadata`。它消费 `UotanStudio.Compress` 的公开入口，本项目的领域压缩簇继续由各自上下文解析。

## 归档能力入口

CPIO 和 TAR 是归档格式，描述文件成员及其元数据；它们本身不等同于压缩算法。`.cpio.gz`、`.tar.xz` 等输入由归档与压缩能力组合处理。

- `Browsing/ArchiveBrowser.cs`：统一浏览和选择性提取入口，包括现有第三方归档读取路径及单文件压缩浏览。
- `Models/`、`Extraction/`、`Writing/`：归档条目、提取策略和写入选项。
- `Formats/Cpio/`、`Formats/Tar/`：分别实现 CPIO 与 TAR 读写，两个格式目录彼此独立，TAR 不嵌套在 CPIO 目录下。
- `CpioArchive.cs`：统一承载 CPIO/TAR 读写行为；`Editing/CpioArchiveEditor.cs` 负责编辑，`CpioFormatHandler.cs` 负责格式接入。

## 内部职责分类

目录决定命名空间，例如 `BlockImage/Sparse/IO/SparseReader.cs` 使用 `UotanStudio.Firmware.BlockImage.Sparse.IO`。同一类型的 partial 文件放在同一职责目录，保持相同命名空间。与格式状态紧密相关的私有方法留在所属类型中，不为每个方法建立转发类。

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
| 厂商容器 | 各厂商边界内按 `Models`、`Extraction`、`Parsing`、`Packing`、`Crypto` 等实际职责归类；NTPi 保持 Reader/Writer 分层，OPPO 保持 Parsers 分层 |

Sparse 描述块映射，LP/Super 描述分区布局，二者不拥有目录和 inode，因此统一归 `BlockImage`。Zucchini 是差分协议，归 OTA 差分而非厂商容器。`PayloadTool` 的 OTA 探测与提取方法位于 `OtaDelta/Payload/Extraction`。

NTPi 的 `Compression/Lzma2Compressor` 实际还处理 `NTDECOMP` 头部、长度和填充约束，因此归入 NTPi 边界。PuffDiff 使用 `PayloadException` 作为异常契约，对 Payload 异常命名空间的依赖仍然存在；目录分类不代表厂商容器与 OTA 差分之间实现了程序集级依赖隔离。

## 命名空间与调用入口

命名空间由目录决定：归档模块为 `UotanStudio.Firmware.Archive`，Sparse 与 LP 位于 `BlockImage`，容器组合与内置处理器注册位于 `Application.Containers` 与 `Application.Formats`，HTTP 下载与 GitHub 镜像选择位于 `Infrastructure.Downloads`，重打包元数据位于 `Core.Repacking`。压缩模块位于 `UotanStudio.Compress`。

文件操作入口是 `UotanStudio.Compress.Files.CompressionFile` 的 `CompressFile`、`DecompressFile`、`DecompressFileAuto`、`DetectFile`；头部识别入口是 `CompressionDetector.Detect`；字节数组与流重载入口是 `CompressionService.Compress`、`Decompress`。程序集不携带其它命名空间的类型转发，外部调用方只能按当前命名空间引用公开类型。
