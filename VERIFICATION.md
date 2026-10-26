# 验证状态与边界

本文件记录门禁实测结果与未验证边界；每跑完一道门禁就更新"当前实测"一节。只写当前状态：一条测量配一个日期，不写轮次叙述、不写"上一轮"或"之前失败现在通过"。常驻约束与命令在根 `AGENTS.md`，测试工程写法在 `UotanStudio.Tests/AGENTS.md`。

## 当前实测（2026-10-25，Windows x64，Debug）

- `dotnet restore UotanStudio.slnx -p:Configuration=Debug` 与 `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误、0 警告（2026-10-27，共享工作树）。
- `dotnet format` 的 `whitespace` 与 `style`：全解零报告（2026-10-27，Release，`--no-restore --verify-no-changes`）。此前的两处 `UotanStudio` 报告已修：`ViewModels/Editors/ScreenMirrorViewModel.cs` 中 `}` 与下一个 `<summary>` 挤在同一行，`Views/Editors/MirrorTrace.cs` 结尾多出一个换行符。
- CI 等价门禁（2026-10-27，Release，共享工作树）：`dotnet build UotanStudio.slnx --no-restore --nologo -c Release` 0 错误 0 警告；两道 `dotnet format` 零报告；`dotnet test --solution UotanStudio.slnx --no-build -c Release --minimum-expected-tests 6000` 7572 总数 / 7565 通过 / 7 跳过 / 0 失败，单轮 1m31s。构建只剩两处需要修的产物：`Protocols/Adb/AdbSyncClient.V2.cs` 原先直接构造 `BrotliStream`，被 `CompressionImplementationBoundaryTests` 判为通用压缩越界，现改走 `CompressionService.Create{,De}compressionStream(..., CompressionFormat.Brotli)`，并用工装证明 `CompressionOptions { Level = 1 }` 与 `CompressionLevel.Fastest` 在同一 1 MiB 输入上产出逐字节相同的 493 B 帧；`Protocols/Apple/Lockdown/LockdownPairingRecord.cs` 原来对每张证书各读两次 `DateTimeOffset.UtcNow`，DER 的 `NotAfter` 只有秒级精度，子证书晚一秒铸造即超过签发者有效期而抛 `ArgumentException`，现由 `CreatePending` 计算一次有效期窗口并传给三份证书。
- 定向测试：`UotanStudio.Tests.Mirroring` 127/127（1 硬件跳过）、`UotanStudio.Tests.Media` 202/202（1 Linux 跳过）、`UotanStudio.Tests.Devices` 305/305、`UotanStudio.Tests.Protocols` 112/112、`UotanStudio.Tests.Architecture` 16/16。架构门覆盖宿主分层、ZIP 容器、格式探测、命名空间归属、项目引用图与解决方案成员。
- 全量（2026-10-27，Release）：7610 总数 / 7603 通过 / 7 跳过 / 0 失败，单轮 2m12s。该轮在共享工作树里先得到 26 项 `McpHttpExposureTests`/`McpToolCallAuditTests`/`Workspace*` 失败,报的都是 `FileNotFoundException : Microsoft.Extensions.Logging.Abstractions`,即并行会话的构建把 `UotanStudio.Tests/bin/Release` 写坏;同一批字节在私有 worktree 里 restore+构建后,`dotnet build UotanStudio.slnx --no-restore --nologo -c Release` 0 错误 0 警告、两道 `dotnet format` 零报告、`dotnet test --solution UotanStudio.slnx --no-build -c Release --minimum-expected-tests 6000` 全绿,以下数字都取自该私有 worktree。
- 同一字节的前两轮分别报出 1 项与 2 项失败：`ErofsTests.Builder_WritesStandardCompressedIFormat`、`ErofsRebuildFeatureReplayTests.Build_TargetDeclaredToDefineTheAdviseBit_WritesItOnEverySingleLinkedFile` 与 `AdbRemoteProtocolTests.PairAsync_StatusFramedResult_IsAccepted`（15 秒 `Timeout` 取消）；三个用例单独复跑分别是 23/23、20/20、14/14 通过，失败轮次本机有并行构建在跑，故记为负载相关的既有抖动，未定位到具体成因，也不写成已通过稳定性验证。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，`UotanStudio.Desktop.exe` 154,245,120 B（147.1 MiB）；只有 Avalonia/Dock/Ursa/Lingua/YamlDotNet/Common.Logging 等第三方既有的 IL2026/IL2104/IL3053 警告，没有本项目新增的裁剪/AOT 警告，内嵌资源名都能在产物中检索到。
- `TreatWarningsAsErrors=true` 生效于 7 个项目：`UotanStudio.Comm`、`UotanStudio.Compress`、`UotanStudio.Firmware`、`UotanStudio.Hash`、`UotanStudio.Media`、`UotanStudio.Net`、`UotanStudio.RegexInfer`；全解 0 警告同时意味着受管库的 `CS1591` 为 0。
- 生产项目之间没有 `InternalsVisibleTo` 授权；宿主与 MCP 需要的契约是公共 API，只保留 `UotanStudio.Tests`、`UotanStudio.Benchmarks`、`UotanStudio.DeliverySmoke`、`GpuMirrorSmoke`、`UotanStudio.ProjectLifecycleSmoke` 等脚手架授权。
- MCP 工具目录 `UotanStudio.Mcp/Tools/PluginToolCatalog.cs` 共 48 条描述符，其中 6 条 `FlashGated`，只在 `--allow-flash` 下注册。
- 公共属性的 `<value>` 注释仍有约 257 处缺口（2026-10-18 度量），不影响构建门。

本机每轮出现的跳过项（数量随环境变化，通常 6–7 条）：

| 用例 | 原因 |
| --- | --- |
| `Media.VideoDecoderFactoryTests.Linux_CreatesDeferredBackendWithoutClaimingGpuSupport` | Linux 专属能力边界 |
| `Media.ScreenMirrorTransportTests.OpenAdbLease_NetworkTargetWithoutServer_DoesNotOpenUsb` | 本机有 ADB server 在运行，"server 不存在"分支不可测 |
| `Mirroring.OhscrcpyHdcIntegrationTests.ControlForward_RealDevice_ConnectsAndInjectsTouch` | 未设 `UOTAN_HDC_MIRROR_DIRECT` |
| `Firmware.ErofsCorpusCompactIndexRebuildTests.PreservedRebuild_OfCorpusCompactIndexImage_ReExtractsEveryFileByteIdentical` | 未设 `UOTAN_EROFS_CORPUS` |
| `Firmware.ErofsInodeModelEquivalenceTests.Decode_CorpusImageInode_MatchesIndependentDecodeForEveryField` | 未设 `UOTAN_EROFS_CORPUS` |
| `Services.ScrcpyServerReleaseTests.BundledCatalog_DigestMatchesTheJarShippedWithTheApplication` | 测试宿主输出与检出仓库中都没有 `UotanStudio.Desktop/Assets/scrcpy-server.jar`，没有可哈希的出厂工件 |
| `Projects.ProjectBoundaryOptimizationTests.Package_RoundTripIncludesSolutionUnknownWorkflowAndImages` | `ProjectArchiveService.ExportPackageAsync` 拿到 solution 路径时只打包子项目目录，包内缺 solution 级 `nodes/`，`ProjectPackage.ImportAsync` 因而拒绝每个已注册节点工程 |

## HarmonyOS update.bin 实测（2026-10-26，Windows x64，Debug）

- `UotanStudio.Firmware/VendorFirmware/HarmonyOS/` 新增只读格式处理器 `HarmonyOS update.bin`，按 L1/L2 组件表（71/87 字节记录、16/32 字节名称字段）、16 字节保留区与 `0x06`/`0x08` 元数据 TLV 解析；`FirmwareFormatRegistry` 在 `UpdateApp` 之前注册，`.bin` 必须先通过布局字（`0x01`/`0x11`）与组件表边界探测。
- 定向测试：`UotanStudio.Tests.Firmware.HarmonyUpdateBinTests` 23/23（L2/L1、`board_list→BOARD.list`、type 0/1 输出名、SHA-256 校验与失败回滚、筛选、预算、覆盖、取消、截断、非法名称、重复名、未知 TLV、metadata-only）。
- 镜像文件管理：`ImageInputService` 新增 `ImageFormatKind.UpdateBin`，标签为 `update.bin`（`ParseFormatLabel` 可回读）；`ProbeFormat` 经已注册的 `HarmonyOS update.bin` 处理器识别，`ImageViewModel` 的解包模式扫描也登记该类型，导入/刷新/工程恢复后不再显示 `unknown`。`ImageInputServiceTests` 75/75（含 `DetectFormat_HarmonyUpdateBin_ReturnsUpdateBin` 与标签往返）；DataGrid 直接绑定该字符串，本轮未启动 GUI 目视渲染。
- 全 Firmware 命名空间：4357 总数 / 4355 通过 / 2 跳过（未设 `UOTAN_EROFS_CORPUS`）/ 0 失败，单轮 32s。
- 真实样例：`D:\Download\update_full_cust_HAD-W24_all_cn.zip`（SHA-256 `e1f28c16e77cfb05d73ab9c91dc0395f3f532b2e6daa2e071dcbc791ee197aed`）中的 `update.bin`（35,778 B，SHA-256 `264d8bf27b82f8f6bfabf7d3341a2c050644d1e8e30f4e741d554a23264ba14a`）经临时控制台工装解析：布局 L2、4 个组件、数据偏移 2,851、载荷 32,927 B、元数据 TLV `0x08`；`VerifyChecksums=true` 解出 `package_info.img`（121 B）、`BOARD.list`（20 B）、`ptable.img`（18 B）、`version.img`（32,768 B），四个输出 SHA-256 与组件表记录逐一相同；同时发布 `metadata.json`，`canPackDirectory=false`。样例未加入测试集、`TestData/` 或仓库。
- NativeAOT：同轮 `dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true` 成功，产物 `UotanStudio.Desktop.exe` 155,762,176 B；只报告第三方既有的 IL2026/IL2104/IL3053 警告，没有指向新增固件代码的裁剪/AOT 警告。
- 全量（2026-10-27）：7599 总数 / 7593 通过 / 6 跳过 / 0 失败，单轮 2m41s（`dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build -c Debug --minimum-expected-tests 6000`，共享工作树）。

## 酷派 CPB 解包实测（2026-10-27，Windows x64，Debug）

- `UotanStudio.Firmware/VendorFirmware/Coolpad/Cpb/` 新增只读格式处理器 `Coolpad CPB Package`（`.cpb`）：按 552 字节头（`CP` + 版本 1.7、6 个校正字、机型/硬件/版本/来源文本、镜像头结束位置、CRC16）与 76 字节镜像头（64 字节名称、偏移、长度、CRC16）解包；偏移与长度按头部校正字推导的密钥异或混淆，容器校验和按**解码后**的镜像头累加。参考实现为 `reference/MIO-KITCHEN-SOURCE/src/c_extension/cpb_file/cpbtool.c`。
- 密钥与校验和的两处非直觉行为按参考实现逐位复刻，并在用例里钉住：校正字之和的"除以 6"实际是 `0x66666667` 高半乘再右移一位（等价于除以 5），厂商默认校正字因此得到密钥 `0x00310000`（`DeriveKey_VendorCorrectionWords_ProducesTheDocumentedSeed`）；CRC-16 累加器不截断到 16 位，故头部与镜像头都用完整 32 位比较，同时接受低 16 位一致（对应 16 位寄存器产出方），`BitwiseCrc16` 用逐位移位独立复算这条低 16 位等价关系。
- 定向测试：`UotanStudio.Tests.Firmware.CpbTests` 18/18（头字段与镜像目录、掩码校验和、位级 CRC 等价、密钥常量、解包字节、负载损坏仍写出但报校验失败、头部校验和不一致拒解、截断范围拒解且不留暂存文件、条目筛选、不安全/重复/空名称净化、取消、版本字节判定、魔数拒绝、过短文件、`DetectDetailed` 强魔数、混淆变换自逆）。
- 魔数：`UotanStudio.Firmware.MagicGen/corpus/Magdir/uotan-firmware` 新增 `CP\x01\x07` 规则并重新生成 `CompiledMagicData.{g.cs,zst}`（24764 条规则 / 4206 条顶层 / blob 3,045,111 B → zstd 317,709 B）；`FileMagicTests` 34/34、`CompiledMagicCoverageTests` 5/5。
- 镜像文件管理与界面接线：`ImageInputService` 新增 `ImageFormatKind.Cpb`（标签 `cpb`，`ParseFormatLabel` 可回读，`ProbeFormat` 经已注册处理器与 `.cpb` 后缀双重识别）；`ImageInputServiceTests` 77/77（含 `DetectFormat_CoolpadCpbMagic_ReturnsCpb` 与标签往返）。固件信息视图经 `FirmwareInfo_CpbSummary` 与 `FirmwareField_{CpbVersion,Model,HardwareVersion,RomFrom,Mismatch}` 本地化（中英双份 resx + `LocaleKeySets`，`LocaleDictionaryTests` 6/6）；资源管理器把 `.cpb` 归入固件包图标，导入与新建工程的选取器加入 `*.cpb`。
- 架构门：`FirmwareArchitectureGuardTests` 12/12、`FirmwareNamespaceFolderTests` 2/2；全 Firmware 命名空间 4378 总数 / 4376 通过 / 2 跳过（未设 `UOTAN_EROFS_CORPUS`）/ 0 失败，单轮 41s；`UotanStudio.Tests.Services` 1799 总数 / 1798 通过 / 1 跳过 / 0 失败，单轮 2m11s；`UotanStudio.Tests.Architecture` 16/16。
- 门禁：`dotnet restore UotanStudio.slnx -p:Configuration=Debug` + `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug` 0 错误 0 警告；`dotnet format` 的 `whitespace` 与 `style` 两项 `--verify-no-changes` 均零报告。
- NativeAOT：`dotnet publish UotanStudio.Desktop/UotanStudio.Desktop.csproj -c Release -r win-x64 --self-contained -p:PublishAot=true --artifacts-path %TEMP%\uotan-aot-cpb` 成功，产物 `UotanStudio.Desktop.exe` 155,977,728 B；只报第三方既有警告（8×IL2104、4×IL3053，来源 Avalonia.Controls.DataGrid、Common.Logging、Dock.Avalonia、Iciclecreek.Avalonia.Terminal、LTRData.Extensions、Semi.Avalonia.Dock、Ursa、Ursa.Themes.Semi、YamlDotNet），没有任何指向 `UotanStudio.*` 的裁剪/AOT 警告；临时产物已删除。共享工作树里其他会话同时构建 Release 时，默认输出目录会以 `CS2012`（用户映射区域占用）失败在 `UotanStudio.Hash`/`Net`/`Media`/`Desktop` 等无关项目上，因此本轮用 `--artifacts-path` 隔离输出。
- 未验证边界：仓库内没有真实酷派 CPB 固件样本，上述解包正确性只由测试自建的容器覆盖（写入方按参考 C 的布局独立构造，不是生产代码的往返）；未与厂商工具产物做逐字节对照；固件信息视图与属性面板未启动 GUI 目视验证；未实现重打包，CPB 保持只读。

## iOS 支持实测（2026-10-26，Windows x64，Debug）

- 定向测试：`UotanStudio.Tests.Protocols` 107/107、`UotanStudio.Tests.Mirroring` 165 通过 + 1 跳过（`OhscrcpyHdcIntegrationTests` 需要 `UOTAN_HDC_MIRROR_DIRECT`）、`UotanStudio.Tests.Devices` 292/292、`UotanStudio.Tests.Services.LocaleDictionaryTests` 6/6。
- `dotnet build UotanStudio.slnx --no-restore --nologo -c Debug`：0 错误；Fastboot 测试文件原有的两条 `xUnit1051`/`xUnit1069` 已消除，`FastbootUdpHandshakeTests` 与 `FastbootRemoteProtocolTests` 把 `TestContext.Current.CancellationToken` 交给 `Task.Run`。
- Apple 协议族当前契约（均有对应用例覆盖）：
  - `BinaryPlist` 写入器的偏移表按表长度定位，读回不报 "offset table is inconsistent"；对象按 Apple 的子项在前、容器在后展平，根对象索引位于尾部。
  - `ApplePlist` 的 XML 路径先剥离普通 `<!DOCTYPE` 声明再解析，带内部子集的 DTD 仍然拒绝；字典/数组读取器按键值对消费，不重复读取取值元素。
  - 配对记录的 `ToPlist` 写入 `HostPrivateKey` 为 PEM 字符串，读取同时接受 DER 数据块与 PEM 字节形态的记录。
  - 配对证书链的可分辨名称使用 DER 空序列 `30 00`，`CertificateRequest` 可正常编码 TbsCertificate。
- AirPlay 接收端（`UotanStudio.Mirroring/AirPlay/`）：RTSP 握手（OPTIONS/ANNOUNCE/SETUP/RECORD/FLUSH/TEARDOWN/GET_PARAMETER/SET_PARAMETER）、mDNS 报文编解码、128 字节镜像头解复用、AES-128-CTR 与 FairPlay 解包骨架、交织与 UDP 媒体通道都有用例覆盖（`AirPlayReceiverSessionTests` 用回环 TCP 假客户端走完整握手并推帧）。
- AirPlay 未验证边界（不得写成已验证）：
  - 没有 iPhone 真机：mDNS 广播没有被 iOS 实际浏览过，UDP 数据端口没有被真实客户端推过流，帧率/延迟/音画同步没有任何测量。
  - 不随包提供 FairPlay 密钥表，`AirPlayOptions.FairPlayKeyPath` 未配置时加密流被明确拒绝（403）；配置后也只验证了 CBC 解包结构，没有设备 ekey 向量。
  - HAP `/pair-setup` 与 `/pair-verify` 未实现，返回 501；现代 iOS 依赖它完成配对，因此当前实现不能与真实设备完成投屏握手。
  - 宿主 AirPlay 入口只启动接收端并报告监听端口，没有接预览呈现；GPU/CPU 呈现路径未接入 AirPlay。
- iOS 隧道与直连 USB 未在任何硬件上验证。

## ADB 协议对照上游与实机验证（2026-10-26，Windows x64，Debug；真机 Wi-Fi）

- 对照对象：`reference/adb-aosp/`（AOSP `platform/packages/modules/adb`）的 `adb.h`、`adb.cpp`、`transport.cpp`、`client/auth.cpp`、`docs/dev/protocol.md`。
- 校验和：上游 `transport.cpp:send_packet` 只在 `protocol_version < A_VERSION_SKIP_CHECKSUM (0x01000001)` 时写负载累加和，否则写 0；接收侧 `check_header` 只校验 magic 与 data_length，**从不校验 data_check**。原实现按“协商版本 ≤ A_VERSION_MIN 才校验”在收到对端 CNXN 之前以 0 作为版本，把现代设备 CNXN 的 0 校验和判成 `ADB payload CRC mismatch`（USB 直连与 TLS 直连都复现）。已移除入站校验（保留不匹配时的 Debug 诊断，不再拒绝帧），并按协商版本决定出站 `data_check`（收到对端 CNXN 之前按上游写累加和）。
- 其它对齐：CNXN 的版本/最大负载按上游 `update_version` 取 min（≤`A_VERSION`、≤`MAX_PAYLOAD`）；TLS 模式下忽略 AUTH（上游 `if (t->use_tls) break`）；STLS 应答保持 24 字节头 + `A_STLS_VERSION`；OPEN 目标与 AUTH 公钥负载的结尾 NUL 与上游一致。
- 托管 TLS 全双工：Windows 回退的 BouncyCastle `TlsStream` 用一个信号量串行化 **await 式** 读写；ADB 消息循环常驻 read 会让所有写（OPEN/WRTE/OKAY）永久阻塞（实测 `OpenServiceStream` 挂起超过 120 s）。现改为托管路径阻塞 I/O：read 在线程池线程上执行、write 在调用线程上执行（BC 的 `m_recordWriteLock` 与 ADB 发送锁仍串行化写），期限由套接字 Receive/SendTimeout 承担；空闲帧首字节仍按连接层的“无限等待”规则执行。
- 实机（无 server，Wi-Fi，`192.168.1.144:40443`，设备 `24129PN74C`）：本机组播浏览 0 条广告、adb server 的 mDNS 表也为空，而**单播 DNS-SD 查询**拿到 `adb-6a082bda-lZgxIP._adb-tls-connect._tcp.local 192.168.1.144:40443`（该网络过滤组播应答）。用修复后的 `AdbSession` + `AdbTlsTransport` 直连：握手成功 `peer=0x01000001`，`exec:echo uotan-crc-ok` 返回 `uotan-crc-ok`，7 秒空闲后再次 `exec` 返回 `uotan-after-idle`，释放正常。
- 未验证边界：USB 直连的同一修复没有可用的 USB ADB 设备复测；配对阶段的广播仍只走组播（该网络实测可发现），组播全部被过滤且无法手动输入端口时仍无法配对；IPv6-only 链路与多网卡未实测。

## ADB TLS 服务端证书验证（2026-10-27，Windows x64，Debug；无 server 的扫码直连）

- 根因：adbd 的 TLS 服务端证书没有可钉扎身份。对照 `reference/adb-android16`：`daemon/auth.cpp` 的 `adbd_auth_tls_handshake` 每个进程懒生成随机 RSA 密钥（`rsa_pkey`），`transport.cpp` 的 `FdConnection::DoTlsHandshake` 每次握手都调用 `crypto/x509_generator.cpp` 的 `GenerateX509Certificate()`，把 `notBefore` 设为当前时间、序列号固定为 1，因此证书 DER 每次连接都变化；主机侧只用 `SetCertVerifyCallback([](X509_STORE_CTX*) { return 1; })` 接受任意服务端证书，设备侧在 `adbd_tls_verify_cert` 里校验本机公钥。
- 症状：按端点钉扎服务端证书 SHA-256 的旧实现第一次连接成功并记下指纹，第二次连接（新证书）在客户端 `NotifyServerCertificate` 处以 `bad_certificate(42)` 中止，设备侧尚未收到客户端证书；设备行随即失效，投屏报 `Device <host:port> is not connected`。
- 变更：`AdbTlsTransport` 删除服务端证书指纹参数、观察者与 `RemoteServerCertificateSha256`，平台与 BouncyCastle 两条路径都接受任意（仍必须出示）服务端证书；`DeviceController.OpenSecureNetworkAdbSession` 去掉钉扎参数；宿主 `PairedNetworkTarget` 只保留 `Address`/`RequiresTls` 并删除指纹持久化。设备以 `bad_certificate`/`certificate_unknown`/`unknown_ca`/`access_denied` 拒绝本机密钥时，握手与已建立连接的读写都返回“重新扫码配对或接受设备调试授权”的失败文案。
- 定向测试：`UotanStudio.Tests.Devices.AdbTlsTransportTests` 6/6——回环 TLS 1.3 服务端每次连接重建自签证书，连续两次 `UpgradeToTls` 均成功、服务端确实收到客户端证书，另有告警映射用例；`Services.PairedNetworkTargetSettingsTests` 3/3；`Devices` 298/298、`Protocols` 112/112。
- 未验证边界：真实手机没有复跑扫码直连与投屏；本轮检测时 `192.168.1.144:40443` 不可达（设备侧无线调试已关闭或端口已换），旧钉扎指纹与该地址的对应关系只由代码路径与设置文件推断。测试里的 TLS 1.3 服务端用 Schannel（临时密钥不受支持，故服务端证书先导出 PFX 再导入），BouncyCastle 服务端与真机 adbd 的对端行为未覆盖。

## 投屏输入注入预检（2026-10-27，Windows x64，Debug；真机 ColorOS）

- 设备侧事实：一加 ColorOS（设备 `24129PN74C`）没有把 `android.permission.INJECT_EVENTS` 授予 `com.android.shell` 时，scrcpy 服务端（`app_process`，uid 2000）的每次触摸注入都被 `system_server` 的 `InputManagerService` 拒绝（`Permission Denial: injectInputEvent() ... requires android.permission.INJECT_EVENTS`）。视频流不经过该权限，因此表现为“有画面、触摸全灭”，与投屏链路和 TLS 会话无关。
- 只读预检：投屏会话建立后执行 `dumpsys package com.android.shell | grep -E 'Package \[|INJECT_EVENTS'`，`InputInjectionProbe.Parse` 以 `Package [com.android.shell]` 标题确认探测有效、以 `granted=true` 作为唯一肯定信号；`granted=false`、只有 requested 行或权限行完全缺失都判为被拦，缺少包标题则返回未知（不误报）。探测复用投屏共享的直连 ADB 会话（server 路由时用 server 会话），不另开传输。
- 界面：判为被拦时在投屏标题栏显示 `ScreenMirror_InputInjectionBlocked`（中英各一条，含开发者选项 → USB 调试（安全设置）指引）；每次触摸在 3 秒节流下重跑一次预检，用户打开该开关后下一次点击即清除提示，无需重启会话。
- 定向测试：`UotanStudio.Tests.Devices.InputInjectionProbeTests` 7/7（granted/denied/仅 requested/其它权限/缺包标题/空输出）；`LocaleDictionaryTests` 6/6（新增 key 的中英一致与生成索引一致）；`MirrorSessionFacadeTests` 13/13。
- 未验证边界：标题栏提示条没有在受该 ROM 限制的真机上做人工确认（需要重新连接该设备并复跑投屏）；预检在放行设备上会多执行一条 `dumpsys`，极慢设备上的耗时未测量；ROM 若以 `dumpsys package` 之外的形式隐藏该授权，预检返回未知而不显示提示。

## 扫码配对连接端口获取：对齐 AOSP 与 ADB_Pair_Go（2026-10-27，Windows x64，Debug；无 server 的扫码直连）

- 参考源码：`reference/adb-android16/`（AOSP `packages/modules/adb`）与 `reference/ADB_Pair_Go/`（`github.com/lx200916/ADB_Pair_Go`，`git clone --depth 1` 拉到本地后逐文件核对 `adbPair.go`/`README.md`）。
- 官方链路（AOSP）：`daemon/mdns.cpp` 的 `register_adb_secure_connect_service(tls_port)` 以 `persist.adb.wifi.guid`（格式 `adb-<serialno>-<6 位随机字母数字>`）作为**实例名**注册 `_adb-tls-connect._tcp`，端口取自 `TlsServer::port()`；`daemon/adb_wifi.cpp` 的 `enable_wifi_debugging()` 在启用无线调试时启动 TLS 服务并注册该实例、`disable_wifi_debugging()` 注销，且启用前先 `start_mdnsd()`（`init.svc.mdnsd` 未运行就启动它，5 秒未就绪只写日志）。主机侧 `client/adb_wifi.cpp` 在配对成功后把配对交换里的 `ADB_DEVICE_GUID` 交给 `adb_secure_connect_by_service_name()`；`client/transport_mdns.cpp` 用它作为实例名在 `_adb-tls-connect` 的结果里按名字查找，`client/mdnsresponder_client.cpp` 再用 `adb_wifi_is_known_host(instance_name)` 与配对时写入 `adb_known_hosts.pb` 的 GUID 比对，命中才 `connect_device("<guid>._adb-tls-connect._tcp")`。ADB_Pair_Go 只覆盖前半段：zeroconf 浏览 `_adb-tls-pairing._tcp` 匹配二维码 `S` 字段后调用外部 `adb pair`，端口发现整个交给 adb 的 GUID 链路，没有独立实现。
- 结论：官方没有“轮询端口”这一步，端口只存在于 mDNS 的 `_adb-tls-connect` 实例里，而实例名就是配对返回的 GUID；本库此前按地址做泛化浏览并丢掉 GUID，随后又用全范围扫描串行兜底，两处偏差都在本轮对齐。
- 变更：配对交换返回的 GUID 带入 `AdbPairingEndpoint.Guid`；每轮除 PTR 浏览外，直接查询 `<guid>._adb-tls-connect._tcp.local` 的 SRV/TXT（组播与单播各一次），`AdbMdnsDiscovery.DescribesDevice` 让“实例名等于 GUID”与“地址等于配对地址”等价，`SelectConnectPorts` 优先选 GUID 命中的实例；`ConnectDiscoveryTimeout` 30 秒降到 10 秒，`ForeignPortFallbackDelay` 8 秒降到 4 秒。范围扫描不再是串行兜底，而是与广播查找**同时**进行（10 秒预算、768 并发、250 毫秒单次期限、命中即停），先给出端口的一方胜出并取消另一方，最坏等待从“30 秒浏览 + 25 秒扫描”降到约 8 秒。
- 实机测量（`192.168.1.144`，无线调试开启、该设备完全不广播 mDNS）：扫描加 ADB 握手确认在 **7.3 秒**内命中 `44065`；同一轮还发现另一个开放端口 `42568`，它没有完成 ADB 握手，说明“只接受完成握手的端口”是必需的判据。该地址 32768–60999 内只有这两个端口接受连接，其余 SYN 被静默丢弃，因此扫描耗时由超时而非 RTT 决定。
- 定向测试：`TcpPortScannerTests` 7/7；新增 `AdbConnectIdentityTests` 5/5（GUID 实例跨地址命中、仅地址命中、不同设备不命中、GUID 实例优先于更新的非 GUID 实例、无 GUID 时保持地址排序）；`Devices` 命名空间整批仍有 2 项既有的并发超时（`AdbTlsTransportTests.UpgradeToTls_DeviceRebuildsItsCertificateEachHandshake_CompletesBothHandshakes`、`HdcServerTransferTests.Recv_RegularFile_PublishesOnlyAfterCompletion`），单独复跑分别 6/6 与 10/10 通过。`UotanStudio.Comm` 与 `UotanStudio.Tests` 定向构建 0 警告 0 错误（此时整解决方案构建在 `UotanStudio.Desktop` 复制输出 DLL 处被正在运行的宿主进程锁定，与本次改动无关），whitespace 与 style `--verify-no-changes` 通过。
- 未验证边界：GUID 直查没有在“设备确实广播但 PTR 被过滤、SRV 仍可答”的网络上复测（本轮这台手机根本不广播，只验到扫描路径）；没有真机重跑完整扫码流程；IPv6-only 与改过 `ip_local_port_range` 的设备不在扫描覆盖内；`start_mdnsd` 失败（该 ROM 可能没有 mdnsd 或注册失败）时设备永远不会广播，除扫描外没有官方替代路径。

## RKDL flash 几何与擦除（2026-10-27，Windows x64，Release）

- `READ_FLASH_INFO` 的 11 字节结构前缀现在解出扇区数、扇区大小、以扇区计的块大小与 flash 片选掩码，并派生块字节大小、块数与在位芯片列表；`READ_CAPABILITY` 解出直接 LBA 位与首 4M 访问位。响应短于结构前缀时按设备实际应答的字节数报错，不再静默补零。
- `WRITE_SECTOR` 在建 CBW 之前先校验负载：必须是 528 字节物理扇区（512 数据 + 16 备用）的整数倍、不得超过一条命令接受的 32 扇区，且 LBA 范围可用；`TEST_UNIT_READY` 把 CSW 数据残留里的进度解出来，`EraseBlocks` 返回设备以 CSW 状态 1 应答的块数，使“事务失败”和“报出坏块”可区分。
- 擦除按上游 `CRKDevice` 的分支走两条路：介质为 eMMC（`READ_FLASH_ID` 前四字节小端等于 `EMMC`）或设备声明直接 LBA 时，用 `ERASE_LBA` 按 32768 扇区分块覆盖上报扇区数；否则按片选掩码逐颗在位芯片用 `ERASE_FORCE` 以 16 块为一条命令推进。每个分块前检查取消，进度按已擦除的扇区或块上报。
- 定向测试：`UotanStudio.Tests.Protocols.RkdlFlashGeometryDecodeTests` 7/7（结构字段映射、短响应报错、缺块几何报零块、能力位、无标志能力字、能力短响应报错、eMMC ID 的小端读法）。
- 未验证边界：本机没有连接任何 RKDL 设备，读几何、写扇区与两条擦除路径都没有线上实测；`EraseAllFlash`/`EraseLbaRange`/`IsEmmcFlash` 目前只是 `UotanStudio.Comm` 的公共 API，宿主与 MCP 都没有调用入口，因此还没接上仓库要求的危险操作确认门；不同主控对 CSW 状态 1 的实际语义差异未验证。

## 设备链路实测（2026-10-24，Windows 主机，ADB server 5037、HDC server 17035）

- ADB 有 server：USB 设备 `6b24c48f` 经 server 执行 shell 与 sync push/pull，回读逐字节一致。
- ADB 无 server：USB 直连在 WinUSB 与 libusb 两个后端均完成 shell 与 sync push/pull；TCP 直连 `192.168.1.149:5555` 完成 shell、sync 与 7 秒空闲后的再次命令。
- HDC 有 server：TCP 目标 `127.0.0.1:5555` 与 USB 目标 `5XM0225C18001159` 均完成 shell 与 7 秒空闲后的再次命令。
- HDC 无 server：停止 HDC server（17035 无监听）后直连 TCP `127.0.0.1:5555`，完成 shell 与 7 秒空闲后的再次命令，并完成过 sync send/recv。
- HDC USB 直连：设备 `12D1:5000`（0xff/0x50/1，序列号 `5XM0225C18001159`）绑定 WINUSB；停止 HDC server 使直连策略放行后，WinUSB 与 libusb 两个后端均完成 HDC 握手、shell 与 7 秒空闲后的再次命令。本机不把 COM 串口当作 HDC 目标。
- ADB Android 11+ 无线调试 TLS：STLS 常量、明文收到 A_STLS 快速失败、分部读取与“等待下一帧不设读取期限”由 `AdbRemoteProtocolTests` 覆盖；无 server 的扫码直连真机（2026-10-26，`192.168.1.144` 的 `_adb-tls-connect` 端口）完成配对与首次连接。托管 TLS（Windows 回退到 BouncyCastle）的读取期限只对已经开始到达的帧生效：等待下一帧无限等待，期限到达即把传输标记失效而不是继续复用损坏的记录层；空闲期间消失的对端由 TCP keepalive 兜底。`AdbTlsTransport.IsConnected` 反映真实链路状态（托管 TLS 已关闭/失败，或读写已失败即视为断开），共享池把失效会话立即移出池并连同其独占端点租约一起拆除，下一次借用重新打开并按 IP 重新解析端口；端口改写后触发一次设备列表刷新。真机上的 shell、logcat、属性查询与断线重连未复测。
- 扫码配对：没有可控的手机扫码 UI，未做真实扫码；对运行中的 adb server（platform-tools 36.0.0）执行 `host:pair` 确认命令格式与结果帧：请求确认后 server 追加 `OKAY`/`FAIL` 状态帧，连接被拒时返回 `Failed: Unable to start pairing client.`（实测分类为 `AdbServerPairingClientException`），非 `ServerOnly` 模式据此改用进程内 SPAKE2 配对后再经 server 连接；有 server 的配对/连接顺序对齐上游命令行的两步（`adb pair` 之后重新读取 `adb mdns services` 再 `adb connect`）：`host:pair` 成功后先重读 `host:mdns:services` 并 `host:connect:` 设备的 `_adb-tls-connect` 行，本地组播浏览保留现有实现并在表无行时兜底，配对前候选最后使用。结果帧的两种形状、失败分类与连接来源顺序由单元测试覆盖，真实扫码与回退后的真机连接仍未验证。
- 扫码后的连接端口自动获取：server 侧按 `host:devices-l` 的两种身份识别已连接设备（`ip:port`，以及经 `host:mdns:services` 表解析的 `adb-…._adb-tls-connect._tcp` 实例名）；显式连接先取重读的 server mDNS 表行，其次实时本地浏览，配对前候选最后，且 `host:connect:` 的应答要在目标列表中复核为非 `offline` 才被接受。配对后每 3 秒重读一次、直到 30 秒期限，候选按“最近广播的 SRV 优先、地址匹配先于仅主机名匹配”排序；无 server 时每轮先做实时浏览再探测，8 秒宽限后把其余广播实例的端口也按配对主机试探（覆盖双栈应答方用另一地址族/主机名发布的情况），组播没有交出任何实例时再向设备自身发送**单播 DNS-SD 问询**（该网络的组播应答会被过滤，实测单播仍能取到端口），未解析出地址的实例按其 SRV 端口参与试探，握手失败按 IP 重新浏览一次（宽限窗口后取最新实例），手动端口预填同样优先使用最新广播值。另外，已记住的安全目标在扫描时与 server 目标对齐：server 已列出同一主机时改用其当前端口，且同一主机已有其它 ADB TCP 行时不再单独生成 `adb-tcp:` 行（消除 43407/36695 这类同机双端口）。匹配、排序、时间戳与同主机去重由 `AdbPairingDiscoveryTests`、`AdbServerMdnsTests` 与 `AdbRemoteRoutingTests` 覆盖；单播兜底与真实手机的换端口时序仍以实机日志为准，IPv6-only 链路未实测。

## F2FS 真实镜像与 GKI 往返（2026-10-24，Windows x64 + WSL2 Alpine，Debug）

- 目标 `D:\NewProject1\NewProject1\output\userdata.raw`（10 GiB，F2FS v1.16，volume `data`，features `0x1499`；可达条目 3 个，含 1.587 GB `preinstall/cust.img`）。
- 托管解包捕获 3 个 inode 的 mode、uid/gid、大小、`security.selinux`、atime/ctime/mtime、generation、project id、link count、flags 与 advise；`TryRestore` 重建 1.70 GB 镜像，`EquivalentStructure`/`EquivalentContent` 为 true、`EquivalentBytes` 为 false、`FitsSourceFilesystemSize` 为 true，清单 2 条一致。
- `F2fsBuilder` 按 checkpoint 5% overprovision 预留主区段容量，`valid_block_count` 不超过 `user_block_count`；回归测试 `F2fsTests.Build_LargeData_CheckpointCapacityCoversEveryValidBlock` 用 320 MB 载荷断言容量账目与保留段。
- GKI 5.10.269 / 6.6.143 / 6.18.32（TCG）完成 fsck → 只读挂载 → 整树元数据与内容校验 → 读写写回 → fsck → 只读回读，三版均 PASS；`cust.img` SHA-256 与源一致，btime 两侧均为 `0.000000000`。
- `UotanStudio.Tests.Firmware` 全命名空间 4301 项 / 4299 通过 / 0 失败 / 2 跳过（缺 `UOTAN_EROFS_CORPUS`）；F2FS 相关 7 个类 70/70 通过。
- 边界：重建重放可复现的超级块特性位并重建 quota inode，crtime 位按上游 `0x0100` 重放；源含 casefold 目录或 verity 文件内容时显式拒绝，无法复现的特性位计入丢弃报告。该镜像无硬链接、符号链接、设备节点、压缩、大小写折叠、加密或 verity 内容；QEMU 为 TCG，不代表真实设备或厂商内核。证据在 `artifacts/gki-fs-lab/f2fs-real-20261024/`。

## 内嵌资源 zstd 帧（2026-10-24，Windows x64，Debug/Release）

### 规则表（Firmware）

- 生成物：`UotanStudio.Firmware/Core/Magic/Generated/CompiledMagicData.zst` 317,678 B（原 3,044,942 B blob 的 10.4%），同目录 `CompiledMagicData.g.cs` 1.5 KB（只含 `RuleCount`、`RulesByteLength`、`CompressedByteLength`、`RulesSha256`、`GeneratedAt`）。解码后 SHA-256 `fe2e629bfea17370ef2f85625e00d3d198cfa3d9ae95d2bb9bb01d8ef2bbf6b1`、`topLevel=4205`、`rules=24763` 与跳过统计保持不变。
- 程序集：`UotanStudio.Firmware.dll` Release 12,213,248 B → 4,443,648 B（−7.41 MiB），Debug 4,770,304 B。资源以 `LogicalName=UotanStudio.Firmware.Core.Magic.rules.zst` 内嵌；`WithCulture=false` 必需，否则 MSBuild 会把文件名里的 `.bin` 段当文化并产出 satellite assembly。
- 运行时：`FileMagic.Identify` 首次调用 331 ms（资源读取 + zstd 解码 + SHA-256 校验 + 4205 条顶层规则建索引 + 首个匹配），热调用 113 µs/次（1000 次平均）；zstd 解码本体 2.0–2.8 ms。
- 识别正确性：`dotnet run --project UotanStudio.Firmware.MagicGen -- --smoke` 20/23 命中，未命中的 `plain-text`、`empty` 与 `ZIP` 样本属于既有边界。

### 字体（Host）

- 生成物：`UotanStudio/Fonts/MiSans-Regular.ttf.zst` 4,900,305 B、`MiSans-Bold.ttf.zst` 4,979,674 B（原 8,122,324 / 8,009,316，60.3% / 62.2%）；原始 TTF 只在本地 `reference/fonts-source/`（仓库忽略）作重新生成的输入。
- 程序集：`UotanStudio.dll` Debug 23,382,016 B → 17,742,848 B（−5.38 MiB）。
- 运行时冒烟（真实 `UotanStudio.dll` + Win32/Skia 控制台宿主）：集合键 `fonts:UotanStudio/MiSans`，Regular 与 Bold 均解析为 `MiSans`、29,758 个字形；离屏渲染 `Uotan 中文字体` 得到 2,599 个非透明像素。冷构造 186.7 ms，首次 typeface 解析 6.0 ms。
- 未验证：真实 GUI 首帧与整体启动耗时、主题/回退链在窗口中的表现；AOT 运行时的字体加载只由发布产物中包含对应逻辑资源名间接证明。

### 鸿蒙采集库（Mirroring）

- 生成物：15 个 `Assets/ohos/device/*.so` 共 11,204,026 B → 15 个 zstd 帧共 3,478,225 B（31.0%），加同目录 `manifest.txt`（文件名、解码长度、SHA-256）；`UotanStudio.Mirroring.dll` Debug 3,627,520 B。
- 运行时：首次使用时物化到 `%TEMP%/UotanStudio/ohos-device/<清单 SHA-256 前 16 位>/`，逐文件校验长度与摘要后原子替换；`OhscrcpyDeviceAssetsTests` 3/3，`OhscrcpyServerManagerTests` 12/12。
- 发布输出不含 `ohos/device` 松散文件（`publish/ohos/device` 只剩空目录）。
- 未验证：真机/HDC 推送与 `dlopen`；多进程并发首次物化共用同名 `<name>.tmp` 的竞态未特殊处理。

### 卡刷更新器（Delivery）

- 生成物：`Assets/update-binary-arm64.zst` 838,349 B（原 2,002,464 B，41.9%），逻辑名 `UotanStudio.Delivery.Assets.update-binary-arm64`。
- 运行时：内存解压 → 长度 + SHA-256 校验 → `%TEMP%/UotanStudio/delivery-updater/` 原子落盘并设置可执行位；`EmbeddedUpdaterAssetTests` 2/2。
- 发布输出 `Assets/` 只有三个图标，不携带 2 MB 更新器。
- 未验证：设备侧实际执行该更新器。

## GUI 与投屏实测（2026-10-25，Windows x64，Debug；真机）

- 环境：Qualcomm `05C6:90DB`（直连 USB ADB，序列号 `6b24c48f`，Android 9 / SDK 28 / arm64-v8a）+ 华为 `12D1:5000`（HDC，connect key `5XM0225C18001159`，OpenHarmony-7.0.0.105 / SDK 26）；本机没有 adb/hdc server，两条链路都是直连。设备管理器同时读到两台设备且详情完整。
- 共享 ADB 会话下的 GUI 行为：设备日志在投屏期间与滚动后持续出新行；点击投屏后属性面板显示 `投屏中` / H264 / Media Foundation (GPU) / 1440 × 2880，全局日志出现 `Composition presentation active: D3D11 shared texture`；用记事本覆盖主窗口 6–7 s 后切回，视频区像素差异 15768–29657 px 而属性区 0 px 变化，注入"主屏幕"后视频区再变 21940 px；刷新详情后 UIA 查询 659 ms 返回且 `targetResponsive=true`；两次关闭程序均 6 s 内 `EXITED_CLEAN`。
- 关闭投屏文档：`SendMessageTimeout(WM_NULL)` 采样 25 s，静态画面下关闭瞬间 UI 最大延迟 9.4 ms（p95 0.5 ms），无一次超过 200 ms；关闭后设备日志仍在刷新，再次投屏正常出画（视频区均值 36.66）。
- 标签切换：切到交付页 3 s 再切回，视频区平均亮度 72.68 → 72.59（max 255），832/569232 个采样像素变化；注入"主屏幕"后变化 126659 个采样像素。最终二进制复跑一次为 44.52 → 36.50（max 255）、62952 个采样像素变化。
- 双设备投屏：同时打开 HDC 与 ADB 两个投屏文档，来回切换后 ADB 画面保持（72.70 → 72.61，852/569232 采样像素变化），注入"最近任务"后视频区变化 127776 采样像素。
- HDC 冷启动：`--open-screen-mirror 5XM0225C18001159` 出画（视频区均值 39.29，max 255）；清理本产品遗留的采集扩展后复跑均值为 50.31；`hdc fport ls` 可见应用注册的 `tcp:…→localabstract:scrcpy_grpc_socket` 与 `localabstract:uitest_socket` 两条转发。
- 未验证：密码页安全视图只验证了"不误判断联"，解锁后能否自动恢复出画未验证；设备待机断流的自动重开只在门禁与代码路径上验证，未在真机等待屏幕超时复现；双设备同时投屏时只有 ADB 一路有画面，两路同时出画未验证；HDC 冷启动稳定性未做 20 次以上重复统计。

## CI 门禁现状

`.github/workflows/ci.yml` 跑 `setup-dotnet` 取 10.0.x、构建、whitespace 与 style 两道 `dotnet format`，然后 `dotnet test --solution UotanStudio.slnx --no-restore -c Release --minimum-expected-tests 6000`，Test 步骤 `timeout-minutes: 15`，不设置任何 `UOTAN_*`，因此门控用例自跳。`.github/workflows/release.yml` 只负责 NativeAOT 发布。套件规模再变化时，Test 步骤的下限要跟着实测调，而不是靠放宽来掩盖删除。

## 门禁覆盖不到的地方

- `UotanStudio.Benchmarks` 与 `UotanStudio.FilesystemFixture` 在 `UotanStudio.slnx` 内，随解决方案构建/format/测试门一起运行。`Benchmarks` 的 `Avalonia.Headless` `PackageVersion` 仍必须保留在 `Directory.Packages.props`。
- 增量构建不重跑分析器，会显示"0 个警告"；任何警告清点都要带 `--no-incremental`。
- Debug 还原与构建都要显式 `-c Debug`（原因见根 `AGENTS.md` 的 Debug 构建条）。
- 测试宿主输出必须留在仓库内，否则 `FirmwareContextDependencyScannerTests` 失败并额外产生环境跳过。
- 共享工作树里并行会话重写 `bin/`/`obj/` 会伪造大批失败；判定红绿前先确认加载的字节与源码一致。`PayloadIncrementalBuilderTests` 家族在全量并发负载下出现过 SHA-256 与清单不符的红，单跑该类 14/14 通过。
- NativeAOT 发布仍会报告第三方 UI/Dock 库的 `IL2026` 反射绑定警告；DI 注册全部使用显式工厂，但第三方警告不等于运行时 GPU/窗口路径已验证。
- `UotanStudio.Tests` 没有 `UotanStudio.Tests.Comm` 命名空间；`UotanStudio.Comm/README.md` 的 `FullyQualifiedName~UotanStudio.Tests.Comm` 过滤命令零匹配，Comm 协议/传输的回归只有零散的 wire-format、设备映射与集成测试。

## 未验证边界

- `win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 未在本机执行；真实 USB 设备、物理手机与 macOS/Linux 平台路径未验证。NativeAOT 只在本机发布过 `win-x64`，AOT 下 `tools/list` 数量与真实设备/图形路径未重测。
- EDL/MTK 的界面项（expert 开关下的写入块上限与 NAND 每块页数覆盖、跳过读写确认头、主分区条目数组起始 LBA 与条目数量/大小覆盖、按清单回读、加密页脚读取、pbl/QFPROM 转储与安全启动状态、DA 阶段的区域转储控件、按目录匹配 programmer、设备卡上的 bootloader/Boot ROM 版本与 MEID/SOCID 行、只读 RPMB 容量按钮）只有假件与脚本化覆盖：没在真实 EDL/MTK 设备上执行过任何一次读写，也没有逐项目视过启动后的 GUI。MEID/SOCID 只在内存与设备卡显示，未持久化、未导出、未进日志；RPMB 容量未经真实 XFlash 存储就绪会话验证。
- WSL/QEMU 原生挂载与原生工具互操作不在仓库门禁内；文件系统写入方的 GKI 5.10/6.1 挂载、fsck 与整树读回取证在仓库外工装完成，不随 CI 运行。
- 无 server 的 TCP ADB 与 scrcpy 投屏结论来自同一模拟器实例的 5555 与 16384 回环端口实测；该 ROM 不回应 `GET_CLIPBOARD`，设备→客户端的主动剪贴板推送未在硬件上观察到，运行时切换也未在模拟器或设备上复跑。
- HDC 直连 forward/控制链路只由受 `UOTAN_HDC_MIRROR_DIRECT` 门控的 `OhscrcpyHdcIntegrationTests` 覆盖；本机因 DevEco 的 HDC server 占用目标，直连 USB 被 Comm 的 fail-closed 策略拒绝，该链路未验证。采集库需要设备侧匹配的 protobuf/gRPC ABI，不匹配时视频路径按能力不足快速失败；设备库随包分发只代表文件存在，不代表可加载。
- 有意不接入宿主的 Comm 能力以 `UotanStudio.Comm/README.md` 的 "Implemented but deliberately not surfaced in the host" 一节为准：目前记录的是要求 preloader 命令阶段且交换被拒即让会话 fault 的 `MtkSession.GetBootLoaderVersionAsync` 与 `GetBromVersionAsync`（同一对字节已由非致命的 `QueryBootIdentityOwnedAsync` 在初始化时收集）。该节同时声明厂商 exploit 实现类入口已整体移出代码库，不要按旧记录去找它们。
- HarmonyOS `update.bin` 只解析组件表与元数据 TLV 形态：`0x08` 签名载荷只跳过、不验签；ZIP 外壳、组件镜像内部的 `HARMONY!`/HVB 封装与文件系统二次解包不在该处理器内。除该小样例外的真实整包（多 GB、L1 布局）未实测，打包未实现。
- 投屏与设备之外的 GUI 交互（真实 FolderPicker、拖拽、缩放手势）没有自动化覆盖，Avalonia 编译绑定只在构建期校验绑定名，不等于渲染正确。
