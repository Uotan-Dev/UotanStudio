# UotanStudio.Tests 项目指令

在本目录内写测试前读这份文件，并读被测项目根目录的 `AGENTS.md` 以确认该项目的边界与不变量。仓库级规则在根 `AGENTS.md`，实测数字与门禁覆盖缺口在根 `VERIFICATION.md`。

## 宿主与命令

- 本目录是唯一可执行的 xUnit v3 测试宿主，跑 Microsoft.Testing.Platform（由根 `global.json` 选定）。项目级与解决方案级入口分别是 `--project` 与 `--solution`；测试按目录分组，固件测试在 `Firmware/` 下，架构门在 `Architecture/` 下（`UotanStudio.Tests.Architecture` 命名空间）。
- 架构门覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员；新增项目或移动目录后必须同步 `HostLayeringGuardTests`、`ProjectReferenceDirectionGuardTests`、`SolutionMembershipGuardTests` 的清单。
- 定向跑优先用原生简单过滤器 `--filter-class`、`--filter-method`、`--filter-namespace`、`--filter-trait`，并且每条都带 `--minimum-expected-tests 1`，防止选择器失效时把"零测试"当成成功：

  ```text
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.HashBoundaryGuardTests --minimum-expected-tests 1
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-method UotanStudio.Tests.Firmware.Architecture.HashBoundaryGuardTests.ProductionSources_NoHashComputationOutsideTheHashLibrary --minimum-expected-tests 1
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~ErofsTests" --minimum-expected-tests 1
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build -c Debug --filter-namespace UotanStudio.Tests.Architecture --minimum-expected-tests 1
  dotnet test --solution UotanStudio.slnx --no-restore
  ```

- 同一命令不得把原生 `--filter-*` 与 `--filter` 或 `--filter-query` 混用。传统 `--filter` 只为既有 VSTest 过滤表达式和 CI 脚本保留。过滤后返回零条时先检查选择器，不得归为测试代码失败。
- `--no-build` 只对当前字节负责：改过生产代码后先重建再跑，否则跑的是旧 dll；共享工作树里并行会话重写 `bin/` 会伪造出大批失败，判定前先确认加载的字节与源码一致。
- 全量单轮的上限与 CI Test 步骤一致，为 15 分钟：超过就按挂死处理，先定位阻塞的 collection，不靠抬高上限。本地跑整套用同一个上限。
- 测试宿主的输出目录必须留在仓库内。重定向到仓库外会让 `FirmwareContextDependencyScannerTests.TryFindRepositoryRoot_CurrentTestHostOutput_FindsCheckedOutSolution` 找不到 `UotanStudio.slnx` 而失败，并额外产生环境跳过。
- 测试用例的临时文件、临时镜像、解压目录与诊断产物必须创建在主机 `%TEMP%`（`Path.GetTempPath()`）下，并在用例或夹具释放时删除；不得写到仓库根、盘符根、测试输出目录或 `artifacts/`。上一条只约束测试宿主输出，不约束这些临时文件。

## 写法

- 测试名用 `Method_Scenario_ExpectedBehavior`，按 Arrange/Act/Assert 组织。优先验证真实格式互操作性、失败回滚、取消与边界，而不是只断言内部对象往返。
- 异步测试用 `async Task` + `await` + `TestContext.Current.CancellationToken`。测试方法里禁止 `.Result`、`.Wait()` 与 `.GetAwaiter().GetResult()`；同步等待只能留在明确的同步基础设施边界，且不得因此触发 xUnit analyzer 报警。
- 断言的偏移与长度要从几何推导，不要从被测缓冲区自身的长度反推；`out` 参数的 `TryParse` 在拒绝分支仍可能保留已解析值，别断言零。
- 本目录不使用行间 `//` 或 `/* */` 注释。跳过原因必须来自代码里的 `Skip = "…"` 文案，夹具意图写在测试名与断言里。
- 测试不引用整镜像内存型构建 API；需要内存镜像字节时经 `InMemoryImage` 走流式重载构建。生产代码必须走文件路径或流式重载。不要用项目级 `NoWarn` 掩盖生产警告、空引用、裁剪或异步问题。
- 共享进程全局状态（节点类型注册表、语言服务、`WeakReferenceMessenger`、静态缓存）的测试要显式登记并清理，不能依赖别的类先跑过；单跑一个类必须给出与全量一致的结果，否则算隔离缺陷而不是偶发。
- 会因首次生成而泄漏环境参数的测试固定在同一个 `[CollectionDefinition(..., DisableParallelization = true)]` 里串行；现有定义（如 `"Debug bridge route"`、`"Workspace state"`）在各自的测试文件内声明，新增时要按同样方式登记。（原名 `"MCP tool generation"` 的 collection 已随测试精简删除。）
- 构建镜像并断言 inode 形态、advise 位或镜像字节的用例必须固定时间：给 `ErofsOptions.BuildTime` 一个常量，源时间早于该常量时同时用 `File.SetLastWriteTimeUtc`/`Directory.SetLastWriteTimeUtc` 固定源时间，不得依赖墙钟。构建默认把超级块纪元取作当前秒，源 mtime 早于纪元会命中 `NeedsExtendedInodeTime` 的 `timestamp < epoch` 分支而写成扩展 inode，扩展记录不写 nlink-one/dot-omitted advise 位，于是同一条用例在并行负载下会随机换成另一种布局。

## 环境门控

测试套件不包含环境变量门控或 `Assert.Skip` 跳过用例；外部样本、原生工具与硬件验证在仓库外完成，不以静默跳过代替断言。