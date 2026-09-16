using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Core;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Exceptions;
using UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.BoundedContexts.OtaDelta.Payload;

/// <summary>
/// Builds optionally signed payloads from block-aligned partitions using at most 65536 operations of up to 1 MiB each.
/// <para>从块对齐分区构建 payload，可选择 RSA 签名，最多使用 65536 个操作，每个操作不超过 1 MiB。</para>
/// </summary>
public sealed partial class PayloadBuilder : IDisposable
{
    private readonly List<PartitionBuildInfo> _partitions = [];
    private long _maxTimestamp;
    private bool _disposed;
    private DynamicPartitionMetadata? _dynamicPartitions;
    private RSA? _signingKey;
    private string _securityPatchLevel = "";
    private readonly Dictionary<string, PartitionUpdate> _postinstall = new(StringComparer.Ordinal);

    /// <summary>Sets the target Android security patch level.</summary>
    public PayloadBuilder WithSecurityPatchLevel(string value)
    {
        _securityPatchLevel = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Copies only the postinstall fields for the named partition.</summary>
    public PayloadBuilder WithPostinstall(PartitionUpdate config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _postinstall[config.PartitionName] = new PartitionUpdate
        {
            PartitionName = config.PartitionName,
            RunPostinstall = config.RunPostinstall,
            PostinstallPath = config.PostinstallPath,
            FilesystemType = config.FilesystemType,
            PostinstallOptional = config.PostinstallOptional
        };
        return this;
    }

    /// <summary>Adds a logical image stream with bounded REPLACE compression. The caller owns the stream.</summary>
    public PayloadBuilder AddPartitionCompressed(string name, Stream stream, CompressionType compression)
    {
        var operation = compression switch
        {
            CompressionType.None => InstallOperation.Types.Type.Replace,
            CompressionType.BZip2 => InstallOperation.Types.Type.ReplaceBz,
            CompressionType.Xz => InstallOperation.Types.Type.ReplaceXz,
            _ => throw new ArgumentOutOfRangeException(nameof(compression))
        };
        AddPartition(name, stream);
        _partitions[^1].OperationType = operation;
        _partitions[^1].CompressionType = compression;
        return this;
    }


    /// <summary>Signs metadata and payload with RSA SHA-256 PKCS#1 v1.5. The caller owns the key and must keep it alive throughout the build.</summary>
    public PayloadBuilder WithSigningKey(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize is not (2048 or 4096)) throw new ArgumentException("Payload signing requires RSA 2048 or 4096.", nameof(key));
        _signingKey = key;
        return this;
    }

    /// <summary>Copies the dynamic partition layout into the payload manifest.</summary>
    public PayloadBuilder WithDynamicPartitions(DynamicPartitionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _dynamicPartitions = metadata.Clone();
        return this;
    }

    public uint BlockSize { get; private set; } = 4096;

    public uint MinorVersion { get; private set; }

    /// <summary>
    /// Initializes a new instance of the PayloadBuilder class with default settings.
    /// <para>使用默认设置初始化 PayloadBuilder 类的新实例。</para>
    /// </summary>
    public PayloadBuilder()
    {
    }

    /// <summary>
    /// Sets the block size for the payload.
    /// <para>设置 payload 的块大小。</para>
    /// </summary>
    public PayloadBuilder WithBlockSize(uint blockSize)
    {
        if (blockSize == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(blockSize), "Block size must be greater than zero.");
        }

