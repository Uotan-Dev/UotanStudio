# UotanStudio.Assets

内嵌二进制资源的统一存储与门面，根命名空间 `UotanStudio.Assets`。构建期把各 bundle 的 zstd 帧与 `manifest.tsv` 内嵌进程序集；运行期由 `AssetCatalog` 暴露资源包，`AssetStore` 负责解码、校验、物化与抽取。本库只引用 `UotanStudio.Hash` 与 `UotanStudio.Compress`，不做网络 I/O。

## 职责

- 保存随包的二进制负载：MiSans 字形、OpenHarmony 采集库与控制代理、八套厂商 Windows 驱动包、卡刷更新器、magic(5) 规则表，另有可选的 scrcpy 服务端 jar。
- 解析内嵌清单，把六列行模型化成 `AssetBundle`/`AssetEntry`，并给每个 bundle 一个只覆盖自身行的摘要。
- 解码 zstd 帧并按清单校验解出长度与 SHA-256 后再交出字节。
- 把 bundle 物化到 `%TEMP%` 缓存目录，或抽取到调用方指定的目录。
- 不负责负载的来源、许可证与格式语义：来源索引见 `Assets/README.md` 与各子目录文档。

## 公共 API

| 类型 | 成员 | 语义 |
| --- | --- | --- |
| `AssetCatalog` | `Fonts` | `fonts` bundle：`MiSans-Regular.ttf` 与 `MiSans-Bold.ttf`，启动时解码进内存。 |
| `AssetCatalog` | `OhosDevice` | `ohos/device` bundle：UI Test 采集库与控制代理。 |
| `AssetCatalog` | `RockusbDriver` | `drivers/rockusb` bundle：`rockusb.inf`、`rockusb.cat`、`rockusb.sys`。 |
| `AssetCatalog` | `MediaTekVcomDriver` / `MediaTekFullDriver` / `GoogleUsbDriver` / `RockchipAdbDriver` / `QualcommQudDriver` / `SilabsCp210xDriver` / `FtdiCdmDriver` | 其余七套厂商驱动包，bundle 为 `drivers/<packageId>`；包内目录用 `__` 编码，消费方还原。 |
| `AssetCatalog` | `DeliveryUpdater` | `delivery` bundle：`update-binary-arm64`。 |
| `AssetCatalog` | `MagicRules` | `firmware/magic` bundle：`CompiledMagicData`。 |
| `AssetCatalog` | `ScrcpyServer` | 可选 `scrcpy` bundle：`scrcpy-server.jar`；仓库不分发该二进制时 `IsPresent` 为 `false`。 |
| `AssetCatalog` | `Bundles` | 清单声明的全部 bundle，按声明顺序；未随包的 bundle 也在其中，只是为空。 |
| `AssetBundle` | `Key` | bundle 键，同时是资源根下的相对目录，例如 `ohos/device`。 |
| `AssetBundle` | `Entries` | 条目，按 `Path` 序数排序。 |
| `AssetBundle` | `Digest` | 规范行（键、路径、长度、摘要）的小写 SHA-256；未随包时为空串；只覆盖本 bundle 的行，其它 bundle 变化不会让它失效。 |
| `AssetBundle` | `IsPresent` | 本次构建是否至少有一个条目。 |
| `AssetBundle` | `this[string path]` | 取包内指定文件名的条目；文件不存在抛 `KeyNotFoundException`。 |
| `AssetEntry` | `Bundle` / `Path` / `Length` / `Sha256` / `Encoding` | 清单一行：所属 bundle 键、包内文件名（不含分隔符）、解压字节长度、解码字节的小写十六进制 SHA-256、编码（当前只有 `AssetEncoding.Zstd`）。 |
| `AssetStore` | `Read(AssetEntry)` | 解码并校验一个条目，返回新数组；数组归调用方。 |
| `AssetStore` | `Read(AssetBundle, string)` | 同上，按 bundle 与包内文件名取条目。 |
| `AssetStore` | `Materialize(AssetBundle, CancellationToken)` | 把 bundle 的每个文件写入缓存目录并返回该目录；重复调用复用目录，只重写长度与清单不符的文件。 |
| `AssetStore` | `Extract(AssetBundle, string, CancellationToken)` | 把 bundle 的每个文件写入必须已存在的目录，按清单顺序返回文件名。 |

