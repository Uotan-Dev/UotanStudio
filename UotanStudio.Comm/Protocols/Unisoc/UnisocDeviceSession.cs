namespace UotanStudio.Comm.Protocols.Unisoc;

/// <summary>
/// Public UNISOC download-session facade over an opened download link (the vendor download serial port
/// <c>1782:4D00</c> or an equivalent transport): the identified loader stage, two-stage loader upload, transcode
/// control, chip UID and the completion modes. Storage operations go through <see cref="OpenStorage"/>.
/// <para>紫光展锐下载会话的公共门面，跑在已打开的下载链路（厂商下载串口 <c>1782:4D00</c> 或等价传输）之上：
/// 识别出的加载阶段、两级加载器上传、转义控制、芯片 UID 与收尾模式。存储操作经 <see cref="OpenStorage"/> 进入。</para>
/// </summary>
/// <remarks>
/// Stage detection follows the hardened probe line of the engine: a single-byte CHECK_BAUD probe is parsed under
/// CRC-16 and then under the additive checksum, with exactly one algorithm allowed to verify; a silent link allows
/// one CONNECT query plus one retried CHECK_BAUD handshake within the probe budget. Boot ROMs that do not report
/// <c>SPRD3</c> require correctly signed loaders; nothing here bypasses signature checks.
/// <para>阶段识别遵循引擎的强化探测线：单字节 CHECK_BAUD 先按 CRC-16 解析、再按加性校验解析，只允许恰好一个算法
/// 通过验证；静默链路在探测预算内允许一次 CONNECT 查询加一次带重试的 CHECK_BAUD 握手。不报告 <c>SPRD3</c> 的
/// Boot ROM 要求正确签名的加载器；此处不做任何签名绕过。</para>
/// </remarks>
public sealed class UnisocDeviceSession : IDisposable
{
    private readonly UnisocDeviceEngine _engine;

    private UnisocDeviceSession(UnisocDeviceEngine engine)
    {
        _engine = engine;
    }

    /// <summary>Gets the identity captured during the handshake.
    /// <para>获取握手期间捕获的身份信息。</para></summary>
    public UnisocDeviceIdentity Identity => _engine.Identity;

    /// <summary>Gets the identified loader stage. <para>获取识别出的加载阶段。</para></summary>
    public UnisocBootStage Stage => _engine.Stage;