        BlockSize = blockSize;
        return this;
    }

    /// <summary>
    /// Sets the minor version for delta payloads.
    /// <para>设置增量 payload 的次版本号。</para>
    /// </summary>
    public PayloadBuilder WithMinorVersion(uint minorVersion)
    {
        uint[] supportedVersions = [0, 2, 3, 4, 5, 6, 7, 8, 9];
        foreach (var v in supportedVersions)
        {
            if (v == minorVersion)
            {
                MinorVersion = minorVersion;
                return this;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(minorVersion),
            $"Minor version {minorVersion} is not supported. Supported values are: 0, 2, 3, 4, 5, 6, 7, 8, 9.");
    }

    /// <summary>
    /// Sets the maximum timestamp for the payload to prevent downgrading.
    /// <para>设置 payload 的最大时间戳以防止降级。</para>
    /// </summary>
    public PayloadBuilder WithMaxTimestamp(long maxTimestamp)
    {
        _maxTimestamp = maxTimestamp;
        return this;
    }

    /// <summary>
    /// Adds a full (non-delta) partition to the payload using REPLACE operations.
    /// <para>使用 REPLACE 操作向 payload 添加完整（非增量）分区。</para>
    /// </summary>
    public PayloadBuilder AddPartition(string partitionName, string imagePath)
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

        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            ImagePath = imagePath,
            OperationType = InstallOperation.Types.Type.Replace
        };
        _partitions.Add(info);
        return this;
    }

    /// <summary>
    /// Adds a full (non-delta) partition to the payload using REPLACE operations from a stream.
    /// <para>使用 REPLACE 操作从流添加完整（非增量）分区。</para>
    /// </summary>
    public PayloadBuilder AddPartition(string partitionName, Stream imageStream)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        ArgumentNullException.ThrowIfNull(imageStream);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            ImageStream = imageStream,
            OperationType = InstallOperation.Types.Type.Replace
        };
        _partitions.Add(info);
        return this;
    }

    /// <summary>
    /// Adds a partition using None, BZip2, or Xz; Android defines no Brotli or Zstd REPLACE operation.
    /// <para>使用 None、BZip2 或 Xz 添加分区；Android 未定义 Brotli 或 Zstd REPLACE 操作。</para>
    /// </summary>
    public PayloadBuilder AddPartitionCompressed(string partitionName, string imagePath, CompressionType compressionType)
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

        var opType = compressionType switch
        {
            CompressionType.None => InstallOperation.Types.Type.Replace,
            CompressionType.Brotli => throw new ArgumentException("Android payload has no Brotli REPLACE operation.", nameof(compressionType)),
            CompressionType.BZip2 => InstallOperation.Types.Type.ReplaceBz,
            CompressionType.Xz => InstallOperation.Types.Type.ReplaceXz,
            CompressionType.Zstd => throw new ArgumentException("Android payload has no defined Zstd REPLACE operation.", nameof(compressionType)),
            _ => throw new ArgumentOutOfRangeException(nameof(compressionType), $"Compression type {compressionType} is not supported.")
        };

        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            ImagePath = imagePath,
            OperationType = opType,
            CompressionType = compressionType
        };
        _partitions.Add(info);
        return this;
    }

    /// <summary>
    /// Adds a zero-filled partition, using REPLACE_BZ below minor version 4 and ZERO otherwise.
    /// <para>添加零填充分区：次版本低于 4 时使用 REPLACE_BZ，其余使用 ZERO。</para>
    /// </summary>
    public PayloadBuilder AddZeroPartition(string partitionName, long sizeBytes)
    {
        ArgumentNullException.ThrowIfNull(partitionName);
        if (string.IsNullOrWhiteSpace(partitionName))
        {
            throw new ArgumentException("Partition name cannot be empty or whitespace.", nameof(partitionName));
        }

        if (sizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sizeBytes), "Size cannot be negative.");
        }

        var info = new PartitionBuildInfo
        {
            PartitionName = partitionName,
            SizeBytes = (ulong)sizeBytes,
            OperationType = InstallOperation.Types.Type.Zero
        };
        _partitions.Add(info);
        return this;
    }

    /// <summary>Builds a payload with optional signing using bounded operations and disk-backed layout. <para>通过有界操作和磁盘临时布局构建payload（按配置签名）。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    public void Build(Stream outputStream) => BuildCore(outputStream, CancellationToken.None);

    /// <summary>Builds a payload with optional signing with cancellation between bounded operations. <para>构建payload（按配置签名），并在有界操作之间响应取消。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    public Task BuildAsync(Stream outputStream, CancellationToken cancellationToken = default)
        => Task.Run(() => BuildCore(outputStream, cancellationToken), cancellationToken);

    /// <summary>Atomically builds a payload with optional signing from block-aligned partitions. <para>从块对齐分区原子构建payload（按配置签名）。</para></summary>
    /// <remarks>Unaligned inputs and more than 65536 operations of up to 1 MiB are rejected. <para>拒绝非块对齐输入，以及超过 65536 个、单个不大于 1 MiB 的操作。</para></remarks>
    public void Build(string outputPath) => BuildFile(outputPath, CancellationToken.None);

    /// <summary>Atomically builds a payload with optional signing with cancellation support. <para>以支持取消的方式原子构建payload（按配置签名）。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    public Task BuildAsync(string outputPath, CancellationToken cancellationToken = default)
        => Task.Run(() => BuildFile(outputPath, cancellationToken), cancellationToken);

    private void BuildFile(string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                BuildCore(stream, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private void BuildCore(Stream output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!output.CanWrite) throw new ArgumentException("Output stream must be writable.", nameof(output));
        if (_partitions.Count == 0) throw new InvalidOperationException("At least one partition is required.");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var data = UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CreateTemporaryStream();
            var manifest = BuildManifest(data, cancellationToken);
            if (_signingKey is not null)
            {
                WriteSignedPayload(output, data, manifest, _signingKey, cancellationToken);
                return;
            }
            if (manifest.CalculateSize() > 64 * 1024 * 1024) throw new PayloadGenerationException("Payload manifest exceeds 64 MiB.");
            var manifestBytes = manifest.ToByteArray();
            var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, 0);
            output.Write(header);
            output.Write(manifestBytes);
            data.Position = 0;
            UotanStudio.Firmware.BoundedContexts.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(data, output, data.Length, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (PayloadGenerationException) { throw; }
        catch (Exception ex) { throw new PayloadGenerationException($"Failed to build payload: {ex.Message}", ex); }
    }

    private DeltaArchiveManifest BuildManifest(FileStream data, CancellationToken cancellationToken)
    {
        var compressionWorkers = _compressionWorkers ?? SharedKernel.FirmwareParallelism.Workers;
        const int maximumOperationBytes = 1024 * 1024;
        const int maximumOperations = 65536;
        if (BlockSize > maximumOperationBytes) throw new PayloadGenerationException("Payload block size exceeds the 1 MiB operation limit.");
        if (_partitions.Count > maximumOperations) throw new PayloadGenerationException("Payload exceeds the supported 65536 partitions.");
        var chunkSize = checked((int)(maximumOperationBytes / BlockSize * BlockSize));
        var manifest = new DeltaArchiveManifest
        {
            BlockSize = BlockSize,
            MinorVersion = MinorVersion,
            MaxTimestamp = _maxTimestamp,
            SecurityPatchLevel = _securityPatchLevel,
            DynamicPartitionMetadata = _dynamicPartitions?.Clone()
        };
        if (_postinstall.Keys.Any(n => !_partitions.Any(p => p.PartitionName == n)))
            throw new PayloadGenerationException("Postinstall refers to an absent partition.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        var operationCount = 0;
        foreach (var partition in _partitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!names.Add(partition.PartitionName)) throw new PayloadGenerationException("Duplicate payload partition name.");
            var update = _postinstall.TryGetValue(partition.PartitionName, out var config) ? config.Clone() : new PartitionUpdate { PartitionName = partition.PartitionName };
            using var partitionHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            if (partition.OperationType == InstallOperation.Types.Type.Zero)
            {
                if (partition.SizeBytes % BlockSize != 0) throw new PayloadGenerationException("Partition size must be block-aligned.");
                var zeroOperationCount = partition.SizeBytes / (ulong)chunkSize + (partition.SizeBytes % (ulong)chunkSize == 0 ? 0UL : 1UL);
                if (zeroOperationCount > (ulong)(maximumOperations - operationCount))
                    throw new PayloadGenerationException("Payload exceeds the supported 65536 operations.");
                var buffer = new byte[chunkSize];
                var remaining = partition.SizeBytes;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = (int)Math.Min((ulong)buffer.Length, remaining);
                    var operation = MinorVersion >= 4
                        ? new InstallOperation { Type = InstallOperation.Types.Type.Zero, DstLength = (ulong)count }
                        : WriteReplaceOperation(data, buffer, count, InstallOperation.Types.Type.ReplaceBz, CompressionType.BZip2, cancellationToken);
                    operation.DstExtents.Add(new Extent { StartBlock = (partition.SizeBytes - remaining) / BlockSize, NumBlocks = (ulong)count / BlockSize });
                    update.Operations.Add(operation); operationCount++;
                    partitionHash.AppendData(buffer, 0, count); remaining -= (ulong)count;
                }
                update.NewPartitionInfo = new PartitionInfo { Size = partition.SizeBytes, Hash = ByteString.CopyFrom(partitionHash.GetHashAndReset()) };
            }
            else
            {
                using var owned = partition.ImagePath is null ? null : File.OpenRead(partition.ImagePath);
                var input = owned ?? partition.ImageStream ?? throw new PayloadGenerationException("Partition has no image stream.");
                if (!input.CanRead) throw new PayloadGenerationException("Partition stream must be readable.");
                var declaredLength = input.CanSeek ? checked(input.Length - input.Position) : (long?)null;
                if (declaredLength is { } knownLength && (knownLength < 0 || knownLength % BlockSize != 0))
                    throw new PayloadGenerationException("Partition size must be block-aligned.");
                var position = WritePartitionOperations(input, declaredLength, data, partition, update, partitionHash,
                    chunkSize, ref operationCount, compressionWorkers, cancellationToken);
                update.NewPartitionInfo = new PartitionInfo { Size = position, Hash = ByteString.CopyFrom(partitionHash.GetHashAndReset()) };
            }
            manifest.Partitions.Add(update);
        }
        return manifest;
    }

    private static InstallOperation WriteReplaceOperation(FileStream data, byte[] buffer, int count,
        InstallOperation.Types.Type operationType, CompressionType compressionType, CancellationToken cancellationToken)
    {
        var offset = data.Position;
        using var hashing = new UotanStudio.Firmware.SharedKernel.Streams.HashingWriteStream(data, leaveOpen: true);
        using (var source = new MemoryStream(buffer, 0, count, writable: false))
        using (var bounded = new UotanStudio.Firmware.SharedKernel.Streams.CountingWriteStream(hashing, count * 2L + 65536, leaveOpen: true, cancellationToken: cancellationToken))
            CompressStream(source, bounded, compressionType);
        var storedLength = hashing.BytesWritten;
        var digest = hashing.GetHashAndReset();
        return new InstallOperation
        {
            Type = operationType,
            DataOffset = (ulong)offset,
            DataLength = (ulong)storedLength,
            DataSha256Hash = ByteString.CopyFrom(digest),
            DstLength = (ulong)count
        };
    }

    /// <summary>
    /// Releases all resources used by the PayloadBuilder.
    /// <para>释放 PayloadBuilder 使用的所有资源。</para>
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var partition in _partitions)
            {
                partition.ImageStream?.Dispose();
            }

            _partitions.Clear();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private sealed class PartitionBuildInfo
    {
        public string PartitionName { get; set; } = string.Empty;
        public string? ImagePath { get; set; }
        public Stream? ImageStream { get; set; }
        public ulong SizeBytes { get; set; }
        public InstallOperation.Types.Type OperationType { get; set; }
        public CompressionType CompressionType { get; set; }
    }
}

public enum CompressionType
{
    None = 0,

    Brotli = 1,

    BZip2 = 2,

    Xz = 3,

    Zstd = 4
}