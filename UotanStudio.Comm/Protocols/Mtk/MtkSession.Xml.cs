using System.Xml;
using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

public sealed partial class MtkSession
{
    private MtkXmlChannel? _xml;

    /// <summary>
    /// Accepts and acknowledges an XML DA's initial START after an explicitly uploaded and
    /// started agent. This only establishes the control channel, not hardware, DRAM, DA2
    /// or storage readiness. No XFlash/XML fallback or loader autodetection is performed.
    /// </summary>
    /// <param name="cancellationToken">Cancellation for waiting and the bounded initial exchange.</param>
    public async Task AttachXmlAgentAsync(CancellationToken cancellationToken = default)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RequireState(MtkSessionState.AgentStarted);
            cancellationToken.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeoutMs);
            try
            {
                var channel = new MtkXmlChannel(Transport, _timeoutMs);
                var initial = MtkXmlMessage.Parse(await channel.ReceiveTextAsync(deadline.Token).ConfigureAwait(false));
                RequireXmlCommand(initial, "CMD:START");
                await channel.AcknowledgeAsync(deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                _xml = channel;
                _state = MtkSessionState.XmlControlReady;
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML DA initial exchange deadline expired.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Sends explicit host metadata and requires OK, END/OK and the next START before completing.</summary>
    /// <remarks>No storage commands or target-requested file operations are performed. Unknown
    /// subcommands fault the session. Progress reports are consumed with bounded acknowledgements.</remarks>
    /// <param name="hostInfo">Explicit metadata of at most 256 characters, without automatic timestamps.</param>
    /// <param name="cancellationToken">Cancellation for waiting and the whole control exchange.</param>
    public Task SetXmlHostInfoAsync(string hostInfo, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostInfo);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hostInfo.Length, 256);
        XmlConvert.VerifyXmlChars(hostInfo);
        var command = new XElement("da", new XElement("version", "1.0"),
            new XElement("command", "CMD:SET-HOST-INFO"), new XElement("arg", new XElement("info", hostInfo)));
        return ExecuteXmlControlCommandsAsync([command], null, null, cancellationToken);
    }

    private async Task ExecuteXmlControlCommandsAsync(XElement[] commands, MtkSessionState? requiredState,
        MtkSessionState? nextState, CancellationToken cancellationToken, int? timeoutMs = null)
    {
        await _operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requiredState is { } expected)
            {
                RequireState(expected);
            }
            else
            {
                ThrowIfDisposed();
                if (!Transport.IsConnected || _state is not (MtkSessionState.XmlControlReady
                    or MtkSessionState.XmlRuntimeConfigured or MtkSessionState.XmlHardwareInitialized
                    or MtkSessionState.XmlStageTwoStarted or MtkSessionState.XmlStageTwoHardwareInitialized
                    or MtkSessionState.XmlStorageGeometryKnown))
                {
                    throw new InvalidOperationException("An attached XML DA control stage is required.");
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            var channel = _xml ?? throw new InvalidOperationException("The XML DA channel was not attached.");
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeoutMs ?? _timeoutMs);
            var token = deadline.Token;
            try
            {
                foreach (var command in commands)
                {
                    await SendXmlControlCommandOwnedAsync(channel, command, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (nextState is { } completed)
                {
                    _state = completed;
                }
            }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                FaultXml();
                throw new TimeoutException("The XML DA control exchange deadline expired.", error);
            }
            catch
            {
                FaultXml();
                throw;
            }
        }
        finally
        {
            _operation.Release();
        }
    }

    private static async Task SendXmlControlCommandOwnedAsync(MtkXmlChannel channel, XElement command, CancellationToken token)
    {
        await channel.SendCommandAsync(command, token).ConfigureAwait(false);
        if (await channel.ReceiveTextAsync(token).ConfigureAwait(false) != "OK")
        {
            throw new MtkProtocolException("The XML DA did not accept the control command.");
        }
        await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
    }

    private static async Task CompleteXmlCommandOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        var end = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        await AcknowledgeXmlEndOwnedAsync(channel, end, token).ConfigureAwait(false);
        await AcknowledgeXmlStartOwnedAsync(channel, token).ConfigureAwait(false);
    }

    private static async Task AcknowledgeXmlEndOwnedAsync(MtkXmlChannel channel, MtkXmlMessage end, CancellationToken token)
    {
        RequireXmlCommand(end, "CMD:END");
        if (end.Result != "OK")
        {
            throw new MtkProtocolException("The XML DA control command failed.");
        }
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
    }

    private static async Task AcknowledgeXmlStartOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        var start = MtkXmlMessage.Parse(await channel.ReceiveTextAsync(token).ConfigureAwait(false));
        RequireXmlCommand(start, "CMD:START");
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
    }

    private static async Task<MtkXmlMessage> ReceiveXmlCommandOwnedAsync(MtkXmlChannel channel, CancellationToken token)
    {
        for (var reports = 0; reports <= 16; reports++)
        {
            var message = MtkXmlMessage.Parse(await channel.ReceiveTextAsync(token).ConfigureAwait(false));
            if (message.Command != "CMD:PROGRESS-REPORT")
            {
                return message;
            }
            if (reports == 16)
            {
                throw new MtkProtocolException("Too many XML DA progress reports.");
            }
            await channel.AcknowledgeAsync(token).ConfigureAwait(false);
            var completed = false;
            for (var packet = 0; packet < 256; packet++)
            {
                var text = await channel.ReceiveTextAsync(token).ConfigureAwait(false);
                if (text.StartsWith("ERR!", StringComparison.Ordinal))
                {
                    throw new MtkProtocolException("The XML DA progress phase failed.");
                }
                await channel.AcknowledgeAsync(token).ConfigureAwait(false);
                if (text == "OK!EOT")
                {
                    completed = true;
                    break;
                }
            }
            if (!completed)
            {
                throw new MtkProtocolException("The XML DA progress phase exceeded its packet limit.");
            }
        }
        throw new MtkProtocolException("An XML DA terminal command was not received.");
    }

    private static void RequireXmlCommand(MtkXmlMessage message, string command)
    {
        if (message.Command != command || message.Result is not (null or "OK"))
        {
            throw new MtkProtocolException($"Expected XML DA {command}, received {message.Command}.");
        }
    }

    private void FaultXml()
    {
        XmlStorage = null;
        _xml = null;
        _state = MtkSessionState.Faulted;
    }
}