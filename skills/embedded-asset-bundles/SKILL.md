---
name: embedded-asset-bundles
description: >-
  Add, update or remove a zstd-compressed binary bundle embedded through
  UotanStudio.Assets. Use when a project needs to ship new non-source payloads
  (fonts, native libraries, vendor drivers, update binaries, generated tables),
  when regenerating Assets/manifest.tsv, or when an embedded asset lookup fails
  because the bundle key, frame name or manifest row is out of sync.
license: MIT
---

# 内嵌资源包增删

本 skill 定义往仓库里加一批随程序集分发的二进制负载的流程：负载 → `BundleSpec` → csproj 的 `EmbeddedResource` → 帧与清单 → 消费方经 `AssetCatalog`/`AssetStore` 读取。所有项目都不再自己内嵌资源，`UotanStudio.Assets/Assets/manifest.tsv` 是唯一清单。

## 何时使用

- 需要随包分发非源码负载：字体、原生库、厂商 Windows 驱动、卡刷更新器、生成式规则表。
- 已有 bundle 换了负载或换了来源，需要重生成帧与清单。
- 消费方报 `KeyNotFoundException`、`InvalidOperationException` 或清单校验失败，怀疑键、帧名或清单行不同步。

## 入口

```text
UotanStudio.Assets/Assets/<bundle>/<path>.zst     压缩帧，与原始负载同目录
UotanStudio.Assets/Assets/manifest.tsv            唯一清单，脚本生成，不手改
UotanStudio.Assets/UotanStudio.Assets.csproj      逐 bundle 的 EmbeddedResource 与校验门
UotanStudio.Assets/AssetNames.cs                  稳定键与文件名常量
UotanStudio.Assets/AssetCatalog.cs                对外暴露的 AssetBundle 属性
tools/compress-embedded-assets.cs                 帧与清单生成器（--check 只校验）
```

先确认门面已经在位：

```powershell
Test-Path UotanStudio.Assets/AssetCatalog.cs
Test-Path tools/compress-embedded-assets.cs
```

## 五步流程

### 1. 放负载

原始负载放 `UotanStudio.Assets/Assets/<bundle>/`，压缩帧与它同目录。原始负载在别的目录（`reference/` 下的字体与驱动包、生成器产出的规则表）时不复制进 Assets，只让 `BundleSpec` 指向源目录。资源 path 只能是纯文件名，自带目录结构的负载用 `__` 编码一级目录（`x64__usb2ser.sys` 代表 `x64/usb2ser.sys`），由消费方物化时还原。

### 2. 加 BundleSpec

在 `tools/compress-embedded-assets.cs` 的 `specs` 表加一条，参数依次是键、目标目录、源目录（`null` 表示同目标目录）、负载匹配模式、`Generated`、`Optional`：

```csharp
new("drivers/rockusb", Path.Combine(driversTarget, "rockusb"), null, "rockusb.*",
    Generated: false, Optional: false),
```

需要精确挑选文件、改写负载名或直接从归档取文件时再传 `Payloads`：`PayloadSource(负载名, 源相对路径, 归档名)`，归档名是源目录内的 zip 文件名。

```csharp
new("drivers/ftdi-cdm", Path.Combine(driversTarget, "ftdi-cdm"),
    Path.Combine(driverSource, "ftdi-cdm"), "*", Generated: false, Optional: false,
    [
        new("Image__FTDIBUS.inf", "Image/FTDIBUS.inf", "CDM-v2.12.36.20-Universal-x64.zip"),
    ]),
```

- `Generated: true` 只给“负载本身由生成器产出”的 bundle（当前是 `firmware/magic`），脚本只索引、不压缩。
- `Optional: true` 给仓库不一定分发的 bundle（当前是 `scrcpy`）：缺失时不报错，消费方看到 `IsPresent` 为 `false`。

### 3. 加 EmbeddedResource

在 `UotanStudio.Assets/UotanStudio.Assets.csproj` 按 bundle 加一组，`LogicalName` 必须写 `%(Filename)%(Extension)`，否则 `.zst` 后缀会被丢掉：

```xml
<EmbeddedResource Include="Assets\<bundle>\*.zst">
  <LogicalName>assets/<bundle>/%(Filename)%(Extension)</LogicalName>
  <WithCulture>false</WithCulture>
</EmbeddedResource>
```

### 4. 生成帧与清单

```powershell
dotnet run tools/compress-embedded-assets.cs
dotnet run tools/compress-embedded-assets.cs -- --check
```

### 5. 补稳定键并审查差异

新 bundle 要同时进 `AssetNames.Bundles`（以及该 bundle 的文件名常量）与 `AssetCatalog` 的属性，否则消费方拿不到 `AssetBundle`。然后审查 `Assets/manifest.tsv` 的差异：新增行的 `bundle`、`path`、长度、SHA-256 与 `origin`，并确认 `Assets/<bundle>/` 下只多出 `.zst` 帧。

## 清单契约

- 六列制表符分隔：`bundle`、`path`、解压长度、小写 SHA-256、编码、`origin`；两行头部注释，正文按 `bundle` 与 `path` 序数排序。
- 编码当前只接受 `zstd`；`origin` 只接受 `source`（帧由同目录原始负载压缩）与 `generated`（负载由生成器产出）。
- 上限在 `AssetLimits`：单条 256 MiB、单 bundle 1 GiB、清单 1 MiB；条目总数上限 4096。
- 帧缺失、截断、不是 zstd 帧、解出长度或摘要与清单不符时抛 `InvalidDataException`，消息带资源名与期望/实际值。

## 验证

```powershell
dotnet build UotanStudio.Assets/UotanStudio.Assets.csproj --nologo
dotnet run tools/compress-embedded-assets.cs -- --check
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Assets --minimum-expected-tests 1
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
```

`--check` 通过只证明帧与清单自洽；消费方能否读到条目还要看它自己的定向测试。真实设备、真实驱动暂存与图形路径不在本 skill 的证明范围内。

## 常见问题

- 消费方 `KeyNotFoundException`：清单里没有该 `(bundle, path)`，或 `path` 用了目录分隔符。资源 path 必须是纯文件名。
- 运行时找不到资源：csproj 漏了该 bundle 的 `EmbeddedResource` 组，或 `LogicalName` 没写 `%(Filename)%(Extension)`。
- `InvalidOperationException`（bundle 未随包）：帧没有提交，或该 bundle 在本次构建里没有条目；`Optional` 为 `false` 时脚本会直接失败。
- 清单差异出现整批重排：`origin` 或 `path` 变了；排序是序数的，按字节比较重排，不要手改回去。
- `ValidateBundledAssets` 通过但测试报错：csproj 门只判断“清单在且至少有一个帧”，逐行双射由 `--check` 与清单测试守护。
