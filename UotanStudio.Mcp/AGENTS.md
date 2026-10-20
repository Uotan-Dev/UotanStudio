# UotanStudio.Mcp 项目指令

在本目录内新增或修改 MCP 工具、资源、传输前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目只引用 `UotanStudio.Firmware`、`UotanStudio.Comm`、`UotanStudio.Delivery`、`UotanStudio.Projects` 与 `ModelContextProtocol`；不得引用 Avalonia 宿主 `UotanStudio` 或 `UotanStudio.Desktop`。依赖方向是宿主/Desktop 指向本项目。
- 设备能力只经 `IDeviceManagerGateway`；无桌面宿主时 `HeadlessDeviceManagerGateway` 使用 Comm 的 `DeviceController`。不得在本项目自行打开 socket、串口或 USB 句柄。
- 启动分发在 `UotanStudio.Desktop/Program.cs`：`--mcp-headless` 走 stdio 并在启动时注册固件格式处理器，`--mcp` 走桌面伴随模式；`McpServerConfig.Parse` 读取 `--mcp-root`（缺省为进程当前目录）与 `--allow-flash`。

## 工具契约

- `Tools/PluginToolCatalog.cs` 是工具目录的唯一真源：新增工具要同时加一条描述符与对应工厂，不要手改 `BuildTools` 的追加逻辑。当前目录共 48 条，其中 6 条 `FlashGated`；`BuildTools` 只在 `AllowFlash` 时注册它们，工厂内不得再藏第二道门控。
- 工具名使用唯一 snake_case；`ReadOnly`、`Destructive`、`FlashGated` 必须与真实行为一致。`NodeTypeKey` 必须能由 `NodeFactoryRegistrar.BuiltInTypeKeys` 解析。
- 所有工具委托必须经 `Tools/ToolGuard.cs`：客户端取消报取消，失败经 `ToolResult.FromException` 保留异常类型与消息，并设置 `IsError`；不得吞异常报成功。
- 结果同时写文本信封、`StructuredContent` 与 `IsError`。负载必须是已在 `McpToolJsonContext` 注册的具体记录类型，经 `ToolJson.Data` 序列化；禁止反射序列化。
- `inputSchema` 不得出现 `cancellationToken`、`progress`、`context`、`sessionId` 等由 SDK 绑定的 ambient 参数；它们只作为委托末参出现。
- 客户端传入的所有路径必须经 `McpServerHost.Sandbox` 的 `Resolve(mustExist)` 或 `ResolveOutput`；不要直接对客户端字符串调用 `File.*`。沙箱在 Windows 上忽略大小写，根目录自身算包含，兄弟前缀必须拒绝。

## 传输与宿主

- stdio 与 HTTP 共用 `McpServerHost.BuildTools` / `BuildResources` / `CreateOptions`，避免两套工具集漂移。HTTP 只绑定 `IPAddress.Loopback`，非回环 `Origin` 返回 403，任意路径映射到同一 MCP 端点；请求头上限 16 KiB、请求体 16 MiB、空闲 5 分钟。
- 伴随模式由桌面宿主注入设备网关、交付工具准备器与 `IDeliverySigningKeyProvider`；`delivery_build` 的签名能力通过构造参数传进单个 host，不写进程级静态状态。无签名提供器时签名计划应如实失败。
- 资源正文不得回显沙箱绝对根路径；审计经 `IMcpToolCallAudit` 按实例注入，过滤器只观察，不得改变工具结果。参数原样进入日志，新增工具要评估是否会记录密码或密钥路径。

## AOT 与验证

- 按 NativeAOT 约束写代码：`McpServerTool.Create` 显式注册，不用特性反射；JSON 走源生成；HTTP 请求体用 `McpJsonUtilities.DefaultOptions`。不得用 `NoWarn` 压掉裁剪警告。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。
- 定向验证：

```text
dotnet build UotanStudio.Mcp/UotanStudio.Mcp.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.McpToolCatalogTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.McpSandboxTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.McpHttpExposureTests --minimum-expected-tests 1
```

- 这些类属于 `"MCP tool generation"` collection，必须保持串行。NativeAOT 下 `tools/list` 数量未按当前源码重测，不能写成已验证。