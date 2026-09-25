using System.Globalization;
using System.Text;

namespace UotanStudio.Services.Mirroring;

/// <summary>
/// Parameters passed to the scrcpy server process.
/// <para>传递给 scrcpy 服务端进程的参数。</para>
/// </summary>
public sealed class ScrcpyServerOptions
{
    /// <summary>Random session identifier shared with the socket name.
    /// <para>与 socket 名称共享的随机会话 ID。</para></summary>
    /// <value>
    /// A 31-bit id from a cryptographic generator, also written into <see cref="SocketName"/> as eight hex digits.
    /// <para>由加密随机数发生器产生的 31 位 ID，同时以 8 位十六进制写入 <see cref="SocketName"/>。</para>
    /// </value>
    public uint Scid { get; init; } = GenerateScid();

    /// <summary>The localabstract socket name suffix ("scrcpy_" + hex scid).
    /// <para>localabstract socket 名称后缀（"scrcpy_" + 十六进制 scid）。</para></summary>
    /// <value>The name the server creates and every client socket connects to.
    /// <para>服务端创建、且每个客户端 socket 连接时使用的名称。</para></value>
    public string SocketName => $"scrcpy_{Scid:x8}";

    /// <summary>The maximum video edge size in pixels.
    /// <para>视频最长边像素上限。</para></summary>
    /// <value>0 for no limit; <see cref="BuildArguments"/> rejects values outside 0..16384.
    /// <para>0 表示不限制；<see cref="BuildArguments"/> 拒绝超出 0..16384 的值。</para></value>
    public int MaxSize { get; init; }

    /// <summary>The video encoding bit rate in bits per second.
    /// <para>视频编码码率（bps）。</para></summary>
    /// <value>Defaults to 8 Mbps; <see cref="BuildArguments"/> rejects values outside 64000..200000000.
    /// <para>默认 8 Mbps；<see cref="BuildArguments"/> 拒绝超出 64000..200000000 的值。</para></value>
    public int VideoBitRate { get; init; } = 8_000_000;

    /// <summary>The maximum capture frame rate.
    /// <para>最大采集帧率。</para></summary>
    /// <value>0 for no limit, default 60; <see cref="BuildArguments"/> rejects values outside 0..240.
    /// <para>0 表示不限制，默认 60；<see cref="BuildArguments"/> 拒绝超出 0..240 的值。</para></value>
    public int MaxFps { get; init; } = 60;

    /// <summary>The video codec name ("h264", "h265" or "av1").
    /// <para>视频编码名称（"h264"、"h265" 或 "av1"）。</para></summary>
    /// <value>Defaults to <c>h264</c>; any other value makes <see cref="BuildArguments"/> throw.
    /// <para>默认 <c>h264</c>；其他取值会使 <see cref="BuildArguments"/> 抛出异常。</para></value>
    public string VideoCodec { get; init; } = "h264";

    /// <summary>Audio codec (aac/opus/flac/raw).
    /// <para>音频编码格式（aac/opus/flac/raw）。</para></summary>
    /// <value>Defaults to <c>aac</c>; any other value makes <see cref="BuildArguments"/> throw.
    /// <para>默认 <c>aac</c>；其他取值会使 <see cref="BuildArguments"/> 抛出异常。</para></value>
    public string AudioCodec { get; init; } = "aac";

    /// <summary>Explicit Android video encoder name, or empty for automatic selection.
    /// <para>显式指定的设备端视频编码器名称，为空则自动选择。</para></summary>
    /// <value>At most 256 ASCII letters, digits, dots, underscores or hyphens; anything else makes
    /// <see cref="BuildArguments"/> throw. <para>最多 256 个 ASCII 字母、数字、点、下划线或连字符；
    /// 其他字符会使 <see cref="BuildArguments"/> 抛出异常。</para></value>
    public string VideoEncoder { get; init; } = string.Empty;

