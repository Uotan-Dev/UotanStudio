# UotanStudio.Mirroring 项目指令

在本目录内改 scrcpy 协议、会话或控制消息前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 命名空间是 `UotanStudio.Mirroring`；本项目只引用 `UotanStudio.Comm`，不得引用 `UotanStudio.Media`、`UotanStudio.Firmware`、`UotanStudio.Projects` 或宿主 `UotanStudio`。scrcpy 不属于 Comm 协议层，也不得反向并入 Comm。
- 设备 I/O 全部经 Comm 契约：`IAdbDeviceSession`（`OpenStream`/`OpenServiceStream`/`CreateSyncClient`）、`AdbServerServices`、`IHdcDeviceSession`、`DeviceLease`。本层不得自建 socket、USB 句柄或启动 adb/hdc 进程。
- ADB server 复用由宿主决定：`DeviceMonitorService.StartMirror` 在目标由 server 管理时复用已运行的 server，不新起 server；否则使用原生 USB 会话。两种来源分别对应 `MirrorSessionFacade.StartScrcpyOverAdbServer` 与 `StartScrcpyOverUsbSession`，`ScrcpyClient` 只是内部实现。
- server jar 的获取、长度与 SHA-256 校验、暂存后原子发布、缓存摘要复验与随包回退都在宿主 `ScrcpyServerService`；本层只接收本地路径并把 jar 推到 `/data/local/tmp/scrcpy-server.jar`。
- HDC 目标的设备端产物不是安装包：采集库与 UI Test 代理以 `Assets/ohos/device/*.so.zst` 的 zstd 帧加同目录 `manifest.txt` 文本清单内嵌随包分发，首次使用时由 `OhscrcpyDeviceAssets` 物化到 `%TEMP%/UotanStudio/ohos-device/<清单哈希前 16 位>/` 并校验长度与 SHA-256；`OhscrcpyServerManager.RuntimeDirectory` 返回该目录，选择与推送仍操作真实文件。改原始 `.so` 后运行 `dotnet run tools/compress-embedded-assets.cs` 重新生成帧与清单；不要在运行时下载或查询 Release 来补设备库。
- `Assets/ohos/device/` 的 15 个文件提取自本机 DevEco Studio 26.0.0.821 的 `plugins/screen-mirroring/lib/hosScrcpy-1.0.20-beta.jar`（JAR SHA-256 `62cba3701b635a9d9c906a70f18cbc43efd60b7bfd61e2bb18fb88b2c7dfb187`）。来源、逐文件摘要与再分发边界见 `Assets/ohos/README.md`；升级 DevEco 后用 `tools/deveco-mirroring/extract.ps1` 重新提取，不要手工改二进制。`libscrcpy_server0.z.so` 作为最后的旧版回退候选保留。
- 呈现不在本层：本层只交付压缩视频/音频包与控制消息；GPU 呈现与 CPU 位图回退在宿主 `ScreenMirrorGpuPresenter` / `ScreenMirrorViewModel`。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。

## 公共门面与内部实现

- 宿主只经 `MirrorSessionFacade` 启动和管理投屏会话：`StartScrcpyOverAdbServer`、`StartScrcpyOverUsbSession`、`StartOhscrcpyOverHdcSession`。HDC 目标走 UI Test 扩展路径，没有 HAP 安装/更新 API；不要再加 `IsOhscrcpyInstalled`、`InstallOhscrcpyHap` 一类入口。工厂负责设备探测、推送、守护进程、fport 转发与客户端构造，并保持 `ManagedMirrorSession` / `ManagedOhscrcpySession` 的释放顺序。
- `ScrcpyClient`、`OhscrcpyClient`、`ScrcpyServerManager`、`OhscrcpyServerManager`、`OhoscrcpyGrpcTransport` 是 internal 实现，不属于公共 API；测试经 `InternalsVisibleTo("UotanStudio.Tests")` 访问。`ManagedMirrorSession` 与 `ManagedOhscrcpySession` 类型保持 public 供宿主持有，但构造函数 internal，只能由 facade 创建。
- `ScrcpyConstants`、`ScrcpyControlMessageWriter`、`ScrcpyServerOptions`、`OhscrcpyConstants`、`OhscrcpyProtocol`、`OhscrcpyLaunchOptions`、`OhscrcpyControlAction` 的帧与消息记录保持 public：宿主 ViewModel/View 仍直接使用控制常量与编码器，帧类型跨层传递。控制写入器暴露的是线协议编码而非会话管理入口，因此不随客户端类型收窄。
- 采集参数用 public 记录 `OhscrcpyLaunchOptions`（帧率、码率、关键帧间隔）在 facade 边界校验；Mirroring 不引用宿主类型，新增启动参数时同步该记录、`OhscrcpyConstants` 的上下界与设备命令。

