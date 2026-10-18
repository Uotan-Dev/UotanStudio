using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

public sealed partial class PayloadBuilder
{
    /// <summary>Adds a partition emitted as REPLACE_ZSTD operations (extension operation 14), optionally compressed against an external raw content dictionary.
    /// <para>添加以 REPLACE_ZSTD 操作（扩展操作 14）发射的分区，可选地带外部原始内容字典压缩。</para></summary>
    /// <remarks>Dictionary-free frames rebuild through this repository's extractor. Frames that use an external dictionary additionally require the consumer to hold the exact dictionary bytes and honor the gating keys <c>max_zstd_dict_raw_size</c> and <c>max_zstd_dict_trust</c> from <c>payload_properties.txt</c>; <see cref="PayloadExtractor"/> takes the per-partition dictionary and the raw-size ceiling, so dictionary frames rebuild through the same extractor path.
    /// <para>无字典帧可经本仓库提取器重建。使用外部字典的帧还要求消费方持有完全一致的字典字节，并遵守 <c>payload_properties.txt</c> 中的管控键 <c>max_zstd_dict_raw_size</c> 与 <c>max_zstd_dict_trust</c>； <see cref="PayloadExtractor"/> 接收按分区字典与原始大小上限，因此字典帧同样经该提取器路径重建。</para></remarks>
    /// <remarks>Adding a REPLACE_ZSTD partition raises the manifest minor version to 10, which permits REPLACE_ZSTD operations.
    /// <para>添加 REPLACE_ZSTD 分区会把清单 minor 版本提升到 10，该版本起才允许 REPLACE_ZSTD 操作。</para></remarks>
    /// <param name="partitionName">Partition written by this update; must not be empty or whitespace and must be unique within the builder.
    /// <para>本次更新写入的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para></param>
    /// <param name="imagePath">Image file to embed; existence is checked here and the file is opened by the build.
    /// <para>待嵌入的镜像文件；此处检查存在性，实际打开发生在构建过程。</para></param>
    /// <param name="zstdDictionary">External raw content dictionary (for example a prior partition image the device already holds), or <see langword="null"/> for plain frames; capped at 64 MiB.
    /// <para>外部原始内容字典（例如设备已持有的先前分区镜像）， 为 <see langword="null"/> 时生成无字典帧；上限 64 MiB。</para></param>
    /// <returns>This builder, so calls can be chained.<para>本构建器，便于链式调用。</para></returns>
    /// <exception cref="ArgumentException">A name or path is empty or whitespace, or the dictionary exceeds 64 MiB.
    /// <para>分区名或路径为空/仅含空白，或字典超过 64 MiB。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="partitionName"/> or <paramref name="imagePath"/> is <see langword="null"/>.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="FileNotFoundException"><paramref name="imagePath"/> does not exist.
    /// <para><paramref name="imagePath"/> 不存在。</para></exception>
    public PayloadBuilder AddPartitionZstd(string partitionName, string imagePath, byte[]? zstdDictionary = null)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(imagePath);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        if (string.IsNullOrWhiteSpace(imagePath))
        {
            throw new ArgumentException("Image path cannot be empty or whitespace.", nameof(imagePath));
        }

        if (!File.Exists(imagePath))
        {
            throw new FileNotFoundException($"Image file not found: {imagePath}", nameof(imagePath));
        }

        ValidateZstdDictionary(zstdDictionary);
        RequireZstdMinorVersion();
        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            ImagePath = imagePath,
            OperationType = InstallOperation.Types.Type.ReplaceZstd,
            CompressionType = CompressionType.Zstd,
            ZstdDictionary = zstdDictionary is { Length: > 0 } ? zstdDictionary : null
        };
        _partitions.Add(info);
        return this;
    }

    /// <summary>Adds a REPLACE_ZSTD partition from a readable stream. The stream must stay readable until the build finishes and is released together with the builder.
    /// <para>由可读流添加 REPLACE_ZSTD 分区。该流必须在构建结束前保持可读，并随构建器一并释放。</para></summary>
    /// <param name="partitionName">Partition written by this update; must not be empty or whitespace and must be unique within the builder.
    /// <para>本次更新写入的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para></param>
    /// <param name="imageStream">Image content read from its current position during the build.
    /// <para>构建时从其当前位置读取的镜像内容。</para></param>
    /// <param name="zstdDictionary">External raw content dictionary, or <see langword="null"/> for plain frames; capped at 64 MiB.
    /// <para>外部原始内容字典，为 <see langword="null"/> 时生成无字典帧；上限 64 MiB。</para></param>
    /// <returns>This builder, so calls can be chained.<para>本构建器，便于链式调用。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="partitionName"/> is empty or whitespace, or the dictionary exceeds 64 MiB.
    /// <para><paramref name="partitionName"/> 为空或仅含空白，或字典超过 64 MiB。</para></exception>
    /// <exception cref="ArgumentNullException"><paramref name="partitionName"/> or <paramref name="imageStream"/> is <see langword="null"/>.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imageStream"/> 为 <see langword="null"/>。</para></exception>
    public PayloadBuilder AddPartitionZstd(string partitionName, Stream imageStream, byte[]? zstdDictionary = null)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(imageStream);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        ValidateZstdDictionary(zstdDictionary);
        RequireZstdMinorVersion();
        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            ImageStream = imageStream,
            OperationType = InstallOperation.Types.Type.ReplaceZstd,
            CompressionType = CompressionType.Zstd,
            ZstdDictionary = zstdDictionary is { Length: > 0 } ? zstdDictionary : null
        };
        _partitions.Add(info);
        return this;
    }

    private static void ValidateZstdDictionary(byte[]? dictionary)
    {
        if (dictionary is { Length: > 64 * 1024 * 1024 })
        {
            throw new ArgumentException("Zstd external dictionary exceeds the 64 MiB ceiling.", nameof(dictionary));
        }
    }

    /// <summary>
    /// Raises the manifest minor version to 10, the minimum that permits REPLACE_ZSTD operations,
    /// mirroring how incremental partitions raise it to 2.
    /// <para>把清单 minor 版本提升到 10（允许 REPLACE_ZSTD 操作的最低版本），与增量分区提升到 2 的做法一致。</para>
    /// </summary>
    private void RequireZstdMinorVersion()
    {
        if (MinorVersion < 10)
        {
            MinorVersion = 10;
        }
    }
}