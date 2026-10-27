namespace UotanStudio.Mirroring;

/// <summary>
/// Constants of the DevEco-style OpenHarmony mirror path: the UI Test control socket, the gRPC video
/// socket published by the pushed device capture library, and the input key codes it injects.
/// <para>DevEco 风格 OpenHarmony 投屏路径的常量：UI Test 控制 socket、推送到设备的采集库发布的 gRPC 视频
/// socket，以及它注入的输入键码。</para>
/// </summary>
/// <remarks>
/// The device side is not an installed HAP. A capture library is pushed to <c>/data/local/tmp</c> and loaded
/// as a UI Test extension by <c>/system/bin/uitest start-daemon singleness --extension-name ...</c>; the
/// extension serves H.264 access units over a standard gRPC server, while touch and mouse input travel over
/// the UI Test JSON socket owned by the same daemon. These constants are tied to the libscrcpy server
/// libraries shipped under <c>ohos/device</c> and to the UI Test version they target.
/// <para>设备端不是已安装的 HAP。采集库被推送到 <c>/data/local/tmp</c>，并由
/// <c>/system/bin/uitest start-daemon singleness --extension-name ...</c> 作为 UI Test 扩展加载；扩展通过标准
/// gRPC 服务提供 H.264 访问单元，触摸与鼠标输入则经由同一守护进程的 UI Test JSON socket。以下常量与
/// <c>ohos/device</c> 下随包提供的 libscrcpy 服务库及其目标 UI Test 版本绑定。</para>
/// </remarks>
public static class OhscrcpyConstants
{
    /// <summary>Device directory that receives the pushed capture library and UI Test agent.
    /// <para>接收推送的采集库与 UI Test 代理的设备目录。</para></summary>
    public const string DeviceRuntimeDirectory = "/data/local/tmp";

    /// <summary>Absolute path of the device UI Test executable. <para>设备 UI Test 可执行文件的绝对路径。</para></summary>
    public const string UiTestExecutable = "/system/bin/uitest";

    /// <summary>UI Test command that starts the long-lived control daemon, without an extension.
    /// <para>启动不带扩展的常驻控制守护进程的 UI Test 命令。</para></summary>
    public const string UiTestDaemonCommand = "start-daemon singleness";

    /// <summary>Local file name of the reusable UI Test control agent on the device.
    /// <para>设备上可复用的 UI Test 控制代理文件名。</para></summary>
    public const string ControlAgentFileName = "agent.so";

    /// <summary>Prefix used for the unique pushed capture library; the rest is a random suffix.
    /// <para>推送的唯一采集库文件名前缀；其余部分为随机后缀。</para></summary>
    public const string ExtensionNamePrefix = "libuotan_hdc_mirror_";

    /// <summary>Virtual socket that serves the gRPC video stream on UI Test versions that use Unix sockets.
    /// <para>使用 Unix 套接字的 UI Test 版本上提供 gRPC 视频流的虚拟 socket。</para></summary>
    public const string UnixVideoEndpoint = "localabstract:scrcpy_grpc_socket";

    /// <summary>Virtual socket that serves the UI Test JSON control RPC on Unix-socket versions.
    /// <para>Unix 套接字版本上提供 UI Test JSON 控制 RPC 的虚拟 socket。</para></summary>
    public const string UnixControlEndpoint = "localabstract:uitest_socket";

    /// <summary>TCP port that serves the gRPC video stream on older UI Test versions.
    /// <para>较早 UI Test 版本上提供 gRPC 视频流的 TCP 端口端点。</para></summary>
    public const string LegacyVideoEndpoint = "tcp:5000";

    /// <summary>TCP port that serves the UI Test JSON control RPC on older UI Test versions.
    /// <para>较早 UI Test 版本上提供 UI Test JSON 控制 RPC 的 TCP 端口端点。</para></summary>
    public const string LegacyControlEndpoint = "tcp:8012";

