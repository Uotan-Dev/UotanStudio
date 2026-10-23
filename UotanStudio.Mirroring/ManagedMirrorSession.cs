using System.Text;
using UotanStudio.Comm.Contracts;
using UotanStudio.Comm.Devices.Services;

namespace UotanStudio.Mirroring;

/// <summary>Owns the ADB and scrcpy transports while presentation consumes decoded packets. Server log lines are forwarded to the optional <c>logSink</c> so this layer stays decoupled from any host logging service.
/// <para>持有 ADB 与 scrcpy 传输直至呈现层消费解码包。 server 日志行转发给可选的 <c>logSink</c>，使本层与宿主日志服务解耦。</para></summary>
public sealed class ManagedMirrorSession : IDisposable
{
    private readonly ScrcpyClient _client;
    private readonly Stream _shell;
    private readonly IAdbDeviceSession? _adbSession;
    private readonly DeviceLease? _lease;
    private readonly Action<string>? _logSink;
    private readonly StringBuilder _output = new();
    private readonly Lock _outputLock = new();
    private int _disposed;
    private bool _displayPowerOn = true;

    /// <summary>Creates a session over a connected scrcpy client and the resources behind it.
    /// <para>基于已连接的 scrcpy 客户端及其背后资源创建会话。</para>
    /// </summary>
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
    internal ManagedMirrorSession(ScrcpyClient client, Stream shell, IAdbDeviceSession? adbSession,
        DeviceLease? lease = null, Action<string>? logSink = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _adbSession = adbSession;
        _lease = lease;
        _logSink = logSink;
    }

    /// <summary>Device name announced by the server during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间服务端宣告的设备名。</para></summary>
    /// <value>
    /// The device name with trailing NUL characters trimmed, or <see cref="string.Empty"/> before a successful connect.
    /// <para>已去除尾部 NUL 字符的设备名；成功连接之前为 <see cref="string.Empty"/>。</para>
    /// </value>
    public string DeviceName => _client.DeviceName;

    /// <summary>Video stream metadata captured during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间捕获的视频流元数据。</para></summary>
    /// <value>The codec/width/height header, or <see langword="null"/> when the server did not send codec metadata.
    /// <para>编码/宽/高头部；服务端未发送编码元数据时为 <see langword="null"/>。</para></value>
    public ScrcpyVideoMetadata? VideoMetadata => _client.VideoMetadata;

    /// <summary>Audio codec metadata captured during <see cref="Connect"/>.
    /// <para>在 <see cref="Connect"/> 期间捕获的音频编码元数据。</para></summary>
    /// <value>
    /// The four-character codec header, or <see langword="null"/> when audio is off,
    /// metadata was not sent, or the server disabled audio.
    /// <para>四字符编码头部；未启用音频、未发送元数据或服务端禁用音频时为 <see langword="null"/>。</para>
    /// </value>
    public ScrcpyAudioMetadata? AudioMetadata => _client.AudioMetadata;

    /// <summary>Whether the server explicitly refused the audio stream during the handshake.
    /// <para>服务端是否在握手期间显式拒绝了音频流。</para></summary>
    /// <value><see langword="true"/> when the server's codec id marked audio disabled and closed the audio stream.
    /// <para>服务端的编码标识表明音频已禁用、且音频流已关闭时为 <see langword="true"/>。</para></value>
    public bool AudioDisabledByServer => _client.AudioDisabledByServer;

    /// <summary>Whether the scrcpy control socket is currently open.
    /// <para>scrcpy 控制 socket 当前是否已打开。</para></summary>
    /// <value><see langword="true"/> while a control stream is held and <see cref="SendControl"/> can be used.
    /// <para>持有控制流、可使用 <see cref="SendControl"/> 时为 <see langword="true"/>。</para></value>
    public bool IsControlConnected => _client.IsControlConnected;

