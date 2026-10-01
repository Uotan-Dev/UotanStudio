using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Services.Mirroring;

/// <summary>Owns the ADB and scrcpy transports while presentation consumes decoded packets.
/// Server log lines are forwarded to the optional <c>logSink</c> so this layer stays decoupled
/// from any host logging service. <para>持有 ADB 与 scrcpy 传输直至呈现层消费解码包。
/// server 日志行转发给可选的 <c>logSink</c>，使本层与宿主日志服务解耦。</para></summary>
/// <param name="client">The connected scrcpy client this session forwards frames and control to.
/// <para>本会话转发帧与控制消息的目标、已连接的 scrcpy 客户端。</para></param>
/// <param name="shell">Shell stream used to pump server log output; disposed with the session.
/// <para>用于抽取服务端日志输出的 shell 流；随会话一同释放。</para></param>
/// <param name="adbSession">Optional ADB device session owning the streams; disposed with the session.
/// <para>可选的、持有这些流的 ADB 设备会话；随会话一同释放。</para></param>
/// <param name="lease">Optional device lease released when the session is disposed.
/// <para>可选的设备租约，在会话释放时一并释放。</para></param>
/// <param name="logSink">Optional sink receiving decoded server log text; may be called from a background thread.
/// <para>可选的接收端，用于接收解码后的服务端日志文本；可能由后台线程调用。</para></param>
public sealed class ManagedMirrorSession(ScrcpyClient client, Stream shell, IAdbDeviceSession? adbSession,
    DeviceLease? lease = null, Action<string>? logSink = null) : IDisposable
{
    private readonly StringBuilder _output = new();
    private int _disposed;

    /// <summary>Device name announced by the server during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间服务端宣告的设备名。</para></summary>
    /// <value>
    /// The device name with trailing NUL characters trimmed, or <see cref="string.Empty"/> before a successful connect.
    /// <para>已去除尾部 NUL 字符的设备名；成功连接之前为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string DeviceName => client.DeviceName;

    /// <summary>Video stream metadata captured during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间捕获的视频流元数据。</para></summary>
    /// <value>The codec/width/height header, or <see langword="null"/> when the server did not send codec metadata.
    /// <para>编码/宽/高头部；服务端未发送编码元数据时为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => client.VideoMetadata;

    /// <summary>Audio codec metadata captured during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间捕获的音频编码元数据。</para></summary>
    /// <value>
    /// The four-character codec header, or <see langword="null"/> when audio is off,
    /// metadata was not sent, or the server disabled audio.
    /// <para>四字符编码头部；未启用音频、未发送元数据或服务端禁用音频时为 <see langword="null"/>。</para>
    /// </value>
    public ScrcpyAudioMetadata? AudioMetadata => client.AudioMetadata;

    /// <summary>Whether the server explicitly refused the audio stream during the handshake.
    /// <para>服务端是否在握手期间显式拒绝了音频流。</para></summary>
    /// <value><see langword="true"/> when the server's codec id marked audio disabled and closed the audio stream.
    /// <para>服务端的编码标识表明音频已禁用、且音频流已关闭时为 <see langword="true"/>。</para></value>
    public bool AudioDisabledByServer => client.AudioDisabledByServer;

    /// <summary>Whether the scrcpy control socket is currently open.
    /// <para>scrcpy 控制 socket 当前是否已打开。</para></summary>
    /// <value><see langword="true"/> while a control stream is held and <see cref="SendControl"/> can be used.
    /// <para>持有控制流、可使用 <see cref="SendControl"/> 时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => client.IsControlConnected;

    /// <summary>
    /// Opens the scrcpy sockets, retrying up to twenty times at 250 ms intervals while the session is alive.
    /// Server error output captured meanwhile is folded into the thrown <see cref="IOException"/> message.
    /// <para>打开 scrcpy socket；在会话存活期间最多重试二十次、每次间隔 250 毫秒。
    /// 期间捕获的服务端错误输出会并入抛出的 <see cref="IOException"/> 消息。</para>
    /// </summary>
    /// <exception cref="IOException">
    /// Every attempt failed and the server emitted log detail; the message carries the summarized server errors.
    /// <para>所有尝试均失败且服务端产生了日志细节；消息携带概括后的服务端错误。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The final attempt failed during the handshake and no server log detail was captured; such underlying
    /// errors are rethrown unchanged.
    /// <para>最后一次尝试在握手期间失败且未捕获到服务端日志细节；此类底层错误原样重新抛出。</para>
    /// </exception>
    public void Connect()
    {
        _ = Task.Run(DrainServerOutput);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    client.Connect(3000);
                    return;
                }
                catch (Exception) when (attempt < 19 && Volatile.Read(ref _disposed) == 0)
                {
                    Thread.Sleep(250);
                }
            }
        }
        catch (Exception error)
        {
            string detail;
            lock (_output) detail = _output.ToString().Trim();
            if (detail.Length == 0) throw;
            var summary = string.Join("\n", detail.Split('\n')
                .Where(line => line.Contains("ERROR:", StringComparison.Ordinal) &&
                    !line.Contains("Exception on thread", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("--video-", StringComparison.Ordinal))
                .Take(6));
            throw new IOException(summary.Length > 0 ? summary : error.Message, error);
        }
    }

    /// <summary>Reads one framed video packet from the underlying scrcpy client.
    /// <para>从底层 scrcpy 客户端读取一个分帧视频包。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds.
    /// <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The frame, or <see langword="null"/> when no header arrived before the timeout.
    /// <para>视频帧；超时前未收到头部时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not connected.
    /// <para>会话尚未连接。</para></exception>
    /// <exception cref="InvalidDataException">The announced frame length exceeds the client's bound.
    /// <para>宣告的帧长度超出客户端上限。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the video stream mid-packet.
    /// <para>服务端在包读取中途关闭了视频流。</para></exception>
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => client.ReadFrame(timeoutMs);

    /// <summary>Reads one framed audio packet from the underlying scrcpy client.
    /// <para>从底层 scrcpy 客户端读取一个分帧音频包。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds.
    /// <para>单次读取超时（毫秒）。</para></param>
    /// <returns>The audio packet, or <see langword="null"/> when no header arrived before the timeout.
    /// <para>音频包；超时前未收到头部时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">
    /// Audio is disabled, was refused by the server, or the session is not connected.
    /// <para>音频已禁用、被服务端拒绝或会话尚未连接。</para></exception>
    /// <exception cref="InvalidDataException">The announced packet length exceeds the client's bound.
    /// <para>宣告的包长度超出客户端上限。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the audio stream mid-packet.
    /// <para>服务端在包读取中途关闭了音频流。</para></exception>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs) => client.ReadAudioFrame(timeoutMs);

    /// <summary>Writes one encoded control message to the device over the control socket.
    /// <para>经控制 socket 向设备发送一条编码后的控制消息。</para></summary>
    /// <param name="message">A message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 生成的消息。</para></param>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    public void SendControl(byte[] message) => client.SendControl(message);

    private void DrainServerOutput()
    {
        try
        {
            byte[] buffer = new byte[4096];
            while (Volatile.Read(ref _disposed) == 0)
            {
                int count;
                try { count = shell.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; }
                catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException
                { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut })
                { continue; }
                if (count == 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                lock (_output)
                {
                    _output.Append(text);
                    if (_output.Length > 4096) _output.Remove(0, _output.Length - 4096);
                }
                logSink?.Invoke(text);
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Releases the scrcpy client, the shell stream, the optional ADB session and the optional device lease,
    /// in that order; each step runs even if an earlier one throws. Concurrent calls are ignored, so the
    /// background log pump stops once the first disposal has begun.
    /// <para>依次释放 scrcpy 客户端、shell 流、可选的 ADB 会话与可选的设备租约；前一步抛出异常时后续步骤
    /// 仍会执行。并发调用会被忽略，因此首次释放开始后后台日志泵即停止。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            client.Dispose();
        }
        finally
        {
            try
            {
                shell.Dispose();
            }
            finally
            {
                try
                {
                    adbSession?.Dispose();
                }
                finally
                {
                    if (lease is not null)
                        lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }
}