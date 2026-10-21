# UotanStudio.Firmware 项目指令

在本目录内改固件格式、文件系统、Payload、启动镜像、签名或厂商容器前读这份文件。目录职责、上下文清单与重打包元数据说明见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 上下文边界

- 顶层目录是限界上下文；`Core` 是共享内核。`Core` 不得引用任何具体上下文，也不得引入第三方命名空间；具体上下文不得引用 `Application` 或 `Infrastructure`；`Application` 不得引用 `Infrastructure`。
- 跨上下文依赖是冻结基线，只允许 `UotanStudio.Tests/Firmware/Architecture/FirmwareArchitectureGuardTests.cs` 的 `AllowedCrossContextEdges` 中列出的边。新增边必须评审并同步更新基线；`FileSystem ↔ BlockImage`、`BootSecurity ↔ Patching` 是已撤销的环边，不得重新引入。
- 每个源文件的命名空间必须由目录推导，`Properties/` 除外；`FirmwareNamespaceFolderTests.cs` 会扫描整个固件树。
- 通用压缩经 `UotanStudio.Compress` 的公共入口完成；只有 `CompressionImplementationBoundaryTests.cs` 允许列表中的格式领域例外可以直接使用 BCL 压缩流，新增例外要写明理由。SquashFS/EROFS/F2FS 的块级压缩簇、启动镜像组件、Payload 压缩操作、`.cpio.gz`/`.tar.xz` 组合仍归本项目的格式上下文。
- 格式识别以 `Firmware.Application.Formats.FirmwareFormatRegistry.RegisterAll()` 注册的处理器为准；先按扩展名候选，再用内容校验确认。不要在文件管理器、交付列表或脚本生成器里另写后缀判断。

## 不可信输入

- 固件、ZIP、Payload、文件系统镜像和设备返回数据都视为不可信输入：优先流式与有界缓冲，不把整个镜像、压缩结果或单个归档条目一次读入内存。
- 所有偏移、长度、条目数量、解压大小、块数量和数组维度都必须先检查上限并防止整数溢出；失败路径要保留输入字节身份。
- 支持取消的 API 必须贯穿传递 `CancellationToken`；写入文件使用临时文件，完整校验后原子替换。
- 生产代码为原子替换创建的临时文件必须留在目标同目录/同卷，完成或失败后删除；测试、诊断与一次性夹具的临时镜像或目录必须创建在主机 `%TEMP%` 下，不得留在仓库或盘符根目录。
- 修改镜像后分别报告字节等价、结构等价、内容等价与容量等价，不能只靠项目内部往返测试。文件系统重建尽量继承经校验的原镜像参数；压缩率、块数、最终大小和物理布局允许重新计算，不得承诺修改后一定适合原分区容量。

## 重打包与持久化

- `Core/Repacking/RepackMetadata` 是通用来源信息、元数据路径、操作上下文与有界原子发布的统一入口；目录重建能力只来自 `IRepackMetadataProvider`。新增格式通过该契约接入，不在共享层增加具体格式判断。
- 元数据文件名、JSON schema、版本与字段构成当前持久化契约，改动会影响已保存工程；不能把它当作内部临时格式随手改。
- `FileSystem/Metadata`、`FileSystem/Extraction`、`FileSystem/Rebuilding` 的职责分工保持现状；Sparse 与 LP/Super 归 `BlockImage`，DAT/Payload 归 `OtaDelta`。

## 文档与验证

- 本库设置 `GenerateDocumentationFile` 与 `TreatWarningsAsErrors`；公共类型和成员必须有中英双语 XML 注释，缺注释直接构建失败。说明性内容优先写 XML 注释，不堆积与实现重复的行间注释。
- 指针、`fixed`、`sizeof` 与 `stackalloc`→指针必须在 `unsafe` 上下文中书写，不得放宽生产代码的 `unsafe` 要求。
- 定向验证：

```text
dotnet build UotanStudio.Firmware/UotanStudio.Firmware.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.FirmwareArchitectureGuardTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Firmware.Architecture.FirmwareNamespaceFolderTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Firmware --minimum-expected-tests 1
```

- 真实设备、原生挂载、外部工具与厂商镜像的未验证边界见根 `VERIFICATION.md`；没有仓库外证据时不得写成已通过。