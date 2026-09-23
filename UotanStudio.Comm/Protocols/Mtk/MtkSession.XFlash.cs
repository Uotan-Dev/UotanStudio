using System.Buffers.Binary;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private const uint XFlashSync = 0x434E5953;
    private MtkXFlashChannel? _xflash;
    private MtkXFlashConnectionAgent? _xflashConnectionAgent;
    private bool _xflashExternalRamInitialized;

    /// <summary>Queries whether DA1 was entered through Boot ROM or an initialized preloader.</summary>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exact recognized connection agent; unknown replies are rejected.</returns>
    public async Task<MtkXFlashConnectionAgent> GetXFlashConnectionAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.XFlashStageOneReady);
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xflash ?? throw new InvalidOperationException("XFlash was not initialized.");
            try
            {
                var reply = await QueryXFlashControlAsync(channel, 0x04000A, cancellationToken).ConfigureAwait(false);
                if (reply.AsSpan().SequenceEqual("brom"u8))
                {
                    _xflashConnectionAgent = MtkXFlashConnectionAgent.BootRom;
                    return MtkXFlashConnectionAgent.BootRom;
                }
                if (reply.AsSpan().SequenceEqual("preloader"u8))
                {
                    _xflashConnectionAgent = MtkXFlashConnectionAgent.Preloader;
                    return MtkXFlashConnectionAgent.Preloader;
                }
                throw new MtkProtocolException("Unrecognized XFlash connection-agent reply.");
            }
            catch
            {
                _xflash = null;
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>
    /// Initializes an explicitly selected XFlash DA1 after its bootloader jump.
    /// This does not initialize external RAM, upload DA2 or make storage ready.
    /// </summary>
    /// <param name="logLevel">The DA UART logging level sent unchanged to the selected agent.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    public async Task InitializeXFlashAsync(uint logLevel = 2, CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentStarted);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var marker = await ReadAsync(1, _timeoutMs, cancellationToken).ConfigureAwait(false);
                if (marker[0] != 0xC0)
                {
                    throw new MtkProtocolException("The selected agent did not return the XFlash startup marker.");
                }

                var channel = new MtkXFlashChannel(Transport, _timeoutMs);
                await SendXFlashWordAsync(channel, XFlashSync, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0x010100, cancellationToken).ConfigureAwait(false);
                var environment = new byte[20];
                BinaryPrimitives.WriteUInt32LittleEndian(environment, logLevel);
                // UART-only logs avoid interleaving USB log messages with protocol replies.
                BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(4), 1);
                BinaryPrimitives.WriteUInt32LittleEndian(environment.AsSpan(8), OperatingSystem.IsWindows() ? 0u : 1u);
                // UFS provisioning and the reserved parameter remain zero.
                await channel.SendAsync(1, environment, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "environment setup", cancellationToken).ConfigureAwait(false);

                await SendXFlashWordAsync(channel, 0x010101, cancellationToken).ConfigureAwait(false);
                await SendXFlashWordAsync(channel, 0, cancellationToken).ConfigureAwait(false);
                await ReadXFlashStatusAsync(channel, "hardware setup", cancellationToken).ConfigureAwait(false);
                var sync = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
                if (sync.DataType != 1 || sync.Payload.Length != 4
                    || BinaryPrimitives.ReadUInt32LittleEndian(sync.Payload) != XFlashSync)
                {
                    throw new MtkProtocolException("XFlash DA1 did not confirm synchronization.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xflash = channel;
                _state = MtkSessionState.XFlashStageOneReady;
            }
            catch
            {
                _xflash = null;
                _state = MtkSessionState.Faulted;
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private async Task SendXFlashWordAsync(MtkXFlashChannel channel, uint value, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, value);
        await channel.SendAsync(1, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReadXFlashStatusAsync(MtkXFlashChannel channel, string operation, CancellationToken cancellationToken,
        bool allowSync = false)
    {
        var status = await ReadXFlashValueAsync(channel, operation, cancellationToken).ConfigureAwait(false);
        if (status != 0 && status != 0xFEEEEEEF && !(allowSync && status == XFlashSync))
        {
            throw new MtkProtocolException($"XFlash rejected {operation} (0x{status:X8}).", status);
        }
    }

    private async Task<uint> ReadXFlashValueAsync(MtkXFlashChannel channel, string operation, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var frame = await channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (frame.DataType != 1 || frame.Payload.Length is not (2 or 4))
        {
            throw new MtkProtocolException($"Invalid XFlash status frame during {operation}.");
        }
        return frame.Payload.Length == 2
            ? BinaryPrimitives.ReadUInt16LittleEndian(frame.Payload)
            : BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload);
    }
}