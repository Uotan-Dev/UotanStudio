using System.Security.Cryptography;
using Google.Protobuf;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload;

public sealed partial class PayloadBuilder : IDisposable
{
    private readonly List<PartitionBuildInfo> _partitions = [];
    private long _maxTimestamp;
    private bool _disposed;

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
    /// Adds a compressed partition using the specified compression type.
    /// <para>使用指定的压缩类型添加压缩分区。</para>
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
            CompressionType.Brotli => InstallOperation.Types.Type.Replace,
            CompressionType.BZip2 => InstallOperation.Types.Type.ReplaceBz,
            CompressionType.Xz => InstallOperation.Types.Type.ReplaceXz,
            CompressionType.Zstd => (InstallOperation.Types.Type)14,
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
    /// Adds a zero-filled partition (for dummy or empty partitions).
    /// <para>添加零填充分区（用于虚拟或空分区）。</para>
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

    /// <summary>
    /// Builds the payload.bin file and writes it to the specified output stream.
    /// <para>构建 payload.bin 文件并将其写入指定的输出流。</para>
    /// </summary>
    public void Build(Stream outputStream)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!outputStream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        }

        if (!outputStream.CanSeek)
        {
            throw new ArgumentException("Output stream must support seeking.", nameof(outputStream));
        }

        if (_partitions.Count == 0)
        {
            throw new InvalidOperationException("At least one partition must be added before building.");
        }

        try
        {
            var compressedDataList = new ArraySegment<byte>[_partitions.Count];
            var manifest = BuildManifest(compressedDataList);
            byte[] manifestBytes;
            using (var ms = new MemoryStream())
            {
                using var cos = new CodedOutputStream(ms);
                manifest.WriteTo(cos);
                cos.Flush();
                manifestBytes = ms.ToArray();
            }

            uint metadataSigLength = 32;
            var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, metadataSigLength);

            outputStream.Write(header, 0, header.Length);
            outputStream.Write(manifestBytes, 0, manifestBytes.Length);
            outputStream.Write(new byte[metadataSigLength], 0, (int)metadataSigLength);

            foreach (var data in compressedDataList)
            {
                if (data.Array != null && data.Count > 0)
                {
                    outputStream.Write(data.Array, data.Offset, data.Count);
                }
            }
        }
        catch (PayloadGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PayloadGenerationException($"Failed to build payload: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Asynchronously builds the payload.bin file and writes it to the specified output stream.
    /// <para>异步构建 payload.bin 文件并将其写入指定的输出流。</para>
    /// </summary>
    public async Task BuildAsync(Stream outputStream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputStream);
        if (!outputStream.CanWrite)
        {
            throw new ArgumentException("Output stream must be writable.", nameof(outputStream));
        }

        if (!outputStream.CanSeek)
        {
            throw new ArgumentException("Output stream must support seeking.", nameof(outputStream));
        }

        if (_partitions.Count == 0)
        {
            throw new InvalidOperationException("At least one partition must be added before building.");
        }

        try
        {
            var compressedDataList = new ArraySegment<byte>[_partitions.Count];
            var manifest = BuildManifest(compressedDataList);
            byte[] manifestBytes;
            using (var ms = new MemoryStream())
            {
                using var cos = new CodedOutputStream(ms);
                manifest.WriteTo(cos);
                cos.Flush();
                manifestBytes = ms.ToArray();
            }

            uint metadataSigLength = 32;
            var header = PayloadHeaderBuilder.BuildHeader((ulong)manifestBytes.Length, metadataSigLength);

            await outputStream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await outputStream.WriteAsync(manifestBytes, cancellationToken).ConfigureAwait(false);
            await outputStream.WriteAsync(new byte[metadataSigLength], cancellationToken).ConfigureAwait(false);

            foreach (var data in compressedDataList)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (data.Array != null && data.Count > 0)
                {
                    await outputStream.WriteAsync(data.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (PayloadGenerationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PayloadGenerationException($"Failed to build payload: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Builds the payload.bin file and saves it to the specified file path.
    /// <para>构建 payload.bin 文件并将其保存到指定的文件路径。</para>
    /// </summary>
    public void Build(string outputPath)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path cannot be empty or whitespace.", nameof(outputPath));
        }

        using var stream = File.Create(outputPath);
        Build(stream);
    }

    /// <summary>
    /// Asynchronously builds the payload.bin file and saves it to the specified file path.
    /// <para>异步构建 payload.bin 文件并将其保存到指定的文件路径。</para>
    /// </summary>
    public async Task BuildAsync(string outputPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path cannot be empty or whitespace.", nameof(outputPath));
        }

        using var stream = File.Create(outputPath);
        await BuildAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private DeltaArchiveManifest BuildManifest(ArraySegment<byte>[] compressedDataList)
    {
        var manifest = new DeltaArchiveManifest
        {
            BlockSize = BlockSize,
            MinorVersion = MinorVersion,
            MaxTimestamp = _maxTimestamp
        };

        ulong dataBlobOffset = 0;

        for (var i = 0; i < _partitions.Count; i++)
        {
            var partition = _partitions[i];
            var partitionUpdate = new PartitionUpdate
            {
                PartitionName = partition.PartitionName
            };

            var operation = new InstallOperation
            {
                Type = partition.OperationType
            };

            if (partition.OperationType == InstallOperation.Types.Type.Zero)
            {
                var numBlocks = partition.SizeBytes / BlockSize;
                if (partition.SizeBytes % BlockSize != 0)
                {
                    numBlocks++;
                }

                operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = numBlocks });
            }
            else
            {
                var imageData = ProcessPartitionImage(partition);
                var numBlocks = imageData.OriginalSize / BlockSize;
                if (imageData.OriginalSize % BlockSize != 0)
                {
                    numBlocks++;
                }

                operation.DataLength = (ulong)imageData.CompressedData.Count;
                operation.DataOffset = dataBlobOffset;
                operation.DstExtents.Add(new Extent { StartBlock = 0, NumBlocks = numBlocks });
                operation.DataSha256Hash = ByteString.CopyFrom(imageData.DataHash);

                partitionUpdate.NewPartitionInfo = new PartitionInfo
                {
                    Size = imageData.OriginalSize,
                    Hash = ByteString.CopyFrom(imageData.SrcHash)
                };

                dataBlobOffset += (ulong)imageData.CompressedData.Count;
                compressedDataList[i] = imageData.CompressedData;
            }

            partitionUpdate.Operations.Add(operation);
            manifest.Partitions.Add(partitionUpdate);
        }

        return manifest;
    }

    private PartitionImageData ProcessPartitionImage(PartitionBuildInfo partition)
    {
        if (partition.ImagePath != null)
        {
            using var fileStream = new FileStream(partition.ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
            return ProcessPartitionImageFromStream(fileStream, partition.CompressionType);
        }

        if (partition.ImageStream != null)
        {
            using var ms = new MemoryStream();
            partition.ImageStream.CopyTo(ms);
            ms.Seek(0, SeekOrigin.Begin);
            return ProcessPartitionImageFromStream(ms, partition.CompressionType);
        }

        throw new InvalidOperationException("Partition has no image data.");
    }

    private static PartitionImageData ProcessPartitionImageFromStream(Stream inputStream, CompressionType compressionType)
    {
        byte[] srcHash;
        byte[] dataHash;
        ArraySegment<byte> compressedData;


        srcHash = SHA256.HashData(inputStream);

        inputStream.Seek(0, SeekOrigin.Begin);
        var compressedStream = new MemoryStream();
        CompressStream(inputStream, compressedStream, compressionType);

        if (compressedStream.TryGetBuffer(out var buffer))
        {
            dataHash = SHA256.HashData(buffer.AsSpan(0, buffer.Count));
            compressedData = buffer;
        }
        else
        {
            var arr = compressedStream.ToArray();
            dataHash = SHA256.HashData(arr);
            compressedData = new ArraySegment<byte>(arr);
        }

        return new PartitionImageData
        {
            SrcHash = srcHash,
            DataHash = dataHash,
            CompressedData = compressedData,
            OriginalSize = (ulong)inputStream.Length
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

    private struct PartitionImageData
    {
        public byte[] SrcHash;
        public byte[] DataHash;
        public ArraySegment<byte> CompressedData;
        public ulong OriginalSize;
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
