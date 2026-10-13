# UotanStudio.Compress

面向 .NET 10 的通用压缩支撑类库，根命名空间为 `UotanStudio.Compress`。它拥有算法选择、压缩参数与压缩流语义：调用方只描述要用哪个格式、什么等级、压缩还是解压，由本库决定具体编码器、容器封装与并行策略，不承担归档成员、文件系统簇、OTA、启动镜像或宿主的领域语义。

`UotanStudio.Firmware` 的归档、文件系统、Payload 与启动镜像路径调用本库处理外层压缩；本库不认识这些领域结构。生产调用优先使用流/文件重载，字节数组重载只用于兼容与小块数据。

## 目录与职责

| 目录 | 命名空间 | 职责 |
| --- | --- | --- |
| `Compressors` | `UotanStudio.Compress.Compressors` | 各算法的压缩与解压入口，按格式一个文件；等级经等级映射落到具体编码器 |
| `Files` | `UotanStudio.Compress.Files` | 文件级编排：文件生命周期、取消、解压大小上限、进度上报与原子发布 |
| `Detection` | `UotanStudio.Compress.Detection` | 字节数组、字节跨度与流头部的魔数识别；不根据文件名猜测 |
| `Internal` | `UotanStudio.Compress.Internal` | 等级映射、有界并行、XZ 容器与 LZMA2、非释放包装流，以及本库自有的叶子辅助类型 |
| `Codecs/Lz4` | `UotanStudio.Compress.Codecs.Lz4` | 限定目标大小的 LZ4 编码器及其基本操作 |
| `Codecs/Lzo` | `UotanStudio.Compress.Codecs.Lzo` | LZO1X 块编解码 |

项目根层承载公开契约：`CompressionFormat` 枚举、`CompressionFormatInfo`、`CompressionOptions`、`CompressionFormats`、`CompressionService` 与内部使用的 `CompressionException`。

## 压缩能力入口

`CompressionService` 提供 `Compress`、`Decompress` 的字节数组与流重载，负责选择并调用编码器，包括有界并行与流式/非流式格式的分派。

`Files/CompressionFile` 提供 `CompressFile`、`DecompressFile`、`DecompressFileAuto`、`DetectFile`，负责文件生命周期、取消、解压大小上限、进度上报和输出的原子发布。

`CompressionFormats` 是格式名称、别名、扩展名与能力的统一来源；`Detection/CompressionDetector` 负责字节数组、字节跨度及流头部识别，流可定位时恢复原位置，不根据文件名猜测魔数。

不要在调用方复制格式列表或头部探测逻辑：名称解析、扩展名映射、能力判断与魔数识别都从上述入口获取。

## 格式或策略

| 格式或策略 | 实现入口 | 说明 |
| --- | --- | --- |
| Brotli | `Compressors/BrotliCompressor.cs` | 压缩、解压；无可靠固定魔数，需要显式格式或外部上下文 |
| LZ4 / LZ4HC | `Compressors/Lz4Compressor.cs` | 同一种 LZ4 格式；默认快速压缩，HC 通过 `CompressionOptions.Level` 选择，例如 9 或 12，无需独立解码器 |
| LZ4 Legacy | `Compressors/Lz4Compressor.cs` | 独立的 legacy 帧封装 |
| Zstandard | `Compressors/ZstdCompressor.cs` | 压缩、解压 |
| DEFLATE | `Compressors/DeflateCompressor.cs` | 原始 DEFLATE 流；不能依赖固定魔数自动识别 |
| gzip / zlib | `Compressors/GzipCompressor.cs`、`ZlibCompressor.cs` | 各自封装 DEFLATE，头部与校验语义不同 |
| LZMA / XZ | `Compressors/LzmaCompressor.cs`、`XzCompressor.cs` | LZMA 编码与 XZ 容器，容器与 LZMA2 辅助位于 `Internal/` |
| bzip2 | `Compressors/Bzip2Compressor.cs` | 压缩、解压 |
| lzop / LZO | `Compressors/LzopCompressor.cs`、`Codecs/Lzo/Lzo1x.cs` | lzop 容器与 LZO 块编码分层 |
| Zopfli | `CompressionFormats` / `CompressionService` | 兼容枚举只支持解压路由，经 gzip 读取器解码；未实现 Zopfli 压缩 |

`LzopCompressor`、`XzCompressor`、`Lz4Compressor`、`Bzip2Compressor` 为本程序集内部实现，调用方统一经 `CompressionService` 与 `CompressionFile` 使用。

## 依赖方向

本库只引用 `UotanStudio.Hash` 项目，以及 `K4os.Compression.LZ4`、`K4os.Compression.LZ4.Streams`、`LZMA-SDK`、`SharpCompress`、`System.IO.Hashing`、`ZstdSharp.Port` 六个第三方包。

本库不得引用 `UotanStudio.Firmware`：Firmware 反过来调用本库的压缩能力，反向引用会形成程序集环。该边界由 `UotanStudio.Tests/Firmware/Architecture/CompressProjectBoundaryTests.cs` 守护，它同时校验每个源文件声明的命名空间与文件夹路径一致。

`UotanStudio.Firmware` 的 `Core/` 是“不依赖第三方、不被下层依赖”的共享内核，本库位于其之下，因此不能复用内核里的 `TempFileHelper`、`StreamCopy`、`AtomicPublish`、`CountingReadStream`、`CountingWriteStream`，必须自带最小实现：`Internal/ScratchFile` 提供临时刮擦路径、`Internal/ExactCopy` 提供精确字节复制、`Internal/AtomicFilePublish` 提供原子发布的暂存文件、`Internal/ByteCountingReadStream` 与 `Internal/BoundedWriteStream` 提供计数与有界流包装。同一个 `CountingWriteStream` 在 Firmware 中另有 40 多处用途，内核副本保留不动，两侧不共享。

## 不在此项目内

下列领域封装留在 `UotanStudio.Firmware`，本库不复制其语义：

- SquashFS、EROFS、F2FS 的块级压缩簇封装（`FileSystem/Squashfs/SquashfsDecompression.cs`、`FileSystem/Erofs/ErofsCompression.cs`、`FileSystem/F2fs/F2fsCompression.cs`）。
- boot 镜像组件封装（`BootSecurity/BootImage/Components/BootCompressor.cs`）。
- Payload 压缩操作（`OtaDelta/Payload/**`）。
- `.cpio.gz`、`.tar.xz` 等归档与压缩组合（`Archive/**`）。
- NTPi 的 `Lzma2Compressor`，它还处理 `NTDECOMP` 头部约束。
- `.dz` 厂商容器。
- 格式处理器 `CompressFormatHandler`，位于 `UotanStudio.Firmware.Application.Formats`，实现 `IFormatHandler` 并走 `Core/Repacking/RepackMetadata`，属于固件的组合层。

## 许可证

`Codecs/Lz4/Lz4DestinationCodec.LICENSE` 随构建与发布输出复制，打包时写入 `licenses/Lz4DestinationCodec.LICENSE`。底层编码器移植自 liblz4 1.9.4，源文件保留头部来源与许可证说明。