    /// <summary>Opens the scrcpy sockets, retrying up to twenty times at 250 ms intervals while the session is alive. Server error output captured meanwhile is folded into the thrown <see cref="IOException"/> message.
    /// <para>打开 scrcpy socket；在会话存活期间最多重试二十次、每次间隔 250 毫秒。 期间捕获的服务端错误输出会并入抛出的 <see cref="IOException"/> 消息。</para></summary>
    /// <exception cref="IOException">Every attempt failed and the server emitted log detail; the message carries the summarized server errors.
    /// <para>所有尝试均失败且服务端产生了日志细节；消息携带概括后的服务端错误。</para></exception>
    /// <exception cref="InvalidDataException">The final attempt failed during the handshake and no server log detail was captured; such underlying errors are rethrown unchanged.
    /// <para>最后一次尝试在握手期间失败且未捕获到服务端日志细节；此类底层错误原样重新抛出。</para></exception>
    public void Connect()
    {
        _ = Task.Run(DrainServerOutput);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    _client.Connect(3000);
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
            lock (_outputLock) detail = _output.ToString().Trim();
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
    public ScrcpyVideoFrame? ReadFrame(int timeoutMs) => _client.ReadFrame(timeoutMs);

    /// <summary>Reads one framed audio packet from the underlying scrcpy client.
    /// <para>从底层 scrcpy 客户端读取一个分帧音频包。</para></summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds.<para>单次读取超时（毫秒）。</para></param>
    /// <returns>The audio packet, or <see langword="null"/> when no header arrived before the timeout.
    /// <para>音频包；超时前未收到头部时返回 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidOperationException">Audio is disabled, was refused by the server, or the session is not connected.
    /// <para>音频已禁用、被服务端拒绝或会话尚未连接。</para></exception>
    /// <exception cref="InvalidDataException">The announced packet length exceeds the client's bound.
    /// <para>宣告的包长度超出客户端上限。</para></exception>
    /// <exception cref="EndOfStreamException">The server closed the audio stream mid-packet.
    /// <para>服务端在包读取中途关闭了音频流。</para></exception>
    public ScrcpyAudioFrame? ReadAudioFrame(int timeoutMs) => _client.ReadAudioFrame(timeoutMs);

    /// <summary>Writes one encoded control message to the device over the control socket.
    /// <para>经控制 socket 向设备发送一条编码后的控制消息。</para></summary>
    /// <param name="message">A message produced by <see cref="ScrcpyControlMessageWriter"/>.
    /// <para>由 <see cref="ScrcpyControlMessageWriter"/> 生成的消息。</para></param>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    public void SendControl(byte[] message) => _client.SendControl(message);

    /// <summary>
    /// Polls one device → client message (clipboard payload or clipboard acknowledgement). The control socket is
    /// pumped by the caller and is independent of the video and audio readers.
    /// <para>轮询一条设备 → 客户端消息（剪贴板内容或剪贴板确认）。控制 socket 由调用方单独泵取，
    /// 与视频、音频读取互不干扰。</para>
    /// </summary>
    /// <param name="timeoutMs">Per-read timeout in milliseconds; zero returns immediately when nothing is pending.
    /// <para>单次读取超时（毫秒）；为 0 时无待读消息即立即返回。</para></param>
    /// <returns>The device message, or <see langword="null"/> when none arrived.
    /// <para>设备消息；未到达时为 <see langword="null"/>。</para></returns>
    /// <exception cref="InvalidDataException">The device sent an over-long clipboard or an unknown message type.
    /// <para>设备发出超长剪贴板或未知消息类型。</para></exception>
    public ScrcpyDeviceMessage? ReadDeviceMessage(int timeoutMs = 0) => _client.TryReadDeviceMessage(timeoutMs);

    /// <summary>
    /// Writes text into the device clipboard, optionally pasting it into the focused field. The UTF-8 payload is
    /// capped at 300 bytes.
    /// <para>把文本写入设备剪贴板，可选择同时在焦点输入框粘贴。UTF-8 编码上限 300 字节。</para>
    /// </summary>
    /// <param name="text">The text to place on the device clipboard. <para>要写入设备剪贴板的文本。</para></param>
    /// <param name="paste">Whether the device also pastes into the focused field. <para>是否同时粘贴到焦点输入框。</para></param>
    /// <param name="sequence">Request sequence echoed by the device's ACK_CLIPBOARD. <para>请求序号，由设备的 ACK_CLIPBOARD 回显。</para></param>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="text"/> encodes to more than 300 UTF-8 bytes.
    /// <para><paramref name="text"/> 的 UTF-8 编码超过 300 字节。</para></exception>
    public void SendClipboard(string text, bool paste, ulong sequence = 0)
        => _client.SendControl(ScrcpyControlMessageWriter.SetClipboard(text, paste, sequence));

    /// <summary>
    /// Asks the device to send its clipboard; the result arrives as a
    /// <see cref="ScrcpyConstants.DeviceTypeClipboard"/> message on <see cref="ReadDeviceMessage"/>.
    /// <para>请求设备回传剪贴板内容；结果以 <see cref="ScrcpyConstants.DeviceTypeClipboard"/> 消息出现在
    /// <see cref="ReadDeviceMessage"/> 上。</para>
    /// </summary>
    /// <param name="copyKey"><para><see cref="ScrcpyConstants.CopyKeyCopy"/> 或 <see cref="ScrcpyConstants.CopyKeyCut"/>。</para>
    /// <see cref="ScrcpyConstants.CopyKeyCopy"/> or <see cref="ScrcpyConstants.CopyKeyCut"/>.</param>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    public void RequestClipboard(byte copyKey = ScrcpyConstants.CopyKeyCopy)
        => _client.SendControl(ScrcpyControlMessageWriter.GetClipboard(copyKey));

    /// <summary>
    /// Sends one Android BACK press as a down/up pair; the same message wakes a powered-off screen.
    /// <para>发送一次 Android「返回」按压（down/up 两条消息）；屏幕关闭时该消息用于唤醒。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    public void SendBack()
    {
        _client.SendControl(ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionDown));
        _client.SendControl(ScrcpyControlMessageWriter.BackOrScreenOn(ScrcpyConstants.ActionUp));
        _displayPowerOn = true;
    }