    /// <summary>First UI Test version that exposes Unix virtual sockets instead of fixed TCP ports.
    /// <para>开始暴露 Unix 虚拟 socket（而非固定 TCP 端口）的首个 UI Test 版本。</para></summary>
    public static readonly Version UnixSocketMinimumVersion = new(6, 0, 2, 1);

    /// <summary>Oldest UI Test version whose extension ABI this project supports.
    /// <para>本项目支持的扩展 ABI 所需的最低 UI Test 版本。</para></summary>
    public static readonly Version MinimumUiTestVersion = new(4, 1, 4, 6);

    /// <summary>gRPC path of the server-streaming video call. <para>服务端流式视频调用的 gRPC 路径。</para></summary>
    public const string VideoCallPath = "/ScrcpyService/onStart";

    /// <summary>gRPC path of the unary key-frame request call. <para>一元关键帧请求调用的 gRPC 路径。</para></summary>
    public const string KeyFrameCallPath = "/ScrcpyService/onRequestIDRFrame";

    /// <summary>Payload key that carries H.264 bytes inside a <c>ReplyMessage</c>.
    /// <para><c>ReplyMessage</c> 中承载 H.264 字节的负载键。</para></summary>
    public const string VideoPayloadKey = "data";

    /// <summary>Byte length of the gRPC message prefix (compressed flag plus big-endian length).
    /// <para>gRPC 消息前缀的字节长度（压缩标志加一大端长度）。</para></summary>
    public const int GrpcHeaderLength = 5;

    /// <summary>Largest accepted gRPC message payload, 32 MiB. <para>接受的 gRPC 消息负载上限，32 MiB。</para></summary>
    public const int MaximumGrpcMessageLength = 32 * 1024 * 1024;

    /// <summary>Largest accepted UI Test JSON response, 1 MiB. <para>接受的 UI Test JSON 响应上限，1 MiB。</para></summary>
    public const int MaximumControlMessageLength = 1024 * 1024;

    /// <summary>Default capture frame rate passed to the device extension.
    /// <para>传给设备扩展的默认采集帧率。</para></summary>
    public const int DefaultFrameRate = 30;

    /// <summary>Default capture bitrate in bits per second. <para>默认采集码率（比特/秒）。</para></summary>
    public const int DefaultBitRate = 8_000_000;

    /// <summary>Lowest accepted capture frame rate. <para>接受的最低采集帧率。</para></summary>
    public const int MinimumFrameRate = 1;

    /// <summary>Highest accepted capture frame rate. <para>接受的最高采集帧率。</para></summary>
    public const int MaximumFrameRate = 120;

    /// <summary>Lowest accepted capture bitrate in bits per second.
    /// <para>接受的最低采集码率（比特/秒）。</para></summary>
    public const int MinimumBitRate = 1_000_000;

    /// <summary>Highest accepted capture bitrate in bits per second.
    /// <para>接受的最高采集码率（比特/秒）。</para></summary>
    public const int MaximumBitRate = 50_000_000;

    /// <summary>Default key-frame interval passed to the device extension, in milliseconds.
    /// <para>传给设备扩展的默认关键帧间隔（毫秒）。</para></summary>
    public const int DefaultKeyFrameIntervalMs = 2_000;

    /// <summary>Upper bound for UTF-8 text sent through one <c>uinput</c> command.
    /// <para>单条 <c>uinput</c> 命令允许的 UTF-8 文本上限。</para></summary>
    public const int MaximumTextLength = 4096;

    /// <summary>Budget for one UI Test JSON request, in milliseconds.
    /// <para>单次 UI Test JSON 请求的预算（毫秒）。</para></summary>
    public const int ControlRequestTimeoutMs = 3_000;

    /// <summary>Budget for the UI Test socket forward and its handshake, in milliseconds.
    /// <para>UI Test socket 转发及其握手的预算（毫秒）。</para></summary>
    public const int ControlConnectTimeoutMs = 15_000;