    /// <summary>Audio encoding bitrate in bits per second.
    /// <para>音频编码码率（bps）。</para></summary>
    /// <value>Defaults to 128000; <see cref="BuildArguments"/> rejects values outside 8000..1000000.
    /// <para>默认 128000；<see cref="BuildArguments"/> 拒绝超出 8000..1000000 的值。</para></value>
    public int AudioBitRate { get; init; } = 128000;

    /// <summary>Whether video capture is enabled.
    /// <para>是否启用视频采集。</para></summary>
    /// <value>
    /// When <see langword="false"/> the server does not encode a video stream; defaults to <see langword="true"/>.
    /// <para>为 <see langword="false"/> 时服务端不编码视频流；默认 <see langword="true"/>。</para>
    /// </value>
    public bool Video { get; init; } = true;

    /// <summary>Whether audio capture is enabled.
    /// <para>是否启用音频采集。</para></summary>
    /// <value>
    /// When <see langword="true"/> the client also opens and reads the audio socket; defaults to <see langword="false"/>.
    /// <para>为 <see langword="true"/> 时客户端还会打开并读取音频 socket；默认 <see langword="false"/>。</para>
    /// </value>
    public bool Audio { get; init; }

    /// <summary>Whether input control is enabled.
    /// <para>是否启用输入控制。</para></summary>
    /// <value>
    /// When <see langword="true"/> a control socket is opened for injected events; defaults to <see langword="true"/>.
    /// <para>为 <see langword="true"/> 时会为注入事件打开控制 socket；默认 <see langword="true"/>。</para>
    /// </value>
    public bool Control { get; init; } = true;

    /// <summary>The server log level ("verbose", "debug", "info", "warn", "error").
    /// <para>服务端日志级别（"verbose"、"debug"、"info"、"warn"、"error"）。</para></summary>
    /// <value>Defaults to <c>warn</c>; any other value makes <see cref="BuildArguments"/> throw.
    /// <para>默认 <c>warn</c>；其他取值会使 <see cref="BuildArguments"/> 抛出异常。</para></value>
    public string LogLevel { get; init; } = "warn";

    /// <summary>Whether to keep the device awake while mirroring.
    /// <para>投屏期间是否保持设备唤醒。</para></summary>
    /// <value>Passed to the server as <c>stay_awake</c>; defaults to <see langword="true"/>.
    /// <para>以 <c>stay_awake</c> 参数传给服务端；默认 <see langword="true"/>。</para></value>
    public bool StayAwake { get; init; } = true;

    /// <summary>Whether the server sends a dummy byte before metadata.
    /// <para>服务端是否在元数据前发送哑字节。</para></summary>
    /// <value>When <see langword="true"/> the client consumes and verifies a leading 0x00 byte on the video socket.
    /// <para>为 <see langword="true"/> 时客户端会在视频 socket 上消费并校验一个前导 0x00 字节。</para></value>
    public bool SendDummyByte { get; init; } = true;

    /// <summary>Whether the server sends the 64-byte device name.
    /// <para>服务端是否发送 64 字节设备名。</para></summary>
    /// <value>When <see langword="true"/> the client reads and trims the device name during the handshake.
    /// <para>为 <see langword="true"/> 时客户端会在握手期间读取并裁剪设备名。</para></value>
    public bool SendDeviceMeta { get; init; } = true;

    /// <summary>Whether the server sends codec metadata (codec id + size).
    /// <para>服务端是否发送编码器元数据（编码器 ID + 尺寸）。</para></summary>
    /// <value>When <see langword="true"/> the client reads the 12-byte video header and the 4-byte audio codec id.
    /// <para>为 <see langword="true"/> 时客户端会读取 12 字节视频头部与 4 字节音频编码标识。</para></value>
    public bool SendCodecMeta { get; init; } = true;

