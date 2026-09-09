using System.Text;

namespace UotanStudio.Comm.Protocols.Hdc;

/// <summary>Native daemon app transfer and bundle-manager completion messages.</summary>
public sealed class HdcAppClient(HdcConnection connection)
{
    /// <summary>Transfers and installs one HAP/HSP/APP package using AppCheck/Begin/Data/Finish.</summary>
    public string Install(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".hap" or ".hsp" or ".app")) throw new ArgumentException("Unsupported HDC package extension.", nameof(path));
        using var input = File.OpenRead(path);
        var config = new TransferConfig { FileSize = input.Length, OptionalName = Guid.NewGuid().ToString("N") + extension, HoldTimestamp = false, FunctionName = "install" };
        using var channel = connection.OpenChannel(HdcCommand.AppCheck, HdcTransferCodec.EncodeConfig(config));
        HdcSyncClient.Expect(channel, HdcCommand.AppBegin, cancellationToken);
        using var transfer = new HdcSyncClient(connection);
        transfer.SendData(channel, input, input.Length, HdcCommand.AppData, cancellationToken);
        return ReadResult(channel, 1, cancellationToken);
    }

    /// <summary>Uninstalls the exact named bundle.</summary>
    public string Uninstall(string bundle, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(bundle);
        if (bundle.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_')))
            throw new ArgumentException("Invalid bundle name.", nameof(bundle));
        using var channel = connection.OpenChannel(HdcCommand.AppUninstall, Encoding.UTF8.GetBytes(bundle));
        return ReadResult(channel, 2, cancellationToken);
    }

    private static string ReadResult(HdcStream channel, byte expectedMode, CancellationToken ct)
    {
        var finish = HdcSyncClient.Expect(channel, HdcCommand.AppFinish, ct, timeoutMs: 300000);
        var payload = finish.Payload ?? [];
        if (payload.Length < 2) throw new InvalidDataException("Truncated HDC app result.");
        if (payload[0] != expectedMode) throw new InvalidDataException("Unexpected HDC app result mode.");
        var text = Encoding.UTF8.GetString(payload.AsSpan(2));
        if (payload[1] != 1) throw new IOException("HDC application operation failed: " + text);
        return text;
    }
}
