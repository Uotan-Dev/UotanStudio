using System.Xml.Linq;

namespace UotanStudio.Comm.Protocols.Mtk;

/// <summary>Explicit XML security-resource commands; these are not boot-ROM E0/E2 resources.
/// <para>显式的 XML 安全资源命令；它们不是 Boot ROM 的 E0/E2 资源。</para></summary>
public enum MtkXmlSecurityResourceKind
{
    /// <summary>A caller-supplied flash-policy response, including any required device-specific signature.
    /// <para>由调用方提供的 flash-policy 响应，包含任何所需的设备专用签名。</para></summary>
    FlashPolicy,
    /// <summary>A caller-supplied all-in-one signature resource.
    /// <para>由调用方提供的一体化（all-in-one）签名资源。</para></summary>
    AllInOneSignature
}

public sealed partial class MtkSession
{
    private async Task UploadXmlSecurityResourceOwnedAsync(MtkXmlChannel channel, XElement command,
        string sourceId, Stream resource, int length, IProgress<long>? progress, CancellationToken token)
    {
        await SendXmlControlCommandOwnedAsync(channel, XmlHostCapabilities(download: true), token).ConfigureAwait(false);
        await channel.SendCommandAsync(command, token).ConfigureAwait(false);
        await RequireXmlOkAsync(channel, token).ConfigureAwait(false);
        var request = await ReceiveXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
        var packetLength = ValidateXmlDownloadRequest(request, sourceId);
        await channel.AcknowledgeAsync(token).ConfigureAwait(false);
        await SendXmlSourceOwnedAsync(channel, resource, length, packetLength, progress, token).ConfigureAwait(false);
        await CompleteXmlCommandOwnedAsync(channel, token).ConfigureAwait(false);
    }
}