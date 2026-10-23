# UotanStudio.Tests 项目指令

在本目录内写测试前读这份文件，并读被测项目根目录的 `AGENTS.md` 以确认该项目的边界与不变量。仓库级规则在根 `AGENTS.md`，实测数字与门禁覆盖缺口在根 `VERIFICATION.md`。

## 宿主与命令

- 本目录是唯一可执行的 xUnit v3 测试宿主，跑 Microsoft.Testing.Platform（由根 `global.json` 选定）。项目级与解决方案级入口分别是 `--project` 与 `--solution`；测试按目录分组，固件测试在 `Firmware/` 下，架构门在 `Architecture/` 下（`UotanStudio.Tests.Architecture` 命名空间）。
- 架构门覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员；新增项目或移动目录后必须同步 `HostLayeringGuardTests`、`ProjectReferenceDirectionGuardTests`、`SolutionMembershipGuardTests` 的清单。
- 定向跑优先用原生简单过滤器 `--filter-class`、`--filter-method`、`--filter-namespace`、`--filter-trait`，并且每条都带 `--minimum-expected-tests 1`，防止选择器失效时把"零测试"当成成功：

  ```text
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Shell.ChecksumServiceTests --minimum-expected-tests 1
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-method UotanStudio.Tests.Shell.ChecksumServiceTests.ComputeAsync_KnownVectors_ProducesStandardDigests --minimum-expected-tests 1
  dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter "FullyQualifiedName~ChecksumServiceTests" --minimum-expected-tests 1
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
- 保留的 `CS0618` 抑制只用于覆盖旧的整镜像内存型构建 API；生产代码必须走文件路径或流式重载。不要用项目级 `NoWarn` 掩盖新的生产警告、空引用、裁剪或异步问题。
- 共享进程全局状态（节点类型注册表、语言服务、`WeakReferenceMessenger`、静态缓存）的测试要显式登记并清理，不能依赖别的类先跑过；单跑一个类必须给出与全量一致的结果，否则算隔离缺陷而不是偶发。
- 会因首次生成而泄漏环境参数的工具目录测试固定在同一个 collection（`"MCP tool generation"`）里串行。

## 环境门控

跨平台、WSL、QEMU、真实设备与真实语料用例缺环境时明确跳过并写出原因。按变量逐个开启，CI 一个都不设：

- `UOTAN_MAGISKBOOT_ORACLE` 指向 magiskboot 可执行文件。
- `UOTAN_SELINUX_POLICY_SAMPLE`、`UOTAN_ELF_FIREHOSE_SAMPLE`、`UOTAN_HOS_UPDATE_APP_SAMPLE` 指向样例文件。
- `UOTAN_SAMSUNG_DAT_MERGE_ORACLE`、`UOTAN_SAMSUNG_PAYLOAD_MERGE_ORACLE`、`UOTAN_EROFS_CORPUS` 指向预言或语料目录。
- `UOTAN_HDC_MIRROR_DIRECT` 指向 HDC connect key（例如 `5XM0225C18001159`）或直连 HDC 守护进程 `host:port`（例如 `127.0.0.1:5555`），用于启用 `OhscrcpyHdcIntegrationTests`；直连 USB 在 HDC server 运行时会被 Comm 的 fail-closed 策略拒绝，跳过/失败都不算通过。

跳过原因里必须写出对应变量名。原生工具互操作（mkfs、e2fsck、fsck.erofs、fsck.f2fs 与环回挂载）和 TB 级大数据构建不由测试工程驱动，其取证在仓库外工装完成。