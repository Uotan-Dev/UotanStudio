# UotanStudio.Compress 项目指令

在本目录内新增或修改压缩/解压能力前读这份文件。项目定位、目录与格式表见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本库只引用 `UotanStudio.Hash`，不得引用 `UotanStudio.Firmware` 或任何上层项目；反向引用会形成程序集环。`UotanStudio.Tests/Firmware/Architecture/CompressProjectBoundaryTests.cs` 会同时校验工程引用、命名空间与固件引用。
- 每个源文件的命名空间必须由目录推导（`Compressors/`、`Files/`、`Detection/`、`Internal/`、`Codecs/Lz4` 等）；`Properties/` 除外。不要移动目录后留下旧命名空间。
- 本库只拥有通用算法选择、压缩参数、流与文件编排、容器编解码；归档成员、文件系统簇、OTA、启动镜像、厂商容器语义留在 `UotanStudio.Firmware`，不要在这里复制。
- 通用压缩只在 `UotanStudio.Compress` 内实现：其他生产工程不得直接 `new GZipStream`、`DeflateStream`、`ZLibStream` 或 `BrotliStream`，必须经本库门面。保留的 BCL 例外与理由集中在 `CompressionImplementationBoundaryTests.cs` 的允许列表，新增例外要同时给出理由。
- ZIP 容器的构造与打开统一经 `Zip/ZipContainer`；其他生产工程不得直接 `new ZipArchive` 或调用 `ZipFile.Open*`，条目路径规范化与可选上限由该门面提供，`ContainerBoundaryGuardTests` 守护这条边界。

## 入口与行为

- 公共契约以 `CompressionService`、`Files/CompressionFile`、`CompressionFormats`、`Detection/CompressionDetector` 为准。不要在调用方复制格式名称、别名、扩展名、能力判断或魔数探测；`CompressionFormats` 是唯一来源。
- `CreateCompressionStream` / `CreateDecompressionStream` 返回增量流，不施加输出上限，释放时只写容器尾部、不关闭调用方流。需要一次性解压并限制展开大小时使用带 `long` 上限的 `Decompress` 重载。
- 文件级操作必须保留取消、进度、解压大小上限与临时文件原子发布；不要为了简化而把整文件读入内存。失败时保留原目标内容。
- `Codecs/Lz4/Lz4BlockCodec`、`Codecs/Zstd/ZstdBlockCodec`、`Codecs/Lz4/Lz4DestinationCodec`、`Codecs/Lzo/Lzo1x` 与 `Codecs/Deflate/PuffDeflateCodec` 是给固件文件系统/Payload/差分使用的格式域原语，因 `UotanStudio.Firmware` 需要直接调用而保持 public；它们不是宿主可用的通用压缩门面，宿主仍应走 `CompressionService`/`CompressionFile`。本库不再对任何生产项目开放 `InternalsVisibleTo`。
- 现有格式入口的能力差异（哪些格式只有整块入口、哪些返回 `NotSupportedException`）是当前契约；改动前先读 README 的“压缩能力入口”，不要用“补一个重载”掩盖。

## 文档与验证

- 本库设置 `GenerateDocumentationFile` 与 `TreatWarningsAsErrors`；公共类型和成员必须有中英双语 XML 注释，缺注释直接构建失败。
- `Codecs/Lz4/Lz4DestinationCodec.LICENSE` 随构建和打包输出；vendored 源文件保留头部来源与许可证说明。
- 定向验证：

```text
dotnet build UotanStudio.Compress/UotanStudio.Compress.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.CompressProjectBoundaryTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.CompressionImplementationBoundaryTests --minimum-expected-tests 1
```