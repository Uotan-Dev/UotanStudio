# UotanStudio.Mcp 项目指令

在本目录内新增或修改 MCP 工具、资源、传输前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目只引用 `UotanStudio.Firmware`、`UotanStudio.Comm`、`UotanStudio.Pipeline`、`UotanStudio.Net` 与 `ModelContextProtocol`；不得引用 Avalonia 宿主 `UotanStudio` 或 `UotanStudio.Desktop`。依赖方向是宿主/Desktop 指向本项目。HTTP 下载一律经 `UotanStudio.Net` 的公共 API，不在本项目内自建下载管线。
- 设备能力只经 `IDeviceManagerGateway`；无桌面宿主时 `HeadlessDeviceManagerGateway` 使用 Comm 的 `DeviceController`。不得在本项目自行打开 socket、串口或 USB 句柄。
- 启动分发在 `UotanStudio.Desktop/Program.cs`：`--mcp-headless` 走 stdio 并在启动时注册固件格式处理器，`--mcp` 走桌面伴随模式；`McpServerConfig.Parse` 读取 `--mcp-root`（缺省为进程当前目录）与 `--allow-flash`。

## 工具契约

- 对外 MCP `tools/list` 只暴露两个元工具：`search_actions` 与 `execute_action`。`Tools/PluginToolCatalog.cs` 仍是内部动作目录的唯一真源；新增内部能力要同时加描述符与工厂，不要手改外部注册逻辑。
- `McpActionRegistry` 从目录构建内部动作，并补充不增加常驻 schema 的流程/工件/计划动作。`search_actions` 只做确定性关键词/域检索，返回候选动作与输入 schema；`execute_action` 以 `JsonElement` 参数对象分发到内部工具，并经 `ToolGuard` 统一返回/错误包装。
- 动作名使用唯一 snake_case；`ReadOnly`、`Destructive`、`FlashGated` 必须与真实行为一致。`NodeTypeKey` 必须能由 `NodeFactoryRegistrar.BuiltInTypeKeys` 解析。刷写门控动作仅在 `AllowFlash` 时可发现/可执行。
- 所有工具/动作委托必须经 `Tools/ToolGuard.cs`：客户端取消报取消，失败经 `ToolResult.FromException` 保留异常类型与消息，并设置 `IsError`；不得吞异常报成功。
- 结果同时写文本信封、`StructuredContent` 与 `IsError`。负载必须是已在 `McpToolJsonContext` 注册的具体记录类型，经 `ToolJson.Data` 序列化；禁止反射序列化。
- 大于 32 KiB 的 action 输出通过 `McpArtifactStore` 持久化到沙箱 `.uotanstudio-mcp/artifacts/`，返回 `ArtifactRef`（id/path/size/sha256/preview），不得泄露沙箱外绝对路径。
- `inputSchema` 不得出现 `cancellationToken`、`progress`、`context`、`sessionId` 等由 SDK 绑定的 ambient 参数；它们只作为委托末参出现。
- 客户端传入的所有路径必须经 `McpServerHost.Sandbox` 的 `Resolve(mustExist)` 或 `ResolveOutput`；不要直接对客户端字符串调用 `File.*`。沙箱在 Windows 上忽略大小写，根目录自身算包含，兄弟前缀必须拒绝。

## 传输与宿主

- stdio 与 HTTP 共用 `McpServerHost.BuildTools` / `BuildMetaTools` / `BuildActionRegistry` / `CreateOptions`，避免两套暴露面漂移。HTTP 只绑定 `IPAddress.Loopback`，非回环 `Origin` 返回 403，任意路径映射到同一 MCP 端点；请求头上限 16 KiB、请求体 16 MiB、空闲 5 分钟。
- 伴随模式由桌面宿主注入设备网关、交付工具准备器与 `IDeliverySigningKeyProvider`；`delivery_build` 的签名能力通过构造参数传进单个 host，不写进程级静态状态。无签名提供器时签名计划应如实失败。
- 资源正文不得回显沙箱绝对根路径；审计经 `IMcpToolCallAudit` 按实例注入，过滤器只观察，不得改变工具结果。参数原样进入日志，新增动作要评估是否会记录密码或密钥路径。

## AOT 与验证

- 按 NativeAOT 约束写代码：`McpServerTool.Create` 显式注册，不用特性反射；JSON 走源生成；HTTP 请求体用 `McpJsonUtilities.DefaultOptions`。不得用 `NoWarn` 压掉裁剪警告。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。
- 定向验证：

```text
dotnet build UotanStudio.Mcp/UotanStudio.Mcp.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.Net11McpSearchTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Architecture --minimum-expected-tests 1
```

- 工具目录、沙箱与 HTTP 暴露曾由 `McpToolCatalogTests`、`McpSandboxTests`、`McpHttpExposureTests` 覆盖，这些类已随测试精简删除；`"MCP tool generation"` collection 也不复存在。当前只有 `Net11McpSearchTests` 的检索契约测试，工具目录/沙箱/HTTP 暴露没有自动化门禁，改动须人工评审。NativeAOT 下 `tools/list` 数量未按当前源码重测，不能写成已验证。