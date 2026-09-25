using UotanStudio.Comm.Transports.Abstractions;
using UotanStudio.Comm.Transports.Serial;

namespace UotanStudio.Comm.Protocols.Edl;

/// <summary>
/// Starts EDL through one explicitly selected serial port without enumeration, retries or loader fallback.
/// <para>通过一个显式选定的串口启动 EDL，不进行枚举、重试或 loader 回退。</para>
/// </summary>
public static class EdlSerialBootstrap
{
    /// <summary>
    /// Retains an optional programmer file before opening the port, then completes Sahara/Firehose initialization.
    /// <para>在打开串口前保留可选的 programmer 文件，随后完成 Sahara/Firehose 初始化。</para>
    /// </summary>
    /// <param name="serial">Explicit serial connection settings.
    /// <para>显式的串口连接设置。</para></param>
    /// <param name="programmerPath">Selected programmer file, or null when no upload is intended; no automatic selection occurs.
    /// <para>选定的 programmer 文件；不打算上传时为 null；不会自动选择。</para></param>
    /// <param name="configuration">Firehose configuration; the caller must not mutate it during startup.
    /// <para>Firehose 配置；启动期间调用方不得修改。</para></param>
    /// <param name="cancellationToken">The startup cancellation token.
    /// <para>启动过程的取消令牌。</para></param>
    /// <returns>A caller-owned initialized session. Loader execution may have device-side effects.
    /// <para>归调用方所有的已初始化会话。loader 执行可能产生设备侧影响。</para></returns>
    /// <exception cref="InvalidDataException">The selected programmer file is empty.
    /// <para>选定的 programmer 文件为空。</para></exception>
    /// <exception cref="InvalidOperationException">The connector returned no transport or the
    /// transport is not connected.
    /// <para>连接器未返回传输，或返回的传输未连接。</para></exception>
    public static Task<EdlSession> ConnectAsync(SerialConnectionOptions serial, string? programmerPath,
        FirehoseConfiguration? configuration = null, CancellationToken cancellationToken = default) =>
        ConnectCoreAsync(serial, programmerPath, configuration, static async (settings, token) =>
            await settings.ConnectAsync(token).ConfigureAwait(false), cancellationToken);

    internal static async Task<EdlSession> ConnectCoreAsync(SerialConnectionOptions serial, string? programmerPath,
        FirehoseConfiguration? configuration, Func<SerialConnectionOptions, CancellationToken, Task<ICommTransport>> connect,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serial);
        ArgumentNullException.ThrowIfNull(connect);
        serial.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var programmer = programmerPath is null ? null
            : new FileStream(programmerPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (programmer is { Length: 0 })
        {
            throw new InvalidDataException("The selected programmer file is empty.");
        }
        ICommTransport? transport = null;
        EdlSession? session = null;
        try
        {
            transport = await connect(serial, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The serial connector returned no transport.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!transport.IsConnected)
            {
                throw new InvalidOperationException("The selected transport is not connected.");
            }
            session = new EdlSession(transport, configuration);
            await session.InitializeAsync(programmer, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return session;
        }
        catch
        {
            if (session is not null)
            {
                session.Dispose();
            }
            else
            {
                transport?.Dispose();
            }
            throw;
        }
    }
}