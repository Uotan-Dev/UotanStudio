# UotanStudio.Pipeline 项目指令

在本目录内改工程/解决方案生命周期、持久化、工程包或交付产出前读这份文件。项目定位、独立用法与源码分组见同目录 `README.md`；仓库级规则（构建命令、Git 纪律、不可信输入的通用上限、format 双门）在根 `AGENTS.md`，宿主侧交付界面规则在 `UotanStudio/AGENTS.md`，不在此重复。

## 边界与可见性

- 允许引用 `UotanStudio.Assets`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash` 与 `UotanStudio.Net`；不得引用桌面宿主、界面或 `KeyLibraryService`，不依赖 Avalonia、Dock、Nodify 或桌面全局状态，启用 NativeAOT 兼容分析。新增引用前先确认它不会把宿主 UI 拉进来。
- 不声明第三方压缩或加密包：压缩与 ZIP 容器走 Compress 门面（`CompressionService`/`ZipContainer`），校验和走 Hash。
- 工具包下载、GitHub 镜像选择与 Release 元数据经 `UotanStudio.Net`（`HttpDownload`、`GitHubReleaseClient`）完成；本库不复制镜像枚举、测速或版本归一化逻辑。
- 签名私钥只经 `Delivery/Models/Signing.cs` 的 `IDeliverySigningKeyProvider` 注入。桌面侧适配在 `UotanStudio/Services/Delivery/`：`DeliveryKeyProvider`、`DeliveryPlanSerializer`、`DeliveryDynamicPartitions`。
- 命名空间按源码分组：`Projects/` 下是 `UotanStudio.Pipeline.Projects`（含 `.Schemes` 与 `.Interactions`），`Delivery/` 下是 `UotanStudio.Pipeline.Delivery`；程序集为 `UotanStudio.Pipeline`。本库不提供类型转发，调用方直接引用本库并按上述命名空间解析类型；`UotanStudio.Services.Projects` 只是宿主的应用服务命名空间。
- 交付对外入口是 `Delivery/Build/DeliveryService.cs`（公共门面，吃 `DeliveryPlan`）。宿主与 MCP 需要的构建/校验/容量/工具准备契约（`IDeliveryTools`、`DeliveryOutputService`、`DeliveryValidation`、`DeliverySuperBuilder`、`DeliveryPayloadBuilder`、`DeliverySuperCapacity`、`DeliveryPackageBuilder`、`DeliveryScriptGenerator`、`DiskSpaceEstimator`、`DeliveryPlanStore` 等）是公共契约；其余实现细节保持 internal。本库不向宿主或 MCP 开放 `InternalsVisibleTo`，只授予 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks`。
- 工程侧桌面兼容门面是宿主 `ProjectOperationsService`。不要在本库新增菜单项或改变编辑菜单的撤销目标；桌面流程、Dock 与文档关闭归宿主。宿主需要直接使用的 `VisualPluginPackage.BundleRoot`/`BundlePath`、`VisualPluginAsset.Snapshot`、`VisualPluginBundle.EnumerateScopes`、`ProjectDiagnostics.Write`、`WorkflowJsonContext`、`AtomicJsonFile`、`ProjectSession.CurrentProjectPath` 等成员保持 public。
- `VisualPlugins/` 的清单、节点/连接/资源上限与资源 SHA-256 也属于持久化契约；README 未覆盖这部分，改动前读该目录与相关测试。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不得用 `NoWarn` 隐藏新的文档或空引用警告。

## 计划与校验

- `DeliveryPlan` 是交付后端唯一的输入契约。模式判定全部集中在 `Delivery/Build/Validate.cs`：未选任何输出方式即失败；`PackSuper` 与 `PackPayload` 互斥；`OrganizeDirectory` 与打包容器同时开启需要 `ContainerConfirmed`；非 Super_Empty 单出时 `Container` 不得为空；`Super.Empty` 与 `Super.Zstd` 互斥且唯一条目是 `super_empty.img`。新行为必须扩展这条校验路径，不要在 View 或后端各写一套规则。
- DAT/BR 归属必须按分区与同组的 `<partition>.transfer.list`、`<partition>.new.dat[.br]`、`<partition>.patch.dat[.br]` 关联；`Delivery/Build/Package.cs` 的组校验要求唯一的 `transfer.list` 与 `new.dat`、最多一个 `patch.dat` 且分区名一致，否则抛 `InvalidDataException`。不得只凭任意 `.dat` 或 `.br` 文件名推断分区，也不要在别处复制后缀判断。
- Super 容量与块对齐规则集中在 `Delivery/Build/Capacity.cs`（`DeliverySuperCapacity`），让面板预检与 LP 构建器共用同一套保留区算术；输出前的磁盘需求估算集中在 `Delivery/Build/Space.cs`（`DiskSpaceEstimator`，中间文件与最终镜像并存，安全系数固定为 2）。

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

