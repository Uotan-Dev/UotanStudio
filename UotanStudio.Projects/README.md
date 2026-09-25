# UotanStudio.Projects

UotanStudio 工程／解决方案生命周期类库，面向 .NET 10，启用 NativeAOT 兼容分析。没有运行时 NuGet 包或其他项目引用，不依赖 Avalonia、Dock、Nodify、固件处理器及桌面全局状态。

模型与服务使用 `UotanStudio.Services.Projects` 命名空间，程序集为 `UotanStudio.Projects`。JSON 字段、`.uotan`／`.uotanfw`／`.uotannode` 格式及目录约定构成当前持久化契约。`UotanStudio` 应用程序集为其中 16 个公共类型提供类型转发；该转发未做过第三方二进制调用方的兼容验证。新调用方直接引用本库。

## 独立使用

```csharp
using UotanStudio.Services.Projects;

var session = new ProjectSession();
await session.CreateNewSolutionAsync(solutionPath, cancellationToken);

var data = (ProjectDataWorkspace)session.Workspace;
data.Settings.ErofsCompLevel = 6;
data.Workflow.Nodes.Add(new NodeDto { InstanceId = "node-1", TypeKey = "Data.FileRead" });
await session.SaveAsync(cancellationToken: cancellationToken);

await session.AddSubProjectAsync(subProjectPath, cancellationToken);
await session.SwitchSubProjectAsync(subProjectName, cancellationToken: cancellationToken);
await session.SaveAsAsync(copyPath, cancellationToken);
await session.ExportAsync(archivePath, cancellationToken);
await session.CloseAsync(cancellationToken: cancellationToken);
```

每个会话默认独立持有 `ProjectContext`、`ProjectHistory` 和 `ProjectDataWorkspace`。数据主机无需节点编辑器，保存／重新读取时保留未知节点 DTO 和缺失镜像的描述；固件检测、文件导入和实际编辑器保存需要宿主提供 `IProjectWorkspace`。数据主机的 `ImportFilesAsync` 明确拒绝未经定义的导入，而不是假定文件格式。

应在会话空闲时编辑数据，然后等待生命周期方法完成；不要在操作过程中直接修改上下文或 DTO 集合。库没有后台线程、定时器或文件监听器。它不主动恢复上一次桌面工程。

## 宿主适配

- `IProjectWorkspace` 聚合数据、文档保存、文件导入三个职责接口。`PreserveMissingImages` 与 `ChangeProjectRoot` 显式提供缺失镜像策略和根目录通知，核心不识别具体宿主类型。`HasNodeEditor` 仅为兼容存在，核心保存不检查它。
- `BeforeProjectChangeAsync` 在项目操作门外执行，可等待／保存当前工程并询问用户；返回 `false` 会终止切换。回调不得递归启动另一次项目变更。
- 项目变更确认单独串行化，持久化／导出／关闭使用同一会话操作门。等待和复制支持取消；自动保存重叠请求跳过。进入最终发布后不在目录交换中途响应取消。
- `Changed` 在成功操作后同步通知，`ProjectContext.Changed` 通知低层上下文变化；宿主决定如何转发消息、同步外部环境和刷新界面。事件处理程序应快速完成且不抛异常。
- 会话保留调用者的同步上下文，适配器可在 UI 线程捕获和恢复数据；普通存储 I/O 不需要 UI。宿主自己的长任务由宿主管理取消。`ImportFilesAsync` 的执行策略由宿主决定，不宣称所有宿主 I/O 都由会话门控制。
- `ProjectDiagnostics.Message` 是可选的进程级日志通道，不持有项目状态；长期订阅由宿主负责解除。桌面兼容层为进程生命周期订阅一次。
- 库只计算 `.uotan/metadata` 目录，不读取或设置固件库的全局元数据根。桌面端负责桥接。

`SwitchSubProjectAsync` 默认先保存当前项目内容并保留已有页面记录，再应用目标项目设置和镜像。桌面门面关闭这一步额外保存，保存范围由宿主的自动保存策略决定。`CloseAsync` 默认经过宿主确认后清理项目、历史和数据主机状态；需要落盘时传 `persistProject: true`。已由桌面关闭流程确认并完成保存时，使用 `confirmChange: false` 避免再次提示。关闭不会删除磁盘工程。

