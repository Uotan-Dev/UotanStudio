# UotanStudio.Firmware.MagicGen 项目指令

在本目录内改 magic(5) 解析、生成器或语料前读这份文件。项目是构建期控制台工具，不是运行时依赖；仓库级规则见根 `AGENTS.md`。

## 边界与产物

- 本工具生成 `UotanStudio.Firmware/Core/Magic/Generated/CompiledMagicData.g.cs`，供固件库在运行时读取编译进程序集的规则表。`UotanStudio.Firmware` 不得反向引用本工程，生成物是唯一交界。
- 不要手改 `CompiledMagicData.g.cs`；改解析器、发射器或语料后重新生成，并把生成物的差异一起审查。
- `corpus/` 是 vendored 的 `file/file` 上游 magic 语料，`corpus/LOAD_ORDER.txt` 的顺序取自上游 `magic/Makefile.am` 的 `MAGIC_FRAGMENTS`，决定同一输入的规则优先级。不得为“看起来更整齐”重排语料或 manifest。
- 语料与上游 file(1) 的 BSD-4-Clause 许可说明保留在 `corpus/README.md`；新增或替换语料要保留来源、版本与许可信息。

## 生成器行为

- `Magic5Parser` 只覆盖当前支持的 magic(5) 子集；不支持的 range、DER、indirect、transform、unknown type 与 malformed 片段必须计入跳过统计，不能静默丢弃或猜测语义。
- `Emitter` 的输出布局（序列化 blob、顶层规则数、base64 分块、`partial class CompiledMagicData`）是固件读取端的契约；调整时要同步固件侧读取代码与测试。
- `--smoke` 用于快速验证内置样本，`--files <path...>` 用于查看单文件识别结果；普通运行会读取整个 corpus 并写生成文件。不要把真实设备或仓库外大语料作为默认生成输入。
- 生成文件带 `DateTime.UtcNow` 时间戳，重生成必然产生该行差异；审查时只看规则数、blob 与跳过统计，不要为了消除时间戳差异手改生成文件。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。`Directory.Build.props` 仍对本工程启用 AOT/裁剪分析器，构建要保持零新增警告。

## 验证

```text
dotnet build UotanStudio.Firmware.MagicGen/UotanStudio.Firmware.MagicGen.csproj --nologo
dotnet run --project UotanStudio.Firmware.MagicGen/UotanStudio.Firmware.MagicGen.csproj -- --smoke
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.FileMagicTests --minimum-expected-tests 1
```

生成器自身的完整重生成与 `CompiledMagicData.g.cs` 差异属于人工审查步骤；CI 的解决方案构建只保证生成物可编译，不证明语料覆盖完整。