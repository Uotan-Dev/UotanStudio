# UotanStudio.Comm 项目指令

在本目录内改设备发现、传输或协议会话前读这份文件。设备边界、层职责与协议细节见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 所有权与路由

- 本项目是全部设备读写的唯一持有者：USB 句柄、TCP/UDP、串口、adb/HDC server 协议都在这里。宿主不得自行打开 socket、串口或 USB 句柄，只能消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与公开协议客户端。`Transports/Usb/Backend` 是内部互操作，不是宿主 API。
- scrcpy 与其它呈现协议不在 Comm；`UotanStudio.Mirroring` 只引用 Comm，`UotanStudio.Media` 不引用 Comm。不要在 `Protocols/` 下新增呈现协议。
- `Routing/DebugBridgeRoute` 只以普通 TCP 探测 adb/HDC server 是否存在；只有连接被拒绝表示 server 不存在，超时/协议失败保持上次状态。正值缓存 2 秒，拒绝不缓存。控制器在首次探测前按 `DeviceCommunicationConfiguration.RouteMode` 固定 `Auto`/`ServerOnly`/`DirectOnly`/`StartupFrozen` 模式，并把启动探测结果写成一份不可变快照；发现、开启、宿主决策都读这一份快照，快照变化推进 epoch 并盖在端点上，打开时 epoch 不符即拒绝而不是静默改路。`StartupFrozen` 不刷新快照。
- server 的 serial/connect key 是权威身份。目标存在、离线或未授权时仍属于 server 管理的目标；协议错误不得自动回退 USB。HDC 只服务其列表实际包含的 connect key，未注册键才允许直连。`ServerOnly` 一律拒绝直连，`DirectOnly`/`StartupFrozen` 在 server 监听时拒绝直写而不是切换；跨路由打开同一物理设备会被拒绝，旧 server 租约在 server 消失时由监视循环释放。`DeviceEndpointId(Source,Transport,Address)` 区分发现来源。
- `UsbPhysicalIdentity.ServerPhysicalId` 只在精确序列号、实例 ID 尾部或仅大小写差异且唯一候选时返回物理标识；VID/PID、枚举顺序与列表位置绝不用于映射。

## USB 与生命周期

- `CommKit` 静态构造把 `UsbAccessPolicy` 配到 `DebugBridgeSessionOpener.RequireDirectUsb`。策略未配置时，debug 接口（class `0xff`、protocol `1`、subclass `0x42`/`0x50`）必须 fail-closed，不得先开句柄再检查。
- `UsbApiKind.Auto` 解析为平台原生后端（Windows WinUSB、Linux usbfs、macOS IOKit）；libusb 与 HarmonyOS DDK 是显式 opt-in。原生 USB 会话按物理路径精确筛选接口，接口申请失败要立即释放本会话已申请的资源。
- 每设备 `DeviceCommandDispatcher` 是 FIFO、严格串行；结果与异常通过提交的 `Task` 返回。流式工作（logcat、shell streaming、投屏）不得走命令池。`DeviceLease` 按命令池 → 会话 → 传输 → 端点占用的顺序释放且只释放一次。
- 取消只影响该次提交；Dispose 取消队列并等待正在执行的命令真正结束。取消原生传输时不得提前复用缓冲区。
- `Native/LibUsb` 按 RID 随包提供，`SHA256SUMS` 记录来源；`COPYING` 以 `libusb-LICENSE.txt` 复制。缺少当前 RID 的二进制只警告并禁用 libusb，除非通过 `CommKit.SetLibusbLibraryPath` / `LibUsbRuntime.SetLibraryPath` 显式注册绝对路径。插入 Comm 与宿主之间的引用项目必须保留 RID 传递。
- Comm 不启动或调用 adb/hdc 可执行文件；server 存活只做 TCP 探测。HDC 关闭优先走 `HdcServerClient.KillAsync` 的 host-channel `kill`；仅当 greeting 未宣告 `CMD_SERVER_KILL` 时，允许读取 pid 文件并在校验进程名与可执行路径后终止该单个进程。

## Windows 驱动与 WinUSB 接管