## 脚本与产物

- 卡刷包复用原 ROM 提供的原生 Edify 解释器，不假定 shell 里有 `block_image_update`：`Delivery/Build/Updater.cs` 校验更新器为本机 ELF 且导出卡刷所需 Edify 函数后才采用，脚本路径为 `META-INF/com/google/android/updater-script`。典型调用是 `block_image_verify`、`block_image_update`、`package_extract_file` 与 `file_getprop`，不要把 shell 命令冒充成卡刷脚本。Brotli 场景的调用传 `.new.dat.br` 与 `.patch.dat`，例如：

  ```edify
  block_image_update(
      "/dev/block/bootdevice/by-name/system",
      package_extract_file("system.transfer.list"),
      "system.new.dat.br",
      "system.patch.dat"
  ) || abort("E2001: Failed to update system image.");
  ```
- 内嵌的 arm64 原生更新器住在 `UotanStudio.Assets` 的 `delivery` 资源包，长度与 SHA-256 由该项目的清单记录：运行时由 `AssetStore.Read(AssetCatalog.DeliveryUpdater, AssetNames.Delivery.Updater)` 解码并校验，然后原子落盘到 `%TEMP%/UotanStudio/delivery-updater/update-binary-arm64`，磁盘上仍是可执行文件；已存在且长度、摘要与清单条目一致时直接复用，避免重复解码。
- Bat、Linux/macOS shell 与卡刷脚本由 `Delivery/Scripts/Generator.cs` 基于同一份文件编排结果生成；机型代号、作者信息与生成元数据一律取自 `DeliveryPlan`，不得在各模板里重复硬编码。脚本语言只在生成时确定，切换界面语言不得改写已生成内容。
- ZIP 与目录内部路径统一使用 `/`（相对路径经 `Replace('\\', '/')`）。成员集合以 `Delivery/Build/Output.cs` 末尾的枚举为准：`PackPayload` 时生成项归入 `ZipFileName(plan, "ota.zip")`，`PackSuper` 时走 `OrganizedImageRelativePath`，两者都不是时镜像落在 `images/`；脚本集合排除 `updater-script` 与 `update-binary`，最后统一附加 `README.txt` 与 `checksums.sha256`。`Delivery/Build/Payload.cs` 在 payload 输出旁边写 `payload_properties.txt`，`Delivery/Build/Output.cs` 负责把它从镜像目录里剔除，不要把它当可刷写镜像列出。
- `TXT` 是清单格式：压缩镜像不得直接列为可刷写镜像，必须由脚本先解压到临时路径再交给 fastboot。
- ZST 生成所需的 zstd 按平台固定版本并校验 SHA-256，条目集中在 `Delivery/Tools/Tools.cs` 的 `DeliveryToolPackage`（Windows 取 zstd 1.5.7 的 win64 zip 内 `zstd.exe`，Linux amd64 取 1.5.6 预构建单文件）。riscv64 二进制不在支持与下载范围内。准备出的平台工具按平台放在 `bin/` 下（`Delivery/Tools/Tools.cs` 写出 `bin/Linux`、`bin/android` 与 `bin/<name>`），输出 README 的目录说明与此一致。
- 输出发布先进暂存目录，再逐项 `File.Move` 替换，并为被覆盖的既有文件留备份；失败时按逆序回滚备份，保留既有输出。不得先删除用户已有文件。

## 验证

```text
dotnet build UotanStudio.Pipeline/UotanStudio.Pipeline.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Delivery --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Projects --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.ProjectOperationsBoundaryTests --minimum-expected-tests 1
```

- 交付测试住在唯一测试宿主的 `UotanStudio.Tests/Delivery/`（命名空间 `UotanStudio.Tests.Delivery`），工程测试住在 `UotanStudio.Tests/Projects/`（命名空间 `UotanStudio.Tests.Projects`）。
- 工程测试使用独立会话和纯数据工作区，不初始化 Avalonia；真实窗口生命周期属于宿主验证，不要在本库测试里启动 Dock 或 UI。
- 交付测试要覆盖失败回滚、取消、超限与字节身份，而不是只断言内部对象往返；把外部工具、真实设备与刷写结果留在仓库外验证。

## 未验证边界

- 真实 TWRP 安装、A/B `ota.zip` 在设备上的接受与否、`UotanStudio.Assets/Assets/delivery/update-binary-arm64` 的设备侧行为、以及 `META-INF/com/android/otacert` 与 TWRP `/res/keys` 的信任判定，都不在本仓库门禁内；没有设备证据时不得把这些写成已验证结论。