## 协议与生命周期

- `ScrcpyConstants` 与部署的 `scrcpy-server` jar 强绑定，当前是 5.x 码流布局：session 标志 bit 63、config 标志 bit 62、keyframe 标志 bit 61，时间戳掩码为 `keyframe - 1`。改这些位必须同时换 jar；只改一边会把会话包读成帧、把负载长度解释成视频高度。
- 版本不是常量：`ScrcpyServerOptions.ServerVersion` 必须来自实际产物，只接受点分十进制且最长 16 字符（同时防 shell 注入）。`BuildArguments` 首位必须是该版本，并保留 `send_stream_meta`/`send_frame_meta`/`tunnel_forward` 等 5.x 参数；不要使用已退役的 `send_codec_meta`。
- scrcpy 生命周期是 Push → `StartServer` 打开 `shell:CLASSPATH=... app_process` → shell 流保持到会话结束（关闭即服务端退出）→ `StopServerAsync` 发 pkill 并最多等 3 秒。`ManagedMirrorSession.Dispose` 依序释放 shell、ADB 流与 `DeviceLease`。
- `Connect` 在会话存活期最多重试 20 次、间隔 250 ms；只对 socket 未就绪重试。非 0 哑字节立即失败；握手失败时把服务端 `ERROR:` 行折进 `IOException`，不要吞掉原始异常。
- 所有从设备读取的长度都必须有界：视频访问单元 ≤32 MiB、音频包 ≤4 MiB、设备剪贴板 ≤1 MiB、写入剪贴板 UTF-8 ≤300 字节、UI Test JSON 响应 ≤1 MiB、`uinput` 文本 ≤4096 字节。越界抛异常，不截断续读。
- 视频、音频、控制 socket 由调用方分别泵取；本层只转发。服务端日志经 `logSink` 回调，可能来自后台线程，不得依赖宿主 `LogService`。
- 剪贴板消息、控制消息与显示电源消息必须使用 `ScrcpyControlMessageWriter` 的编码；`SET_DISPLAY_POWER` 后必须带布尔字节，否则服务端会把下一条消息首字节当标志并错位。
- ohscrcpy（HDC 目标）既不是 scrcpy 兼容流，也不是已安装的 HAP：设备端是推送到 `/data/local/tmp` 的采集 `.so`，由 `/system/bin/uitest start-daemon singleness --extension-name ...` 作为 UI Test 扩展加载。`OhscrcpyConstants` 固定了 `uitest` 路径、设备目录、扩展名前缀、gRPC 路径、Unix/TCP 端点与版本门槛；改这些常量必须同步替换 `ohos/device` 下的库。
- 设备准备顺序固定：探测 UI Test 版本（低于 `MinimumUiTestVersion` 失败，高于 `UnixSocketMinimumVersion` 才用 Unix 虚拟 socket）→ 复用或启动不带扩展的 `uitest start-daemon singleness` 控制守护进程；需要新建控制守护进程时推送控制代理，替换前备份并在清理时恢复 → 从 `ohos/device` 选择与设备架构/版本匹配的采集库，以唯一文件名推送到 `/data/local/tmp` → 启动扩展：`uitest start-daemon singleness --extension-name <唯一名> -scale 1 -frameRate ... -bitRate ... -p 5000 -iFrameInterval ... -repeatInterval ...`。
- 通道仍归 Comm：`MirrorSessionFacade.StartOhscrcpyOverHdcSession` 在 Mirroring 内部完成探测、推送与进程启动，设备 socket 只经 Comm `HdcForwardTunnel` 转发到回环端口；直连会话走会话自带的转发管理器，server 会话回退 server 的 `fport` 命令。Unix 版本转发 `localabstract:scrcpy_grpc_socket` 与 `localabstract:uitest_socket`，旧版本转发 `tcp:5000` 与 `tcp:8012`。本层不自建 socket、不启动 `hdc` 进程。
- 视频是标准 gRPC HTTP/2：`/ScrcpyService/onStart` 服务端流返回 `ReplyMessage`，`payload["data"]` 是 Annex-B H.264 访问单元；`/ScrcpyService/onRequestIDRFrame` 请求关键帧。没有独立编码配置消息，SPS/PPS 与尺寸从首个访问单元解析（`OhscrcpyProtocol`）。`OhoscrcpyGrpcTransport` 使用明文 HTTP/2 先验知识连接，消息前缀是 1 字节压缩标志 + 4 字节大端长度，拒绝压缩消息；帧包装成 `ScrcpyVideoFrame`（config / key-frame 标志与 scrcpy 一致），GPU 解码管线无需第二套实现。
- 控制分两套通道：触摸、鼠标与轴手势走 UI Test JSON socket，按键与文本走 shell `uinput`；单次 JSON 请求预算 3 秒，连接与首帧预算见 `OhscrcpyConstants`。剪贴板与 UHID 没有对应物，不得假装支持。
- 清理只处理本会话拥有的对象：只停止 `ControlDaemonHandle` / `VideoDaemonHandle` 记录的 pid，删除本会话推送的扩展与控制代理，恢复被替换的代理备份，然后按 转发 → 会话 → 租约 的顺序释放。不得按进程名 `pkill`，也不得删除非本会话创建的文件。

