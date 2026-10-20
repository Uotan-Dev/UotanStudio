# UotanStudio.Mirroring 项目指令

在本目录内改 scrcpy 协议、会话或控制消息前读这份文件。仓库级规则、构建命令与 Git 纪律见根 `AGENTS.md`。

## 边界

- 命名空间是 `UotanStudio.Services.Mirroring`；本项目只引用 `UotanStudio.Comm`，不得引用 `UotanStudio.Media`、`UotanStudio.Firmware`、`UotanStudio.Projects` 或宿主 `UotanStudio`。scrcpy 不属于 Comm 协议层，也不得反向并入 Comm。
- 设备 I/O 全部经 Comm 契约：`IAdbDeviceSession`（`OpenStream`/`OpenServiceStream`/`CreateSyncClient`）、`AdbServerServices`、`DeviceLease`。本层不得自建 socket、USB 句柄或启动 adb 进程。
- ADB server 复用由宿主决定：`DeviceMonitorService.StartMirror` 在目标由 server 管理时复用已运行的 server，不新起 server；否则使用原生 USB 会话。`ScrcpyClient` 的两个构造重载对应这两种来源。
- server jar 的获取、长度与 SHA-256 校验、暂存后原子发布、缓存摘要复验与随包回退都在宿主 `ScrcpyServerService`；本层只接收本地路径并把 jar 推到 `/data/local/tmp/scrcpy-server.jar`。
- 呈现不在本层：本层只交付压缩视频/音频包与控制消息；GPU 呈现与 CPU 位图回退在宿主 `ScreenMirrorGpuPresenter` / `ScreenMirrorViewModel`。
- csproj 设置 `GenerateDocumentationFile`，公共类型与成员要写中英双语 XML 注释；本项目不启用 `TreatWarningsAsErrors`，但不要用 `NoWarn` 压掉新增警告。

## 协议与生命周期

- `ScrcpyConstants` 与部署的 `scrcpy-server` jar 强绑定，当前是 5.x 码流布局：session 标志 bit 63、config 标志 bit 62、keyframe 标志 bit 61，时间戳掩码为 `keyframe - 1`。改这些位必须同时换 jar；只改一边会把会话包读成帧、把负载长度解释成视频高度。
- 版本不是常量：`ScrcpyServerOptions.ServerVersion` 必须来自实际产物，只接受点分十进制且最长 16 字符（同时防 shell 注入）。`BuildArguments` 首位必须是该版本，并保留 `send_stream_meta`/`send_frame_meta`/`tunnel_forward` 等 5.x 参数；不要使用已退役的 `send_codec_meta`。
- 生命周期是 Push → `StartServer` 打开 `shell:CLASSPATH=... app_process` → shell 流保持到会话结束（关闭即服务端退出）→ `StopServerAsync` 发 pkill 并最多等 3 秒。`ManagedMirrorSession.Dispose` 依序释放 shell、ADB 流与 `DeviceLease`。
- `Connect` 在会话存活期最多重试 20 次、间隔 250 ms；只对 socket 未就绪重试。非 0 哑字节立即失败；握手失败时把服务端 `ERROR:` 行折进 `IOException`，不要吞掉原始异常。
- 所有从设备读取的长度都必须有界：视频访问单元 ≤32 MiB、音频包 ≤4 MiB、设备剪贴板 ≤1 MiB、写入剪贴板 UTF-8 ≤300 字节。越界抛异常，不截断续读。
- 视频、音频、控制 socket 由调用方分别泵取；本层只转发。服务端日志经 `logSink` 回调，可能来自后台线程，不得依赖宿主 `LogService`。
- 剪贴板消息、控制消息与显示电源消息必须使用 `ScrcpyControlMessageWriter` 的编码；`SET_DISPLAY_POWER` 后必须带布尔字节，否则服务端会把下一条消息首字节当标志并错位。

## 验证

```text
dotnet build UotanStudio.Mirroring/UotanStudio.Mirroring.csproj --nologo
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyVideoMetadataTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyClientConnectTests --minimum-expected-tests 1
dotnet test --project UotanStudio.Tests/UotanStudio.Tests.csproj --no-build --filter-class UotanStudio.Tests.Mirroring.ScrcpyServerProtocolOptionsTests --minimum-expected-tests 1
```

- 真实投屏结论目前来自模拟器回环端口；设备→客户端剪贴板推送未在硬件上观察到，不能写成已验证。缺少 `Desktop/Assets/scrcpy-server.jar` 时，出厂摘要测试按 `VERIFICATION.md` 的说明跳过而非通过。