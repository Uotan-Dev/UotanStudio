namespace UotanStudio.Comm.Protocols.Mtk;

public static partial class MtkBootstrap
{
    private static async Task InitializeXmlAsync(MtkSession session, Stream stageTwo, uint address,
        MtkBootstrapOptions options, CancellationToken cancellationToken)
    {
        var xml = options.Xml!;
        session.HostFileStore = xml.HostFileStore;
        session.MaximumHostFileTransferBytes = xml.MaximumHostFileTransferBytes;
        session.XmlFieldPolicy = xml.XmlFieldPolicy;
        await session.AttachXmlAgentAsync(cancellationToken).ConfigureAwait(false);
        await session.ConfigureXmlRuntimeAsync(xml.Runtime, xml.OperationTimeoutMs, cancellationToken).ConfigureAwait(false);
        await session.InitializeXmlHardwareAsync(xml.OperationTimeoutMs, cancellationToken).ConfigureAwait(false);
        await session.SetXmlHostInfoAsync(xml.HostInfo, cancellationToken).ConfigureAwait(false);
        await session.UploadXmlStageTwoAsync(stageTwo, address, checked((int)stageTwo.Length),
            xml.OperationTimeoutMs, cancellationToken: cancellationToken).ConfigureAwait(false);
        await session.InitializeXmlStageTwoHardwareAsync(xml.OperationTimeoutMs, cancellationToken).ConfigureAwait(false);
        await session.RequireXmlStageTwoSlaInactiveAsync(xml.SlaEnvelopeName, options.SlaTimeoutMs,
            cancellationToken).ConfigureAwait(false);
        var storage = await session.DiscoverXmlStorageAsync(xml.OperationTimeoutMs, cancellationToken).ConfigureAwait(false);
        var expected = options.StorageKind switch
        {
            MtkXFlashStorageKind.Emmc => MtkXmlStorageKind.Emmc,
            MtkXFlashStorageKind.Ufs => MtkXmlStorageKind.Ufs,
            _ => throw new ArgumentOutOfRangeException(nameof(options))
        };
        if (storage.Kind != expected)
        {
            throw new InvalidOperationException("The XML agent reported a different storage family than explicitly selected.");
        }
    }
}