## 验证

```text
dotnet build UotanStudio.Mirroring/UotanStudio.Mirroring.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Media.OhscrcpyProtocolTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.OhscrcpyServerManagerTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.OhscrcpyClientTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyToOhscrcpyControlAdapterTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.MirrorSessionFacadeTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyVideoMetadataTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyClientConnectTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyServerProtocolOptionsTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Devices.HdcSessionRoutingTests --minimum-expected-tests 1
```

- 单元验证范围：gRPC 帧与 `ReplyMessage` 解析、SPS 几何（`UotanStudio.Tests.Media.OhscrcpyProtocolTests`）；控制代理/采集库选择与设备启动命令构造（`OhscrcpyServerManagerTests`）；客户端 gRPC 解复用、视频元数据与控制分发（`OhscrcpyClientTests`）；scrcpy 控制消息到 UI Test JSON/uinput 的映射（`ScrcpyToOhscrcpyControlAdapterTests`）；facade 参数、端口与采集参数校验（`MirrorSessionFacadeTests`）；HDC 会话路由决策（`UotanStudio.Tests.Devices.HdcSessionRoutingTests`，覆盖 server 拥有目标、无 server 与 ServerOnly 拒绝直连）。
- 直连控制/转发全链路（`fport` → gRPC/UI Test JSON）没有本轮实测：只能靠受 `UOTAN_HDC_MIRROR_DIRECT` 门控的 `OhscrcpyHdcIntegrationTests` 覆盖（接受 connect key 或 `host:port`）。本轮对 `5XM0225C18001159` 的尝试因 DevEco HDC server 占用并被 Comm fail-closed USB 策略拒绝直连而未通过；停止 HDC server 或在无 server 主机上重跑前，跳过/失败都不算通过。
- server 会话的投屏链路（server 路由的 shell 探测、sync 文件推送与 `fport` 回退）同样没有实测：`OhscrcpyHdcIntegrationTests` 只覆盖直连端点，本机有 hdc server 时的推送、转发与控制尚未在硬件上跑通，不能写成已验证。
- scrcpy 的真实投屏结论目前来自模拟器回环端口；设备→客户端剪贴板推送未在硬件上观察到，不能写成已验证。缺少 `Desktop/Assets/scrcpy-server.jar` 时，出厂摘要测试按 `VERIFICATION.md` 的说明跳过而非通过。
- HDC 视频路径未在本机硬件验证：连接中的 5XM0225C18001159（HarmonyOS 7.0.0.109）加载 `ohos/device` 下 2026 年的采集库时，`dlopen` 因需要更新的 protobuf/gRPC ABI 而失败；模拟器缺 `libgrpc.z.so`。设备库随包分发只代表文件存在，不代表可加载；视频解码/播放与 UI 渲染均未验证，不能写成已验证。