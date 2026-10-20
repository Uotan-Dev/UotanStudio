# UotanStudio.Comm 项目指令

在本目录内改设备发现、传输或协议会话前读这份文件。设备边界、层职责与协议细节见同目录 `README.md`；仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 所有权与路由

- 本项目是全部设备读写的唯一持有者：USB 句柄、TCP/UDP、串口、adb/HDC server 协议都在这里。宿主不得自行打开 socket、串口或 USB 句柄，只能消费 `DeviceController`/`DeviceLease`、`Contracts` 门面与公开协议客户端。`Transports/Usb/Backend` 是内部互操作，不是宿主 API。
- scrcpy 与其它呈现协议不在 Comm；`UotanStudio.Mirroring` 只引用 Comm，`UotanStudio.Media` 不引用 Comm。不要在 `Protocols/` 下新增呈现协议。
- `Routing/DebugBridgeRoute` 只以普通 TCP 探测 adb/HDC server 是否存在；只有连接被拒绝表示 server 不存在，超时/协议失败保持上次状态。正值缓存 2 秒，拒绝不缓存。每个 adb/HDC 操作读取该参数，不在操作中途临时改走另一条路。
- server 的 serial/connect key 是权威身份。目标存在、离线或未授权时仍属于 server 管理的目标；协议错误不得自动回退 USB。HDC 只服务其列表实际包含的 connect key，未注册键才允许直连。`DeviceEndpointId(Source,Transport,Address)` 区分发现来源。
- `UsbPhysicalIdentity.ServerPhysicalId` 只在精确序列号、实例 ID 尾部或仅大小写差异且唯一候选时返回物理标识；VID/PID、枚举顺序与列表位置绝不用于映射。

## USB 与生命周期

- `CommKit` 静态构造把 `UsbAccessPolicy` 配到 `DebugBridgeSessionOpener.RequireDirectUsb`。策略未配置时，debug 接口（class `0xff`、protocol `1`、subclass `0x42`/`0x50`）必须 fail-closed，不得先开句柄再检查。
- `UsbApiKind.Auto` 解析为平台原生后端（Windows WinUSB、Linux usbfs、macOS IOKit）；libusb 与 HarmonyOS DDK 是显式 opt-in。原生 USB 会话按物理路径精确筛选接口，接口申请失败要立即释放本会话已申请的资源。
- 每设备 `DeviceCommandDispatcher` 是 FIFO、严格串行；结果与异常通过提交的 `Task` 返回。流式工作（logcat、shell streaming、投屏）不得走命令池。`DeviceLease` 按命令池 → 会话 → 传输 → 端点占用的顺序释放且只释放一次。
- 取消只影响该次提交；Dispose 取消队列并等待正在执行的命令真正结束。取消原生传输时不得提前复用缓冲区。
- `Native/LibUsb` 按 RID 随包提供，`SHA256SUMS` 记录来源；`COPYING` 以 `libusb-LICENSE.txt` 复制。缺少当前 RID 的二进制只警告并禁用 libusb，除非通过 `CommKit.SetLibusbLibraryPath` / `LibUsbRuntime.SetLibraryPath` 显式注册绝对路径。插入 Comm 与宿主之间的引用项目必须保留 RID 传递。
- Comm 不启动、停止或调用 adb/hdc 可执行文件；server 存活只做 TCP 探测，HDC 关闭走 `HdcServerClient.KillAsync`。不要为生命周期引入 `Process`。

## 危险操作

- EDL `FlashBundleOptions.WipeConfirmation` 必须携带精确短语 `WIPE`，整存储擦除还需要 `AllowEntireStorageWipe`；两道门都在生成命令文档之前拒绝，设备零写入。
- MTK preloader 内存写/跳转、EDL streaming 写与 flash mode 等危险入口必须有显式确认参数；未确认时在任何字节上线前失败。
- 已被移除的厂商漏洞利用入口不得以“方便”为由恢复；重新暴露需要真实设备证据与专用危险确认，而不是只加一个按钮。

## 文档与验证

- 本库设置 `GenerateDocumentationFile`、`TreatWarningsAsErrors` 与 `AllowUnsafeBlocks`；公共类型和成员必须有中英双语 XML 注释。`IsRidAgnostic=false` 与 `InternalsVisibleTo("UotanStudio")` 是宿主集成契约，不要随意改。
- 真实设备测试优先只读；重启、刷写、提权、安装、卸载、清日志或清数据只在用户明确要求且已隔离时执行。缺少环境变量或设备时跳过并写明原因，跳过不等于通过。
- 定向验证：

```text
dotnet build UotanStudio.slnx --no-restore --nologo -c Debug
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Devices --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-namespace UotanStudio.Tests.Protocols --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Services.HeadlessControllerGatewayTests --minimum-expected-tests 1
```

- 设备 I/O 归属与 scrcpy 分离没有自动架构门禁，只能靠评审；Windows-only 构建与测试不证明 Linux/macOS/HarmonyOS 后端或 NativeAOT 行为。