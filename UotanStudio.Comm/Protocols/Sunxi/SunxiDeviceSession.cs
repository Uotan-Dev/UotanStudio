namespace UotanStudio.Comm.Protocols.Sunxi;

/// <summary>
/// One Allwinner endpoint after its identity handshake, exposing the FEL or FES session that matches the mode
/// the device reported. Both modes enumerate with the same vendor and product id, so the mode is only known
/// after <see cref="SunxiEfexSession.VerifyDeviceAsync"/> answered.
/// <para>完成身份握手后的一个全志端点，按其上报的模式暴露对应的 FEL 或 FES 会话。两种模式以相同的厂商与产品
/// id 枚举，因此只有在 <see cref="SunxiEfexSession.VerifyDeviceAsync"/> 应答之后才能知道模式。</para>
/// </summary>
public sealed class SunxiDeviceSession
{
    private readonly ISunxiEfexTransport _transport;
    private SunxiFelSession? _fel;
    private SunxiFesSession? _fes;

    internal SunxiDeviceSession(ISunxiEfexTransport transport, SunxiDeviceIdentity identity)
    {
        _transport = transport;
        Identity = identity;
    }

    /// <summary>Identity the device reported during the handshake.
    /// <para>设备在握手期间上报的身份信息。</para></summary>
    public SunxiDeviceIdentity Identity { get; }

    /// <summary>Mode the device currently runs. <para>设备当前运行的模式。</para></summary>
    public SunxiDeviceMode Mode => Identity.Mode;

    /// <summary>Whether the device runs the USB boot ROM. <para>设备是否运行 USB boot ROM。</para></summary>
    public bool IsFel => Mode == SunxiDeviceMode.Fel;

    /// <summary>Whether the device runs the FES service. <para>设备是否运行 FES 服务。</para></summary>
    public bool IsFes => Mode == SunxiDeviceMode.Fes;

    /// <summary>The FEL session, created on first use. <para>FEL 会话，首次使用时创建。</para></summary>
    /// <exception cref="InvalidOperationException">The device is not in FEL mode.
    /// <para>设备不在 FEL 模式。</para></exception>
    public SunxiFelSession Fel => IsFel
        ? _fel ??= new SunxiFelSession(_transport)
        : throw new InvalidOperationException($"The device reports mode {Mode} instead of FEL.");

    /// <summary>The FES session, created on first use. <para>FES 会话，首次使用时创建。</para></summary>
    /// <exception cref="InvalidOperationException">The device is not in FES mode.
    /// <para>设备不在 FES 模式。</para></exception>
    public SunxiFesSession Fes => IsFes
        ? _fes ??= new SunxiFesSession(_transport)
        : throw new InvalidOperationException($"The device reports mode {Mode} instead of FES.");

    /// <summary>
    /// Opens a session over one bulk transport and completes the identity handshake.
    /// <para>在一条批量传输之上打开会话并完成身份握手。</para>
    /// </summary>
    /// <param name="transport">Transport the session runs on. <para>会话所使用的传输。</para></param>
    /// <param name="cancellationToken">Cancels the handshake. <para>取消本次握手。</para></param>
    /// <returns>The session carrying the reported identity. <para>携带上报身份的会话。</para></returns>
    /// <exception cref="SunxiEfexException">The device answered with a non-zero status.
    /// <para>设备以非零状态应答。</para></exception>
    /// <exception cref="InvalidDataException">The device returned an unexpected identity frame.
    /// <para>设备返回了异常的身份帧。</para></exception>
    internal static async Task<SunxiDeviceSession> OpenAsync(ISunxiEfexTransport transport,
        CancellationToken cancellationToken)
    {
        var identity = await new SunxiFesSession(transport).VerifyDeviceAsync(cancellationToken)
            .ConfigureAwait(false);
        return new SunxiDeviceSession(transport, identity);
    }
}