    /// <summary>Budget for the video forward and the first access unit, in milliseconds.
    /// <para>视频转发与首个访问单元的预算（毫秒）。</para></summary>
    public const int VideoConnectTimeoutMs = 20_000;

    /// <summary>OpenHarmony <c>KEYCODE_HOME</c>. <para>OpenHarmony <c>KEYCODE_HOME</c>。</para></summary>
    public const int KeyCodeHome = 1;

    /// <summary>OpenHarmony <c>KEYCODE_BACK</c>. <para>OpenHarmony <c>KEYCODE_BACK</c>。</para></summary>
    public const int KeyCodeBack = 2;

    /// <summary>OpenHarmony <c>KEYCODE_VOLUME_UP</c>. <para>OpenHarmony <c>KEYCODE_VOLUME_UP</c>。</para></summary>
    public const int KeyCodeVolumeUp = 16;

    /// <summary>OpenHarmony <c>KEYCODE_VOLUME_DOWN</c>. <para>OpenHarmony <c>KEYCODE_VOLUME_DOWN</c>。</para></summary>
    public const int KeyCodeVolumeDown = 17;

    /// <summary>OpenHarmony <c>KEYCODE_POWER</c>. <para>OpenHarmony <c>KEYCODE_POWER</c>。</para></summary>
    public const int KeyCodePower = 18;

    /// <summary>
    /// OpenHarmony <c>KEYCODE_TAB</c>, the key of the task-switcher chord this path injects for the mirror's
    /// Recents button.
    /// <para>OpenHarmony <c>KEYCODE_TAB</c>，本路径为投屏「多任务」按钮注入的任务切换组合键所用的键。</para>
    /// </summary>
    /// <remarks>
    /// The value comes from the <c>keycode/KeyCodeMapBundle.properties</c> key map shipped by the DevEco Studio
    /// screen-mirroring plugin, where AWT <c>VK_TAB</c> (9) maps to <c>KEYCODE_TAB</c> (2049), and from its
    /// <c>DeviceController.recentApplication()</c> handler, which sends
    /// <c>uinput -K -d 2076 -d 2049 -u 2076 -u 2049</c> for its own Recent Apps button.
    /// <para>该值来自 DevEco Studio 投屏插件随附的 <c>keycode/KeyCodeMapBundle.properties</c> 键码表（AWT
    /// <c>VK_TAB</c>（9）映射为 <c>KEYCODE_TAB</c>（2049）），以及其
    /// <c>DeviceController.recentApplication()</c> 处理函数——该函数为自己的「最近应用」按钮发送
    /// <c>uinput -K -d 2076 -d 2049 -u 2076 -u 2049</c>。</para>
    /// </remarks>
    public const int KeyCodeTab = 2049;

    /// <summary>
    /// OpenHarmony <c>KEYCODE_META_LEFT</c>, the modifier of the task-switcher chord this path injects for the
    /// mirror's Recents button.
    /// <para>OpenHarmony <c>KEYCODE_META_LEFT</c>，本路径为投屏「多任务」按钮注入的任务切换组合键的修饰键。</para>
    /// </summary>
    /// <remarks>
    /// The value comes from the same DevEco Studio key map, where AWT <c>VK_META</c> (157) maps to
    /// <c>KEYCODE_META_LEFT</c> (2076); the device opens the recent-tasks view for the <c>META</c> + <c>TAB</c>
    /// chord, not for a dedicated single key.
    /// <para>该值来自同一份 DevEco Studio 键码表（AWT <c>VK_META</c>（157）映射为
    /// <c>KEYCODE_META_LEFT</c>（2076））；设备为 <c>META</c> + <c>TAB</c> 组合键打开最近任务视图，而不是为某个
    /// 单独的键。</para>
    /// </remarks>
    public const int KeyCodeMetaLeft = 2076;
}