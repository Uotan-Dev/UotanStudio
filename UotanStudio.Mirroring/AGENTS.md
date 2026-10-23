# UotanStudio.Mirroring 项目指令

在本目录内改 scrcpy 协议、会话或控制消息前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 命名空间是 `UotanStudio.Mirroring`；本项目只引用 `UotanStudio.Comm`，不得引用 `UotanStudio.Media`、`UotanStudio.Firmware`、`UotanStudio.Projects` 或宿主 `UotanStudio`。scrcpy 不属于 Comm 协议层，也不得反向并入 Comm。
- 设备 I/O 全部经 Comm 契约：`IAdbDeviceSession`（`OpenStream`/`OpenServiceStream`/`CreateSyncClient`）、`AdbServerServices`、`DeviceLease`。本层不得自建 socket、USB 句柄或启动 adb 进程。
- ADB server 复用由宿主决定：`DeviceMonitorService.StartMirror` 在目标由 server 管理时复用已运行的 server，不新起 server；否则使用原生 USB 会话。两种来源分别对应 `MirrorSessionFacade.StartScrcpyOverAdbServer` 与 `StartScrcpyOverUsbSession`，`ScrcpyClient` 只是内部实现。
- server jar 的获取、长度与 SHA-256 校验、暂存后原子发布、缓存摘要复验与随包回退都在宿主 `ScrcpyServerService`；本层只接收本地路径并把 jar 推到 `/data/local/tmp/scrcpy-server.jar`。
- 呈现不在本层：本层只交付压缩视频/音频包与控制消息；GPU 呈现与 CPU 位图回退在宿主 `ScreenMirrorGpuPresenter` / `ScreenMirrorViewModel`。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。

## 公共门面与内部实现

- 宿主只经 `MirrorSessionFacade` 启动和管理投屏会话：`StartScrcpyOverAdbServer`、`StartScrcpyOverUsbSession`、`StartOhscrcpyOverHdcSession`、`IsOhscrcpyInstalled`、`InstallOhscrcpyHap`。工厂负责推送/启动服务端、打开 shell 或 HDC 通道、构造客户端与会话，并保持 `ManagedMirrorSession` / `ManagedOhscrcpySession` 的释放顺序。
- `ScrcpyClient`、`OhscrcpyClient`、`ScrcpyServerManager`、`OhscrcpyServerManager`、`OhscrcpyHdcTunnel` 是 internal 实现，不属于公共 API；测试经 `InternalsVisibleTo("UotanStudio.Tests")` 访问。`ManagedMirrorSession` 与 `ManagedOhscrcpySession` 类型保持 public 供宿主持有，但构造函数 internal，只能由 facade 创建。
- `ScrcpyConstants`、`ScrcpyControlMessageWriter`、`ScrcpyServerOptions`、`OhscrcpyConstants`、`OhscrcpyControlMessageWriter`、`OhscrcpyProtocol` 的帧与消息记录保持 public：宿主 ViewModel/View 仍直接使用控制常量与编码器，帧类型跨层传递。控制写入器暴露的是线协议编码而非会话管理入口，因此不随客户端类型收窄。
- ohscrcpy 启动选项由宿主 `OhscrcpyLaunchOptions` 拆成基本参数传入 facade，Mirroring 不引用宿主类型；新增启动参数时同步宿主记录与 facade 形参。

## 协议与生命周期

- `ScrcpyConstants` 与部署的 `scrcpy-server` jar 强绑定，当前是 5.x 码流布局：session 标志 bit 63、config 标志 bit 62、keyframe 标志 bit 61，时间戳掩码为 `keyframe - 1`。改这些位必须同时换 jar；只改一边会把会话包读成帧、把负载长度解释成视频高度。
- 版本不是常量：`ScrcpyServerOptions.ServerVersion` 必须来自实际产物，只接受点分十进制且最长 16 字符（同时防 shell 注入）。`BuildArguments` 首位必须是该版本，并保留 `send_stream_meta`/`send_frame_meta`/`tunnel_forward` 等 5.x 参数；不要使用已退役的 `send_codec_meta`。
- 生命周期是 Push → `StartServer` 打开 `shell:CLASSPATH=... app_process` → shell 流保持到会话结束（关闭即服务端退出）→ `StopServerAsync` 发 pkill 并最多等 3 秒。`ManagedMirrorSession.Dispose` 依序释放 shell、ADB 流与 `DeviceLease`。
- `Connect` 在会话存活期最多重试 20 次、间隔 250 ms；只对 socket 未就绪重试。非 0 哑字节立即失败；握手失败时把服务端 `ERROR:` 行折进 `IOException`，不要吞掉原始异常。
- 所有从设备读取的长度都必须有界：视频访问单元 ≤32 MiB、音频包 ≤4 MiB、设备剪贴板 ≤1 MiB、写入剪贴板 UTF-8 ≤300 字节。越界抛异常，不截断续读。
- 视频、音频、控制 socket 由调用方分别泵取；本层只转发。服务端日志经 `logSink` 回调，可能来自后台线程，不得依赖宿主 `LogService`。
- 剪贴板消息、控制消息与显示电源消息必须使用 `ScrcpyControlMessageWriter` 的编码；`SET_DISPLAY_POWER` 后必须带布尔字节，否则服务端会把下一条消息首字节当标志并错位。
- ohscrcpy（HDC 目标）是独立协议，不是 scrcpy 兼容流：设备 53535 端口上的单条 TCP 连接按大端 `type(4) + length(4) + payload` 分帧，`0x02` 视频配置、`0x03` 视频帧（`flags(1) + pts(8) + Annex-B NAL`）、`0x10` 控制、`0x20` 设备状态、`0x01` 心跳；服务端 30 秒收不到任何字节即断开，客户端按心跳间隔发送。`OhscrcpyClient` 把帧包装成 `ScrcpyVideoFrame`（config / key-frame 标志位与 scrcpy 一致），GPU 解码管线无需第二套实现；控制消息经 `ScrcpyToOhscrcpyControlAdapter` 翻译，剪贴板与 UHID 没有对应物。
- 设备 I/O 仍归 Comm/宿主：`IOhscrcpyTunnel` 只接收已转发的 `Stream`；HDC 租约在宿主 `DeviceMonitorService.StartOhscrcpyMirror`，`MirrorSessionFacade.StartOhscrcpyOverHdcSession` 在 Mirroring 内部完成包检查、`aa start`、通道与客户端构造，`OhscrcpyServerManager` 与 `OhscrcpyHdcTunnel` 是实现细节；`fport` 映射与回环套接字在 Comm `HdcForwardTunnel`。设备端产物是 HAP（需系统证书签名与权限白名单），不是推送的二进制。

## 验证

```text
dotnet build UotanStudio.Mirroring/UotanStudio.Mirroring.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyVideoMetadataTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyClientConnectTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyServerProtocolOptionsTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.MirrorSessionFacadeTests --minimum-expected-tests 1
```

- 真实投屏结论目前来自模拟器回环端口；设备→客户端剪贴板推送未在硬件上观察到，不能写成已验证。缺少 `Desktop/Assets/scrcpy-server.jar` 时，出厂摘要测试按 `VERIFICATION.md` 的说明跳过而非通过。