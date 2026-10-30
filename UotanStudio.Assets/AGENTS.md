# UotanStudio.Assets 项目指令

在本目录内改资源、清单或资源门面前读这份文件。模块职责、公共 API 与用法示例见同目录 `README.md`，资源目录的来源索引见 `Assets/README.md`；仓库级规则（构建命令、Git 纪律、不可信输入上限）在根 `AGENTS.md`。

## 边界

- 只引用 `UotanStudio.Hash` 与 `UotanStudio.Compress`，净依赖图由 `UotanStudio.Tests` 的 `ProjectReferenceDirectionGuardTests.Allowed` 把关；新增引用前先确认方向，不得引用其他业务项目。
- 不引用 `UotanStudio.Net`，也不做网络 I/O：负载随程序集分发，运行期只读内嵌帧。
- 本工程不声明 RID 相关的 `Content`/`None` 复制项，也不按 `RuntimeIdentifier` 分叉：负载只作为 `EmbeddedResource` 进程序集，各平台发布产出一致。

## 资源布局与命名

- 压缩帧路径恒为 `Assets/<bundle>/<path>.zst`，与原始负载同目录。
- 逻辑资源名恒为 `assets/<bundle>/<path>.zst`，由 csproj 的 `EmbeddedResource` 逐 bundle 指定，运行时按同一规则拼名。`LogicalName` 必须写成 `%(Filename)%(Extension)`，因为 `%(Filename)` 会丢掉 `.zst` 后缀。
- bundle 是资源根下的相对目录（`fonts`、`ohos/device`、`drivers/rockusb`、`drivers/mediatek-vcom`、`drivers/mediatek-full`、`drivers/google-usb`、`drivers/rockchip-adb`、`drivers/qualcomm-qud`、`drivers/silabs-cp210x`、`drivers/ftdi-cdm`、`delivery`、`firmware/magic`，另有可选 `scrcpy`），path 是纯文件名、不含任何分隔符；bundle 键总长上限 128 字符，每个目录段与文件名也各不超过 128 字符。
- 稳定键在 `AssetNames.Bundles` 与 `AssetNames.Fonts`/`AssetNames.Delivery`/`AssetNames.Magic`/`AssetNames.Scrcpy`；`AssetCatalog` 用同一组键暴露 `AssetBundle`。改键或加文件要同步清单、csproj 与消费方，否则消费方查不到条目。
- 驱动包的 path 用 `__` 编码一级目录（`x64__usb2ser.sys` 代表 `x64/usb2ser.sys`），消费方负责还原目录；因为暂存厂商 INF 需要原始目录布局，而资源 path 只能是纯文件名。

## 清单

- 唯一清单是 `Assets/manifest.tsv`，以逻辑名 `assets/manifest.tsv` 内嵌；六列制表符分隔：`bundle`、`path`、解压长度、小写 SHA-256、编码（当前只接受 `zstd`）、`origin`。
- `origin=source` 表示帧由同目录的原始负载压缩得到；`origin=generated` 表示负载本身由生成器产出。当前只有 `firmware/magic` 的 `CompiledMagicData.zst` 是 `generated`：它由 `UotanStudio.Firmware.MagicGen` 生成，生成脚本只读取并索引它，不再压缩。两种来源的帧都必须与清单行的长度、SHA-256 逐字节一致。
- 清单由 `tools/compress-embedded-assets.cs` 生成，含两行头部注释、正文按 `bundle` 与 `path` 序数排序；不得手改。改负载后重跑脚本并审查差异。

## 新增 bundle

1. 把原始负载放进 `Assets/<bundle>/`（需要放在别处时，用脚本的 `--fonts-source` 这类源目录参数指向它；驱动包与字体就是这种形态，原始负载留在 `reference/` 下，只在 `Assets/` 提交帧）。
2. 在 `tools/compress-embedded-assets.cs` 的 `specs` 表加一条 `BundleSpec`：键、目标目录、源目录（默认同目标目录）、负载匹配模式、`Generated` 与 `Optional`。需要精确挑选文件、改写负载名或直接从 zip 取文件时，再传 `Payloads` 表：`PayloadSource(负载名, 源相对路径, 归档名)`，`归档名` 为源目录内的 zip 文件名。
3. 在 `UotanStudio.Assets.csproj` 加一组 `<EmbeddedResource Include="Assets\<bundle>\*.zst"><LogicalName>assets/<bundle>/%(Filename)%(Extension)</LogicalName><WithCulture>false</WithCulture></EmbeddedResource>`。
4. 运行 `dotnet run tools/compress-embedded-assets.cs` 写出 `<path>.zst` 与 `manifest.tsv`。
5. 审查 `Assets/manifest.tsv` 的差异：新增行、长度、SHA-256 与 `origin`。

五步之外还要把新键补进 `AssetNames` 与 `AssetCatalog`，否则消费方拿不到对应的 `AssetBundle`。同样的流程与常见故障见 `skills/embedded-asset-bundles/SKILL.md`。

## MagicGen 引导顺序

`firmware/magic` 的帧由外部生成器产出，帧缺失时按以下顺序恢复，不要跳过第一步：

1. 先让本工程可构建（`manifest.tsv` 存在且至少有一个 `*.zst` 帧即可通过 csproj 的 `ValidateBundledAssets` 粗粒度门禁），因为 `UotanStudio.Firmware.MagicGen` 依赖本工程才能运行。
2. 运行 MagicGen 重生成 `Assets/firmware/magic/CompiledMagicData.zst`。
3. 再跑 `dotnet run tools/compress-embedded-assets.cs` 把清单里的对应行更新为新的长度与 SHA-256。

清单与帧的逐行双射由生成脚本的 `--check` 模式与清单测试守护，csproj 的 `ValidateBundledAssets` 只判断“清单在且至少有一个帧”。

## 失败语义与上限

- 清单不可解析时抛 `InvalidDataException`：非 UTF-8、列数不是六、bundle 或文件名不可用、长度为 0 或超过 256 MiB、SHA-256 不是 64 位小写十六进制、编码不是 `zstd`、`origin` 不是 `source`/`generated`、`(bundle, path)` 重复、条目超过 4096 条、单 bundle 声明总长超过 1 GiB。
- 取条目走 `AssetBundle` 的索引器：文件不在包内抛 `KeyNotFoundException`。未随包的 bundle 走 `Materialize`/`Extract` 抛 `InvalidOperationException`；`Extract` 的目标目录不存在抛 `DirectoryNotFoundException`，目标路径为空抛 `ArgumentException`。
- 帧缺失、被截断、不是 zstd 帧、解出长度与清单不符或摘要与清单不符，一律抛 `InvalidDataException`，消息含资源名、期望值与实际值。
- 上限常量在 `AssetLimits`：单条 256 MiB、单 bundle 1 GiB、清单 1 MiB。

## 文档与验证

- `GenerateDocumentationFile` 与 `TreatWarningsAsErrors` 都已开启：公共类型与成员必须有中英双语 XML 注释，`CS1591` 会直接失败。说明性内容写进 XML 注释，不用行间 `//` 注释。
- 定向验证：

```text
dotnet build UotanStudio.Assets/UotanStudio.Assets.csproj --nologo
dotnet run tools/compress-embedded-assets.cs -- --check
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Assets --minimum-expected-tests 1
```