    /// <summary>
    /// Opens the session: identifies the loader stage on the link. On success the session is connected to the
    /// identified stage; on failure the transport has been closed.
    /// <para>打开会话：识别链路上的加载阶段。成功时会话已连接到识别出的阶段；失败时传输已被关闭。</para>
    /// </summary>
    /// <param name="transport">The opened transport; the session owns and disposes it.
    /// <para>已打开的传输；会话持有并释放它。</para></param>
    /// <param name="options">Session parameters, or <see langword="null"/> for the defaults.
    /// <para>会话参数；<see langword="null"/> 表示使用默认值。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消握手。</para></param>
    /// <returns>The connected session. <para>已连接的会话。</para></returns>
    /// <exception cref="UnisocProtocolException">The link did not answer the probe lines with a recognizable stage.
    /// <para>链路对探测线没有给出可识别的阶段应答。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    internal static async Task<UnisocDeviceSession> OpenAsync(IUnisocTransport transport,
        UnisocSessionOptions? options, CancellationToken cancellationToken)
    {
        var engine = await UnisocDeviceEngine.OpenAsync(transport, options, cancellationToken).ConfigureAwait(false);
        return new UnisocDeviceSession(engine);
    }

    /// <summary>
    /// Uploads and executes the first-stage loader from a Boot ROM connection. On success the link talks to FDL1
    /// under the additive checksum.
    /// <para>从 Boot ROM 连接上传并执行一级加载器。成功后链路以加性校验与 FDL1 通信。</para>
    /// </summary>
    /// <param name="loader">Readable, seekable loader image; the session reads it chunk by chunk.
    /// <para>可读可定位的加载器镜像；会话逐块读取。</para></param>
    /// <param name="address">RAM load address confirmed by the package scheme or the user.
    /// <para>由包 scheme 或用户确认的 RAM 加载地址。</para></param>
    /// <param name="cancellationToken">Cancels the upload. <para>取消上传。</para></param>
    /// <exception cref="InvalidOperationException">The session is not connected to the Boot ROM.
    /// <para>会话未连接到 Boot ROM。</para></exception>
    /// <exception cref="UnisocProtocolException">Any exchange failed; the session has faulted.
    /// <para>任一交互失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task UploadFdl1Async(Stream loader, uint address, CancellationToken cancellationToken)
        => _engine.UploadFdl1Async(loader, address, cancellationToken);

    /// <summary>
    /// Uploads and executes the second-stage loader from an FDL1 connection. On success the link talks to FDL2.
    /// <para>从 FDL1 连接上传并执行二级加载器。成功后链路与 FDL2 通信。</para>
    /// </summary>
    /// <param name="loader">Readable, seekable loader image. <para>可读可定位的加载器镜像。</para></param>
    /// <param name="address">RAM load address. <para>RAM 加载地址。</para></param>
    /// <param name="cancellationToken">Cancels the upload. <para>取消上传。</para></param>
    /// <exception cref="InvalidOperationException">The session is not connected to FDL1.
    /// <para>会话未连接到 FDL1。</para></exception>
    /// <exception cref="UnisocProtocolException">Any exchange failed; the session has faulted.
    /// <para>任一交互失败；会话已失效。</para></exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task UploadFdl2Async(Stream loader, uint address, CancellationToken cancellationToken)
        => _engine.UploadFdl2Async(loader, address, cancellationToken);

    /// <summary>
    /// Disables the HDLC escape transcoding after the device acknowledges it; every later frame is sent unescaped.
    /// <para>在设备确认后关闭 HDLC 转义；此后每条帧都按未转义发送。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused or the link failed; the session has faulted.
    /// <para>设备拒绝或链路失败；会话已失效。</para></exception>
    /// <exception cref="InvalidOperationException">The transcode is already disabled.
    /// <para>转义已关闭。</para></exception>
    public Task DisableTranscodeAsync(CancellationToken cancellationToken)
        => _engine.DisableTranscodeAsync(cancellationToken);

    /// <summary>
    /// Reads the chip UID. <para>读取芯片 UID。</para>
    /// </summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <returns>The raw UID bytes, 1 to 256. <para>原始 UID 字节，1 到 256 字节。</para></returns>
    /// <exception cref="UnisocProtocolException">The device refused or the payload was out of bounds; the session
    /// has faulted. <para>设备拒绝或负载越界；会话已失效。</para></exception>
    public Task<byte[]> ReadChipUidAsync(CancellationToken cancellationToken)
        => _engine.ReadChipUidAsync(cancellationToken);

    /// <summary>Resets the device into normal mode. <para>把设备重启进正常模式。</para></summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public Task NormalResetAsync(CancellationToken cancellationToken)
        => _engine.NormalResetAsync(cancellationToken);

    /// <summary>Powers the device off. <para>让设备关机。</para></summary>
    /// <param name="cancellationToken">Cancels the exchange. <para>取消交互。</para></param>
    /// <exception cref="UnisocProtocolException">The device refused; the session has faulted.
    /// <para>设备拒绝；会话已失效。</para></exception>
    public Task PowerOffAsync(CancellationToken cancellationToken)
        => _engine.PowerOffAsync(cancellationToken);

    /// <summary>
    /// Opens the FDL2 storage face; every operation re-checks the stage before it touches the device.
    /// <para>打开 FDL2 存储面；每个操作在触碰设备前都会重新检查阶段。</para>
    /// </summary>
    /// <returns>The storage face bound to this session. <para>绑定本会话的存储面。</para></returns>
    /// <exception cref="InvalidOperationException">The session is not connected to FDL2.
    /// <para>会话未连接到 FDL2。</para></exception>
    public UnisocStorageSession OpenStorage() => new(_engine);

    /// <summary>Disposes the session and its transport. <para>释放会话与其传输。</para></summary>
    public void Dispose() => _engine.Dispose();
}