    /// <summary>
    /// Validates the options and builds the ordered server argument list (args[0] is the version).
    /// <para>校验选项并构建有序的服务端参数列表（args[0] 为版本号）。</para>
    /// </summary>
    /// <returns>The argument strings, in the order the scrcpy server parses them.
    /// <para>按 scrcpy 服务端解析顺序排列的参数字符串数组。</para></returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="Scid"/> exceeds <see cref="int.MaxValue"/>, or a capture limit
    /// (<see cref="VideoBitRate"/>, <see cref="AudioBitRate"/>,
    /// <see cref="MaxFps"/> or <see cref="MaxSize"/>) is out of range.
    /// <para><see cref="Scid"/> 超过 <see cref="int.MaxValue"/>，或采集上限（<see cref="VideoBitRate"/>、
    /// <see cref="AudioBitRate"/>、<see cref="MaxFps"/>、<see cref="MaxSize"/>）超出允许范围。</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The codec, encoder name or log level is not one of the supported values.
    /// <para>编解码器、编码器名称或日志级别不属于支持的取值。</para>
    /// </exception>
    public string[] BuildArguments()
    {
        if (Scid > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(Scid));
        if (VideoCodec is not ("h264" or "h265" or "av1")) throw new ArgumentException("Unsupported video codec.", nameof(VideoCodec));
        if (AudioCodec is not ("aac" or "opus" or "flac" or "raw")) throw new ArgumentException("Unsupported audio codec.", nameof(AudioCodec));
        if (VideoEncoder.Length > 256 || VideoEncoder.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))
            throw new ArgumentException("Invalid encoder name.", nameof(VideoEncoder));
        if (VideoBitRate is < 64000 or > 200000000 || AudioBitRate is < 8000 or > 1000000 || MaxFps is < 0 or > 240 || MaxSize is < 0 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(VideoBitRate), "Invalid capture limits.");
        if (LogLevel is not ("verbose" or "debug" or "info" or "warn" or "error")) throw new ArgumentException("Invalid server log level.", nameof(LogLevel));
        var args = new List<string>
        {
            ScrcpyConstants.ServerVersion,
            $"scid={Scid:x8}",
            $"log_level={LogLevel}",
            $"max_size={MaxSize.ToString(CultureInfo.InvariantCulture)}",
            $"video_bit_rate={VideoBitRate.ToString(CultureInfo.InvariantCulture)}",
            $"max_fps={MaxFps.ToString(CultureInfo.InvariantCulture)}",
            $"video_codec={VideoCodec}",
            $"audio_codec={AudioCodec}",
            $"audio_bit_rate={AudioBitRate}",
            $"video={Bool(Video)}",
            $"audio={Bool(Audio)}",
            $"control={Bool(Control)}",
            $"stay_awake={Bool(StayAwake)}",
            $"send_device_meta={Bool(SendDeviceMeta)}",
            "send_frame_meta=true",
            $"send_dummy_byte={Bool(SendDummyByte)}",
            $"send_codec_meta={Bool(SendCodecMeta)}",
            "cleanup=true",
            "tunnel_forward=true",
        };
        if (VideoEncoder.Length > 0) args.Add($"video_encoder={VideoEncoder}");
        return [.. args];
    }

    private static string Bool(bool value) => value ? "true" : "false";

    private static uint GenerateScid()
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes) & 0x7fffffff;
    }

    /// <summary>
    /// Builds the full <c>app_process</c> command line for the shell, appending every validated argument.
    /// <para>构建 shell 执行的完整 <c>app_process</c> 命令行，逐个追加通过校验的参数。</para>
    /// </summary>
    /// <returns>The shell command string. <para>shell 命令字符串。</para></returns>
    /// <exception cref="ArgumentException">
    /// An option fails the validation performed by <see cref="BuildArguments"/>.
    /// <para>某个选项未通过 <see cref="BuildArguments"/> 执行的校验。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A numeric limit fails the validation performed by <see cref="BuildArguments"/>.
    /// <para>某个数值上限未通过 <see cref="BuildArguments"/> 执行的校验。</para>
    /// </exception>
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