`AssetNames` 提供稳定的键与文件名常量：`Bundles.Fonts`/`OhosDevice`/`RockusbDriver`/`MediaTekVcomDriver`/`MediaTekFullDriver`/`GoogleUsbDriver`/`RockchipAdbDriver`/`QualcommQudDriver`/`SilabsCp210xDriver`/`FtdiCdmDriver`/`DeliveryUpdater`/`MagicRules`/`ScrcpyServer`，`Fonts.Regular`/`Fonts.Bold`、`Delivery.Updater`、`Magic.Rules`、`Scrcpy.ServerJar`。

## 用法

读取单个条目：

```csharp
using UotanStudio.Assets;

var regular = AssetStore.Read(AssetCatalog.Fonts, AssetNames.Fonts.Regular);
var bold = AssetCatalog.Fonts[AssetNames.Fonts.Bold];
var boldBytes = AssetStore.Read(bold);
```

物化到缓存目录：

```csharp
using UotanStudio.Assets;

var cancellationToken = CancellationToken.None;
var directory = AssetStore.Materialize(AssetCatalog.OhosDevice, cancellationToken);
foreach (var entry in AssetCatalog.OhosDevice.Entries)
{
    Console.WriteLine(Path.Combine(directory, entry.Path));
}
```

抽取到指定目录：

```csharp
using UotanStudio.Assets;

var staging = Path.Combine(Path.GetTempPath(), "UotanStudio", "rockusb-driver");
Directory.CreateDirectory(staging);
var written = AssetStore.Extract(AssetCatalog.RockusbDriver, staging);
```

## 缓存布局

```text
%TEMP%/UotanStudio/assets/<bundle>/<bundle 摘要前 16 位>/
```

- bundle 键按 `/` 拆成多级目录，例如 `%TEMP%/UotanStudio/assets/ohos/device/1f3c9a2b7d4e5061/`。
- 末级目录名取 `AssetBundle.Digest` 的前 16 位十六进制：bundle 内容一变就落到新目录，无关 bundle 的缓存不受影响。
- 物化复用同一目录，只重写长度与清单不符的文件；写入经同目录临时文件加原子替换，失败时删除临时文件。
- 缓存目录是派生产物，可随时删除；下一次 `Materialize` 会重建。

## 异常语义

| 情形 | 异常 |
| --- | --- |
| `bundle` 或 `entry` 参数为 `null` | `ArgumentNullException` |
| `Extract` 的目标目录参数为空或空白 | `ArgumentException` |
| `Extract` 的目标目录不存在 | `DirectoryNotFoundException` |
| bundle 未随包就调用 `Materialize` 或 `Extract` | `InvalidOperationException` |
| 包内没有该文件名 | `KeyNotFoundException` |
| 帧缺失、被截断、不是 zstd、解出长度或摘要与清单不符 | `InvalidDataException` |

异常消息中英双语，并带上资源名、期望值与实际值。长度与摘要校验只发生在 `AssetStore` 内部，调用方不要重复实现。

## 上限常量

上限由 `AssetLimits`（internal）声明，并由清单解析与帧读取执行：

| 常量 | 值 | 含义 |
| --- | ---: | --- |
| `MaximumManifestBytes` | 1 MiB | `manifest.tsv` 字节上限 |
| `MaximumEntries` | 4,096 | 清单条目数上限 |
| `MaximumBundleKeyLength` | 128 | bundle 键（含 `/` 的完整相对目录）长度上限 |
| `MaximumPathLength` | 128 | bundle 的单个目录段与包内文件名长度上限 |
| `MaximumEntryBytes` | 256 MiB | 单条解压长度上限，也是单个内嵌帧的字节上限 |
| `MaximumBundleBytes` | 1 GiB | 单个 bundle 声明总长上限 |

资源布局、清单六列与新 bundle 的接入流程见 `AGENTS.md`；资源来源与许可证索引见 `Assets/README.md`。