namespace UotanStudio.DD.Drives;

/// <summary>
/// Combines the raw-disk and optical-disc scanners of one platform into a single enumeration.
/// <para>把单一平台的裸磁盘与光盘扫描器合并为一次枚举。</para>
/// </summary>
internal sealed class CompositeDriveScanner(IDriveScanner diskScanner, IDriveScanner opticalScanner) : IDriveScanner
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<FlashDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var disks = await diskScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var optical = await opticalScanner.ScanAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<FlashDrive>(disks.Count + optical.Count);
        result.AddRange(disks);
        result.AddRange(optical);
        return result;
    }
}