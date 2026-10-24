# UotanStudio.Delivery 项目指令

在本目录内工作时读这份文件。项目定位、依赖方向说明与目录分组见同目录 `README.md`；仓库级规则（Git 与工作区纪律、不可信输入的通用上限、构建与测试命令、format 双门）在根 `AGENTS.md`，宿主侧交付界面规则在 `UotanStudio/AGENTS.md`，不在此重复。

## 边界与可见性

- 不得引用桌面宿主、界面、项目持久化服务或 `KeyLibraryService`；签名私钥只经 `Models/Signing.cs` 的 `IDeliverySigningKeyProvider` 注入。桌面侧适配在 `UotanStudio/Services/Delivery/`：`DeliveryKeyProvider`、`DeliveryPlanSerializer`、`DeliveryDynamicPartitions`。这条方向没有测试守护，只能靠评审维持。
- 允许引用 `UotanStudio.Firmware`、`UotanStudio.Net` 与必要的第三方库（SharpCompress 等）。
- 工具包下载、GitHub 镜像选择与 Release 元数据经 `UotanStudio.Net`（`HttpDownload`、`GitHubReleaseClient`）完成；本库不复制镜像枚举、测速或版本归一化逻辑。
- 对外入口是 `Build/DeliveryService.cs`（公共门面，吃 `DeliveryPlan`）。宿主与 MCP 需要的构建/校验/容量/工具准备契约（`IDeliveryTools`、`DeliveryOutputService`、`DeliveryValidation`、`DeliverySuperBuilder`、`DeliveryPayloadBuilder`、`DeliverySuperCapacity`、`DeliveryPackageBuilder`、`DeliveryScriptGenerator`、`DiskSpaceEstimator`、`DeliveryPlanStore` 等）是公共契约；其余实现细节保持 internal。本库不向宿主或 MCP 开放 `InternalsVisibleTo`，只保留 Tests/Benchmarks/DeliverySmoke 等脚手架的授权。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不得用 `NoWarn` 隐藏新的文档或空引用警告。

## 计划与校验

- `DeliveryPlan` 是后端唯一的输入契约。模式判定全部集中在 `Build/Validate.cs`：未选任何输出方式即失败；`PackSuper` 与 `PackPayload` 互斥；`OrganizeDirectory` 与打包容器同时开启需要 `ContainerConfirmed`；非 Super_Empty 单出时 `Container` 不得为空；`Super.Empty` 与 `Super.Zstd` 互斥且唯一条目是 `super_empty.img`。新行为必须扩展这条校验路径，不要在 View 或后端各写一套规则。
- DAT/BR 归属必须按分区与同组的 `<partition>.transfer.list`、`<partition>.new.dat[.br]`、`<partition>.patch.dat[.br]` 关联；`Build/Package.cs` 的组校验要求唯一的 `transfer.list` 与 `new.dat`、最多一个 `patch.dat` 且分区名一致，否则抛 `InvalidDataException`。不得只凭任意 `.dat` 或 `.br` 文件名推断分区，也不要在别处复制后缀判断。
- Super 容量与块对齐规则集中在 `Build/Capacity.cs`（`DeliverySuperCapacity`），让面板预检与 LP 构建器共用同一套保留区算术；输出前的磁盘需求估算集中在 `Build/Space.cs`（`DiskSpaceEstimator`，中间文件与最终镜像并存，安全系数固定为 2）。

## 脚本与产物

- 卡刷包复用原 ROM 提供的原生 Edify 解释器，不假定 shell 里有 `block_image_update`：`Build/Updater.cs` 校验更新器为本机 ELF 且导出卡刷所需 Edify 函数后才采用，脚本路径为 `META-INF/com/google/android/updater-script`。典型调用是 `block_image_verify`、`block_image_update`、`package_extract_file` 与 `file_getprop`，不要把 shell 命令冒充成卡刷脚本。Brotli 场景的调用传 `.new.dat.br` 与 `.patch.dat`，例如：

  ```edify
  block_image_update(
      "/dev/block/bootdevice/by-name/system",
      package_extract_file("system.transfer.list"),
      "system.new.dat.br",
      "system.patch.dat"
  ) || abort("E2001: Failed to update system image.");
  ```
- 内嵌的 arm64 原生更新器以 zstd 帧随程序集发布：运行时在内存中解压，按固定长度与 SHA-256 校验后原子落盘到 `%TEMP%/UotanStudio/delivery-updater/update-binary-arm64`，磁盘上仍是可执行文件；`.zst` 由资源压缩步骤从原始 `Assets/update-binary-arm64` 生成，原始文件只作生成输入，不再随程序复制或发布。
- Bat、Linux/macOS shell 与卡刷脚本由 `Scripts/Generator.cs` 基于同一份文件编排结果生成；机型代号、作者信息与生成元数据一律取自 `DeliveryPlan`，不得在各模板里重复硬编码。脚本语言只在生成时确定，切换界面语言不得改写已生成内容。
- ZIP 与目录内部路径统一使用 `/`（相对路径经 `Replace('\\', '/')`）。成员集合以 `Build/Output.cs` 末尾的枚举为准：`PackPayload` 时生成项归入 `ZipFileName(plan, "ota.zip")`，`PackSuper` 时走 `OrganizedImageRelativePath`，两者都不是时镜像落在 `images/`；脚本集合排除 `updater-script` 与 `update-binary`，最后统一附加 `README.txt` 与 `checksums.sha256`。`Build/Payload.cs` 在 payload 输出旁边写 `payload_properties.txt`，`Build/Output.cs` 负责把它从镜像目录里剔除，不要把它当可刷写镜像列出。
- `TXT` 是清单格式：压缩镜像不得直接列为可刷写镜像，必须由脚本先解压到临时路径再交给 fastboot。
- ZST 生成所需的 zstd 按平台固定版本并校验 SHA-256，条目集中在 `Tools/Tools.cs` 的 `DeliveryToolPackage`（Windows 取 zstd 1.5.7 的 win64 zip 内 `zstd.exe`，Linux amd64 取 1.5.6 预构建单文件）。riscv64 二进制不在支持与下载范围内。准备出的平台工具按平台放在 `bin/` 下（`Tools/Tools.cs` 写出 `bin/Linux`、`bin/android` 与 `bin/<name>`），输出 README 的目录说明与此一致。
- 输出发布先进暂存目录，再逐项 `File.Move` 替换，并为被覆盖的既有文件留备份；失败时按逆序回滚备份，保留既有输出。不得先删除用户已有文件。

## 验证

- 定向测试：`dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Delivery --minimum-expected-tests 1`。交付测试全部住在唯一测试宿主的 `UotanStudio.Tests/Delivery/`，命名空间 `UotanStudio.Tests.Delivery`。
- 测试要覆盖失败回滚、取消、超限与字节身份，而不是只断言内部对象往返；把外部工具、真实设备与刷写结果留在仓库外验证。

## 未验证边界

- 真实 TWRP 安装、A/B `ota.zip` 在设备上的接受与否、`Assets/update-binary-arm64` 的设备侧行为、以及 `META-INF/com/android/otacert` 与 TWRP `/res/keys` 的信任判定，都不在本仓库门禁内；没有设备证据时不得把这些写成已验证结论。