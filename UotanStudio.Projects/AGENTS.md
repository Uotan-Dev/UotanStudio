# UotanStudio.Projects 项目指令

在本目录内改工程/解决方案生命周期、持久化或工程包前读这份文件。项目定位、独立用法与持久化边界见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 本项目只引用 `UotanStudio.Hash` 与 `UotanStudio.Compress`（ZIP 容器经 `Compress.Zip.ZipContainer`），不依赖 Avalonia、Dock、Nodify、固件处理器、Comm 或桌面全局状态；启用 NativeAOT 兼容分析。新增依赖前先确认它不会把宿主 UI 或固件格式拉进来。
- `VisualPlugins/` 的清单、节点/连接/资源上限与资源 SHA-256 也属于持久化契约；README 未覆盖这部分，改动前读该目录与相关测试。
- 模型与服务使用 `UotanStudio.Projects` 命名空间，程序集为同名 `UotanStudio.Projects`。本项目不提供类型转发；调用方直接引用本库并按 `UotanStudio.Projects` 解析类型，`UotanStudio.Services.Projects` 只是宿主的应用服务命名空间。
- 桌面兼容门面是宿主 `ProjectOperationsService`。不要在本库新增菜单项或改变编辑菜单的撤销目标；桌面流程、Dock 与文档关闭归宿主。宿主需要直接使用的 `VisualPluginPackage.BundleRoot`/`BundlePath`、`VisualPluginAsset.Snapshot`、`VisualPluginBundle.EnumerateScopes`、`ProjectDiagnostics.Write`、`WorkflowJsonContext`、`AtomicJsonFile`、`ProjectSession.CurrentProjectPath` 等成员保持 public；本库不向宿主开放 `InternalsVisibleTo`。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不得用 `NoWarn` 隐藏新的文档或空引用警告。

## 持久化契约

- `.uotan`、`.uotanfw`、`.uotannode` 的 JSON 字段、`FormatVersion`、`.uotan/metadata` 目录与 `nodes/<name>.uotannode` 布局是当前持久化契约。改动必须保留旧版本的宽容读取，并为读写路径补测试；读取失败返回 `false`/`null`，生命周期操作检测到写入失败时抛异常。
- JSON 使用源生成序列化；读取与写入上限均为 32 MiB，内容不变不重写，写入走临时文件原子替换。取消以 `OperationCanceledException` 传播。
- 低层 `SolutionPersistenceService` / `SolutionSettingsService` 的契约是失败返回 `false`/`null`；生命周期操作检测写入失败并抛异常，历史写入失败也向上传播。读取失败与缺失内容按宽容加载策略处理，但本库不是完整的工程修复服务。
- 保存只重写宿主 `OpenWorkflowNames` 报告的打开文档；未打开的登记项保持磁盘原样。未知节点 DTO 与缺失镜像描述在保存/重新读取时保留。
- 解决方案根不存放 `delivery.json` 之类的交付配置文件；解决方案级状态只走 `SolutionSnapshot.State`。子项目自己的 `.uotan/` 目录语义保持不变。
- 创建、普通保存、新增/导入子项目先暂存待发布的元数据，再批量发布；捕获到的中途失败恢复已发布文件，回滚自身失败时保留备份并报告组合异常。准备阶段可能留下空目录；不提供断电恢复或跨进程事务隔离。
- 另存为复制全部子项目与元数据，保留目标不冲突文件，准备完成后发布；拒绝目录重叠、链接和源根外共享子项目。复制有界且可取消。桌面镜像导入与容器导入复用 `ProjectFiles` 的有界复制/不覆盖移动原语。
- ZIP 导出与工程包语义不同：普通导出导出数据根、跳过 `.git`、`.deleted`、输出自身和临时输出；独立 `ExportPackageAsync`/`ImportPackageAsync` 才生成可重新打开的工程包。工程包预检上限为中央目录 32 MiB、条目 100000、名称总量 8 Mi 字符、展开总量 64 GiB，拒绝越界/重复路径和链接。缺失或位于数据根外的子项目不能打包。

## 会话与并发

- 每个会话默认独立持有 `ProjectContext`、`ProjectHistory` 与 `ProjectDataWorkspace`。库没有后台线程、定时器或文件监听器；在会话空闲时编辑数据，然后等待生命周期方法完成，不要在操作过程中直接改上下文或 DTO 集合。
- `BeforeProjectChangeAsync` 在项目操作门外执行，可等待/保存当前工程并询问用户；返回 `false` 会终止切换，回调不得递归启动另一次项目变更。
- 项目变更确认单独串行化；持久化/导出/关闭使用同一会话操作门。等待与复制支持取消；自动保存重叠请求跳过；进入最终发布后不在目录交换中途响应取消。
- `Changed` 在成功操作后同步通知，`ProjectContext.Changed` 通知低层上下文变化；事件处理程序应快速完成且不抛异常。`ProjectDiagnostics.Message` 是可选的进程级日志通道，不持有项目状态。
- 历史栈只在动作成功后移动；异常或取消保留原栈。动作执行期间拒绝历史变更。优先使用会话的撤销/重做入口以共用操作门，不要绕过它直接调 `ProjectHistory`。

## 验证

```text
dotnet build UotanStudio.Projects/UotanStudio.Projects.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Projects --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.ProjectOperationsBoundaryTests --minimum-expected-tests 1
```

- 测试使用独立会话和纯数据工作区，不初始化 Avalonia；真实窗口生命周期属于宿主验证，不要在本库测试里启动 Dock 或 UI。