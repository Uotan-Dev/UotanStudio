using UotanStudio.Firmware.Application.Containers;
using UotanStudio.Services.Firmware;

namespace UotanStudio.Services.Flash;

/// <summary>Reads and stages selected partitions from an HTTP range-backed raw Payload.<para>从支持 HTTP 范围请求的原始 Payload 中读取并暂存所选分区。</para></summary>
/// <param name="client">HTTP client performing the ranged requests.<para>执行范围请求的 HTTP 客户端。</para></param>
public sealed class FlashRemotePayloadService(HttpClient client)
{
    private readonly RemoteRomService _remote = new(client);

    /// <summary>Rejects non-Payload URLs before offering any partitions for extraction.<para>在给出可解压分区之前先拒绝非 Payload 的 URL。</para></summary>
    /// <param name="address">URL of the remote raw Payload.<para>远程原始 Payload 的 URL。</para></param>
    /// <param name="ct">Token cancelling the read.<para>用于取消读取的令牌。</para></param>
    /// <returns>The partition names and declared sizes.<para>分区名与声明的大小。</para></returns>
    public async Task<IReadOnlyList<(string Name, ulong Size)>> ListAsync(string address, CancellationToken ct)
    {
        var analysis = await _remote.AnalyzeAsync(address, ct).ConfigureAwait(false);
        if (analysis.Kind != ContainerKind.Payload)
            throw new InvalidDataException("The URL does not contain a raw Payload.");
        if (analysis.Entries.Any(entry => !FlashManifestReader.IsPartitionName(entry.Name) || entry.SizeBytes < 0) ||
            analysis.Entries.Select(entry => entry.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != analysis.Entries.Count)
            throw new InvalidDataException("The remote Payload contains invalid or duplicate partition names.");
        return [.. analysis.Entries.Select(entry => (entry.Name, checked((ulong)entry.SizeBytes)))];
    }

    /// <summary>Publishes a new directory only when all requested images have been extracted.<para>只有全部请求的镜像都解压完成才发布新目录。</para></summary>
    /// <param name="address">URL of the remote raw Payload.<para>远程原始 Payload 的 URL。</para></param>
    /// <param name="partitions">Distinct partition names available remotely.<para>远程可用的去重分区名。</para></param>
    /// <param name="parent">Existing directory that receives the new output directory.<para>接收新输出目录的既有目录。</para></param>
    /// <param name="ct">Token cancelling the extraction.<para>用于取消解压的令牌。</para></param>
    /// <returns>Path of the directory holding the extracted images.<para>存放解压镜像的目录路径。</para></returns>
    public async Task<string> ExtractAsync(string address, IReadOnlyList<string> partitions, string parent, CancellationToken ct)
    {
        var available = await ListAsync(address, ct).ConfigureAwait(false);
        if (partitions.Count == 0 || partitions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != partitions.Count ||
            partitions.Any(name => !available.Any(entry => entry.Name.Equals(name, StringComparison.Ordinal))))
            throw new ArgumentException("Select distinct partitions from the remote Payload.", nameof(partitions));
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);

        var output = Path.Combine(parent, "payload-unpack-" + Guid.NewGuid().ToString("N")[..8]);
        var staging = output + ".tmp";
        try
        {
            Directory.CreateDirectory(staging);
            await _remote.ExtractPayloadPartitionsAsync(new Uri(address), partitions, staging,
                cancellationToken: ct).ConfigureAwait(false);
            foreach (var name in partitions)
            {
                if (!File.Exists(Path.Combine(staging, name + ".img")))
                    throw new InvalidDataException($"Partition extraction did not produce {name}.img.");
            }
            ct.ThrowIfCancellationRequested();
            Directory.Move(staging, output);
            return output;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}