- `Transports/Usb/Drivers/` 是全部 Windows 驱动逻辑的唯一位置：`WinUsbDriverInstaller` 只做设备接管（`QueryState`/`InstallTakeover`/`UninstallTakeover`），`WinUsbSelfSignedDriverPackage` 生成设备级 INF、自签代码签名证书、目录（catalog）签名并把证书写入本机 `Root` 与 `TrustedPublisher`，`WinUsbTakeoverNativeMethods` 保存 crypt32/advapi32/wintrust/mssign32 的 P/Invoke。厂商驱动包由 `DriverPackageCatalog`（只带本地化 key 后缀）、`DriverPackageAssets`（唯一接触内嵌资源的地方）、`DriverPackageInfReader` 与 `DriverPackageInstaller` 组成。
- 接管失败必须如实报错：取消接管先读回设备绑定，绑定的 provider 不是 `UotanStudio` 时拒绝删除并报失败；厂商包卸载只遍历 `%SystemRoot%\inf\oem*.inf` 并按 provider 与服务商 catalog 双匹配，同一 catalog 但 provider 不同时同样拒绝。删除成功后固定移除暂存包与接管证书，不做“留着下次用”的优化；接管开始时顺带清除已退役驱动签名实现遗留的 `CN=UotanStudio Driver Signing` 证书（本机与当前用户信任存储，失败忽略，不能因此挡住接管）。
- 暂存走 `SetupCopyOEMInfW(SPOST_PATH)`，随后就地绑定：`CM_Get_DevNode_Status` 报 `CM_PROB_FAILED_INSTALL` 的在位节点用 `CM_Reenumerate_DevNode(RETRY_INSTALLATION | ASYNCHRONOUS)` 让 PnP 排队重装并选中刚暂存的包，调用立即返回，绑定与问题码由有界轮询读回；轮询仍未绑定时才在提权助手进程里调用 `UpdateDriverForPlugAndPlayDevices`（`INSTALLFLAG_FORCE`/`INSTALLFLAG_NONINTERACTIVE`）就地更新。禁止同步重枚举与 `CM_Query_And_Remove_SubTree`：卡住的设备节点会让它们无限期不返回，`CM_Query_And_Remove_SubTree` 还会把节点留在无法启动的待移除状态。`UpdateDriverForPlugAndPlayDevices`/`DiInstallDriverW` 只允许出现在助手进程，宿主进程不得直接调用，它们会长时间阻塞枚举。宿主等待提权助手有上限（90 秒），超时终止子进程并按失败上报，避免卡住的设备冻结界面。
- 生成的设备级 INF 只导入系统 `winusb.inf` 的 `WINUSB.NT`、`WINUSB.NT.Services` 与 `WINUSB.NT.HW` 段，不重述 WinUSB 服务、不写 `AddService`/`KmdfService`，否则设备装得上却启动不了；设备描述按识别到的模式命名（`UotanStudio WinUSB (Rockchip RKDL)` 等），未识别出模式时回退硬件 ID。
- 驱动包负载是 `UotanStudio.Assets` 的 `drivers/<packageId>` 资源包，本库经 `AssetStore` 读取，自己不内嵌任何程序集资源。资源路径只能是纯文件名，所以包内目录用 `__` 编码（`x64__usb2ser.sys` 代表 `x64/usb2ser.sys`），`DriverPackageAssets` 物化时还原目录，因为暂存要求原始布局。换包或换源后跑 `dotnet run tools/compress-embedded-assets.cs` 重生成帧与清单，来源与排除规则见 `reference/driver-pkg/README.md`。
- 只内置可独立暂存的 INF/CAT/SYS/DLL：`wch-serial`（只有 EXE 自解压安装器）与 `huawei-hisuite`（许可与体积）不内置，只以 `UnavailableReasonKey`（`SelfExtractorOnly`/`LicenseAndSize`）在目录中列出，安装入口对它们直接失败。

## 危险操作

- EDL `FlashBundleOptions.WipeConfirmation` 必须携带精确短语 `WIPE`，整存储擦除还需要 `AllowEntireStorageWipe`；两道门都在生成命令文档之前拒绝，设备零写入。
- MTK preloader 内存写/跳转、EDL streaming 写与 flash mode 等危险入口必须有显式确认参数；未确认时在任何字节上线前失败。
- 已被移除的厂商漏洞利用入口不得以“方便”为由恢复；重新暴露需要真实设备证据与专用危险确认，而不是只加一个按钮。

## Apple 协议族

- Apple 相关协议不要接到设备扫描上：`DeviceControllerFactory` 只注册 USB/adb/HDC 发现源，`Devices/Discovery` 不提供 usbmuxd 发现源，`CommDeviceTransportOpener` 不识别 usbmuxd 端点路由，宿主扫描也不把 usbmuxd 端点映射为设备。不要为扫描恢复 usbmuxd 探针、端点映射或新增 Apple 发现源；`Protocols/Apple/` 的实现只经显式直连 USB 会话消费。
- `Protocols/Apple/` 是 Apple 设备（iPhone/iPad）的唯一实现处：usbmuxd 管道客户端（Windows TCP 27015 / Unix 套接字，可用 `USBMUXD_SOCKET_ADDRESS` 覆盖）、lockdownd 与配对、AFC、syslog 中继、iOS 17.4+ 受信隧道（`Protocols/Apple/Tunnel/`）与直连 USB 复用（`AppleUsbMuxDirect`，接口 class 0xFF/subclass 0xFE/protocol 2）。
- 直连 USB 复用与 Apple Mobile Device Service 驱动互斥：本库不做任何驱动替换；直连仅在 WinUSB 可打开接口时可用，属于显式 opt-in 路径。
- 配对记录保存在 `%LOCALAPPDATA%/UotanStudio/apple-lockdown`，可只读导入 `C:\ProgramData\Apple\Lockdown` 的 Apple 记录；配对私钥是该目录的敏感数据，不得写入日志或诊断输出。
- 所有 plist/TTLV/隧道帧的解码都有界（见 `ApplePlistLimits` 与各编解码器的上限常量），设备返回数据按不可信输入处理；流式工作（syslog、投屏）不进命令池。

## 文档与验证

- 本库设置 `GenerateDocumentationFile`、`TreatWarningsAsErrors` 与 `AllowUnsafeBlocks`；公共类型和成员必须有中英双语 XML 注释。`IsRidAgnostic=false` 保留宿主集成的 RID 传播；宿主只经公共契约消费本库，本库不向宿主或任何生产项目开放 `InternalsVisibleTo`，只保留对 `UotanStudio.Tests` 与 `UotanStudio.Benchmarks` 的测试授权。
- 真实设备测试优先只读；重启、刷写、提权、安装、卸载、清日志或清数据只在用户明确要求且已隔离时执行。缺少环境变量或设备时跳过并写明原因，跳过不等于通过。
- 定向验证：

```text
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Devices --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Protocols --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.HeadlessControllerGatewayTests --minimum-expected-tests 1
```

- 设备 I/O 归属与 scrcpy 分离没有自动架构门禁，只能靠评审；Windows-only 构建与测试不证明 Linux/macOS/HarmonyOS 后端或 NativeAOT 行为。