    /// <summary>
    /// Toggles the device screen power. The scrcpy message only carries an absolute on/off mode, so the session
    /// remembers the last value sent and inverts it; a fresh session assumes the screen is on.
    /// <para>切换设备屏幕电源状态。scrcpy 的消息只接受绝对的开/关模式，因此本会话记录上一次下发的状态用于反向
    /// 切换；会话新建时假定屏幕是点亮的。</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">Control is disabled or the session is not connected.
    /// <para>控制未启用或会话尚未连接。</para></exception>
    public void ToggleDisplayPower()
    {
        _displayPowerOn = !_displayPowerOn;
        _client.SendControl(ScrcpyControlMessageWriter.SetDisplayPower(_displayPowerOn));
    }

    private void DrainServerOutput()
    {
        try
        {
            var buffer = new byte[4096];
            while (Volatile.Read(ref _disposed) == 0)
            {
                int count;
                try { count = _shell.Read(buffer, 0, buffer.Length); }
                catch (TimeoutException) { continue; }
                catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException
                { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut })
                { continue; }
                if (count == 0) break;
                var text = Encoding.UTF8.GetString(buffer, 0, count);
                lock (_outputLock)
                {
                    _output.Append(text);
                    if (_output.Length > 4096) _output.Remove(0, _output.Length - 4096);
                }
                _logSink?.Invoke(text);
            }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// Releases the captured session resources in order.
    /// <para>按顺序释放采集会话持有的各项资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _client.Dispose();
        }
        finally
        {
            try
            {
                _shell.Dispose();
            }
            finally
            {
                try
                {
                    _adbSession?.Dispose();
                }
                finally
                {
                    if (_lease is not null)
                        _lease.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }
    }
}