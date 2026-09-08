using System.Globalization;
using System.Text;

namespace UotanStudio.Comm.Protocols.Scrcpy;

/// <summary>
/// Parameters passed to the scrcpy server process.
/// <para>传递给 scrcpy 服务端进程的参数。</para>
/// </summary>
public sealed class ScrcpyServerOptions
{
    /// <summary>Gets the random session id shared with the socket name.</summary>
    /// <summary>获取与 socket 名称共享的随机会话 ID。</summary>
    public uint Scid { get; init; } = GenerateScid();

    /// <summary>Gets the localabstract socket name suffix ("scrcpy_" + hex scid).</summary>
    /// <summary>获取 localabstract socket 名称（"scrcpy_" + 十六进制 scid）。</summary>
    public string SocketName => $"scrcpy_{Scid:x8}";

    /// <summary>Gets or sets the max video size (0 = no limit).</summary>
    /// <summary>获取或设置视频最大尺寸（0 表示不限制）。</summary>
    public int MaxSize { get; init; }

    /// <summary>Gets or sets the video bit rate in bits per second.</summary>
    /// <summary>获取或设置视频码率（bps）。</summary>
    public int VideoBitRate { get; init; } = 8_000_000;

    /// <summary>Gets or sets the max frame rate (0 = no limit).</summary>
    /// <summary>获取或设置最大帧率（0 表示不限制）。</summary>
    public int MaxFps { get; init; } = 60;

    /// <summary>Gets or sets the video codec ("h264" or "h265").</summary>
    /// <summary>获取或设置视频编码格式（"h264" 或 "h265"）。</summary>
    public string VideoCodec { get; init; } = "h264";

    /// <summary>Gets or sets whether video capture is enabled.</summary>
    /// <summary>获取或设置是否启用视频采集。</summary>
    public bool Video { get; init; } = true;

    /// <summary>Gets or sets whether audio capture is enabled.</summary>
    /// <summary>获取或设置是否启用音频采集。</summary>
    public bool Audio { get; init; }

    /// <summary>Gets or sets whether input control is enabled.</summary>
    /// <summary>获取或设置是否启用输入控制。</summary>
    public bool Control { get; init; } = true;

    /// <summary>Gets or sets the log level ("info", "debug", "warn", "error").</summary>
    /// <summary>获取或设置日志级别（"info"、"debug"、"warn"、"error"）。</summary>
    public string LogLevel { get; init; } = "warn";

    /// <summary>Gets or sets whether to keep the device awake while mirroring.</summary>
    /// <summary>获取或设置投屏期间是否保持设备唤醒。</summary>
    public bool StayAwake { get; init; } = true;

    /// <summary>Gets whether the server sends a dummy byte before metadata.</summary>
    /// <summary>获取服务端是否在元数据前发送哑字节。</summary>
    public bool SendDummyByte { get; init; } = true;

    /// <summary>Gets whether the server sends the 64-byte device name.</summary>
    /// <summary>获取服务端是否发送 64 字节设备名。</summary>
    public bool SendDeviceMeta { get; init; } = true;

    /// <summary>Gets whether the server sends codec metadata (codec id + size).</summary>
    /// <summary>获取服务端是否发送编码器元数据（编码器 ID + 尺寸）。</summary>
    public bool SendCodecMeta { get; init; } = true;

    /// <summary>Builds the ordered server argument list (args[0] is the version).</summary>
    /// <summary>构建有序的服务端参数列表（args[0] 为版本号）。</summary>
    /// <returns>The argument strings. 参数字符串数组。</returns>
    public string[] BuildArguments()
    {
        var args = new List<string>
        {
            ScrcpyConstants.ServerVersion,
            $"scid={Scid:x8}",
            $"log_level={LogLevel}",
            $"max_size={MaxSize.ToString(CultureInfo.InvariantCulture)}",
            $"video_bit_rate={VideoBitRate.ToString(CultureInfo.InvariantCulture)}",
            $"max_fps={MaxFps.ToString(CultureInfo.InvariantCulture)}",
            $"video_codec={VideoCodec}",
            $"video={Bool(Video)}",
            $"audio={Bool(Audio)}",
            $"control={Bool(Control)}",
            $"stay_awake={Bool(StayAwake)}",
            "send_device_meta=true",
            "send_frame_meta=true",
            "send_dummy_byte=true",
            "send_codec_meta=true",
            "cleanup=true",
            $"socket_name={SocketName}",
        };
        return [.. args];
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static uint GenerateScid()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }

    /// <summary>Builds the full <c>app_process</c> command line for the shell.</summary>
    /// <summary>构建 shell 执行的完整 <c>app_process</c> 命令行。</summary>
    /// <returns>The shell command string. shell 命令字符串。</returns>
    public string BuildShellCommand()
    {
        var sb = new StringBuilder();
        sb.Append("CLASSPATH=").Append(ScrcpyConstants.ServerDevicePath);
        sb.Append(" app_process / ").Append(ScrcpyConstants.ServerMainClass);
        foreach (var arg in BuildArguments())
        {
            sb.Append(' ').Append(arg);
        }

        return sb.ToString();
    }
}