数据主机中的文档路径按指定根重新计算，另存为保持文档身份不变；桌面文档的路径由桌面适配器捕获。工作流执行、未知节点实例化策略、Dock 和实际文档关闭都属于宿主。

## 持久化保证和边界

- JSON 使用源生成序列化，读取和写入上限均为 32 MiB，内容不变不重写，临时文件原子替换。读取磁盘快照期间允许对该文件做原子替换；取消以 `OperationCanceledException` 传播。
- 低层 `SolutionPersistenceService`／`SolutionSettingsService` 的契约是失败返回 `false`／`null`；生命周期操作检测写入失败并抛异常，历史写入失败也向上传播。读取失败及缺失内容按宽容加载策略处理，本库不等于完整的工程修复服务。
- 创建、普通保存、新增／导入子项目先暂存待发布的元数据，再批量发布；捕获到的中途发布失败恢复已发布文件，回滚自身失败时保留备份并报告组合异常。准备阶段可能留下空目录；不提供断电恢复或跨进程事务隔离。编辑器保存和固件大文件操作不属于该元数据事务。
- 另存为复制全部子项目和元数据，保留目标不冲突文件，准备完成后发布；捕获到的发布失败回滚。拒绝目录重叠、链接和源根外共享子项目。复制有界且可取消。磁盘空间开销包含准备副本和已有目标的备份。
- ZIP 导出语义：导出解决方案数据根目录，跳过 `.git`、`.deleted`、输出自身和临时输出；拒绝链接并检测复制期间文件变化。它不会自动把数据根之外的顶层 `.uotan` 文件加入压缩包。需要可重新打开的工程包时，使用独立的 `ExportPackageAsync`／`ImportPackageAsync`。
- 历史数据可在无执行工厂时加载和重存；此时不虚构可执行撤销动作。宿主工厂只负责重建动作。
- 不提供跨进程锁、多桌面解决方案并行、崩溃恢复、物理工程目录重命名／永久删除或对外发布 SDK。桌面显式绑定单一 Shell 并校验释放归属；独立会话隔离不代表应用中的其他单例已经支持多窗口。

## 验证

`UotanStudio.Tests/Projects` 验证独立生命周期、另存为边界、取消、历史格式读取和路径身份，与桌面兼容测试统一由 `UotanStudio.Tests` 运行。测试使用独立会话和纯数据工作区，不初始化 Avalonia。真实 Avalonia 窗口验证见 `tools/ProjectLifecycleSmoke`。

## 边界与操作契约

- `session.State` 返回只读目录快照。`CurrentProjectPath` 从上下文读取，其 `internal` setter 只作为兼容通道；宿主通过生命周期方法变更路径。目录解析完成后才发布字段，失败保留先前状态。
- `RenameSubProjectAsync` 修改解决方案引用的显示名称，保留物理目录、文件名和编辑器身份；`RemoveSubProjectAsync` 仅移除引用，不删除文件，拒绝移除最后一个子项目。两者经过会话变更确认和互斥门。
- `ProjectFiles` 提供有界临时文件复制、不覆盖移动及 `_1` 后缀命名策略。桌面镜像导入、容器导入、导出和删除移动复用这些原语；格式检测、确认、界面集合和历史更新仍在桌面。
- 完整工程包包含顶层 `.uotan` 和同名数据目录。导入只接受不存在的目标目录，在旁边展开并验证后发布；中央目录预检上限 32 MiB、条目上限 100000、名称总量 8 Mi 字符、展开总量 64 GiB，拒绝越界／重复路径和链接。缺失或位于数据根外的子项目不能打包。工程包不携带 `.git`、`.deleted` 和源机器的可执行撤销历史；导入后历史清空。外部文档内容不保证包含在工程包内。
- `ProjectHistory.RecordAsync`、`UndoAsync`、`RedoAsync` 和 `ReconstructAsyncOperation` 支持可等待动作；成功后才移动历史栈，异常或取消保留原栈。动作执行期间拒绝历史变更；历史对象由宿主的同一同步上下文访问，不提供任意线程并发保证。优先使用会话的撤销／重做入口，以共用项目操作门。同步 API 同样存在，遇到异步条目必须使用异步入口。
- 本库的服务能力只经桌面 `ProjectOperationsService` 兼容门面暴露，不新增菜单项，也不改变编辑菜单的撤销目标。
