# UotanStudio.RegexInfer 项目指令

在本目录内修改正则推导、排序或候选契约前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界与行为

- 本项目是零依赖的确定性正则推导库，命名空间为 `UotanStudio.RegexInfer`；不要引入 UI、文件系统、网络或固件依赖，也不要依赖宿主状态。
- 输入是源文本、正例与可选负例；输出是候选正则及证据。只有通过 .NET 正则引擎编译、并精确提取全部正例的候选才会返回；仍然匹配负例的候选不能被静默丢弃，必须带非零 `NegativeLeaks` 返回，由调用方判断。
- 同一输入与同一 `RegexInferenceOptions` 必须产生相同候选、相同顺序与相同分数。不要引入字典遍历顺序、当前文化、随机数或时间依赖。
- 不要把选项 XML 当成已经生效的保护：`MaxNodes` 当前没有被读取；`TopResults`、`MaxOutputLength`、`MaxTraceLength` 只参与评分或没有限制搜索/长度；`Ranker.Validate` 与 `InferredRegex.TryCreateCompiled` 都使用无 `MatchTimeout` 的 `new Regex(pattern)`。要补预算或超时，必须同时改验证与编译两处并补测试，不能只改注释。
- `PreferAnchors` 只在每条轨迹都消费完整输入时加 `^`/`$`；`CoversWholeInput` 的这个前提不能被绕过。
- 正例按 Ordinal 定位；正例为空或不在源文本中时返回空列表。公共面只保留 `RegexInfer`、`InferredRegex`、`RegexInferenceOptions`、`RegexInferenceResult`、`CharClassKind`；`PatternTrace`/`PatternTrie` 保持 internal。
- 候选的 `PositiveMatches` 必须等于 `PositiveTotal`，`Confidence` 必须在 [0,1] 并随排序递减；`TryCreateCompiled` 的失败只表示宿主正则引擎拒绝了本库认为合法的模式，不能把它当普通输入错误吞掉。

## 文档与验证

- csproj 设置 `GenerateDocumentationFile` 与 `TreatWarningsAsErrors`；公共类型与成员必须有中英双语 XML 注释，缺注释直接构建失败。说明性内容写 XML 注释。
- 定向验证：

```text
dotnet build UotanStudio.RegexInfer/UotanStudio.RegexInfer.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.RegexInfer.RegexInferContractTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.RegexInfer.RegexInferCandidateInvariantTests --minimum-expected-tests 1
```

- 当前没有统一的正则超时或 ReDoS 兜底；在补上之前，不要声称对所有输入都线性、有界或对所有 .NET 版本都等价。