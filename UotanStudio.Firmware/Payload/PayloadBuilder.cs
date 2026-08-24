using System.Security.Cryptography;
using Google.Protobuf;
using SharpCompress.Compressors.BZip2;
using UotanStudio.Firmware.Payload.Core;
using UotanStudio.Firmware.Payload.Exceptions;
using UotanStudio.Firmware.Payload.Proto;

namespace UotanStudio.Firmware.Payload;

/// <summary>
/// Provides functionality to build Android OTA payload.bin files from partition images.
/// <para>提供从分区镜像构建 Android OTA payload.bin 文件的功能。</para>
/// </summary>
public sealed class PayloadBuilder : IDisposable
{
    private readonly List<PartitionBuildInfo> _partitions = [];
    private long _maxTimestamp;
    private bool _disposed;

    /// <summary>
    /// Gets the block size used for building the payload.
    /// <para>获取用于构建 payload 的块大小。</para>
    /// </summary>
    public uint BlockSize { get; private set; } = 4096;

    /// <summary>
    /// Gets the minor version of the payload format.
    /// <para>获取 payload 格式的次版本号。</para>
    /// </summary>
    public uint MinorVersion { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="PayloadBuilder"/> class with default settings.
    /// <para>使用默认设置初始化 <see cref="PayloadBuilder"/> 类的新实例。</para>
    /// </summary>
    public PayloadBuilder()
    {
    }

    /// <summary>
    /// Sets the block size for the payload. Default is 4096 bytes.
    /// <para>设置 payload 的块大小。默认为 4096 字节。</para>
    /// </summary>
    /// <param name="blockSize">The block size in bytes. Must be a positive value.
    /// <para>块大小（字节）。必须为正值。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="blockSize"/> is zero.
    /// <para>当 <paramref name="blockSize"/> 为零时抛出。</para>
    /// </exception>
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
    /// Sets the minor version for delta payloads. Default is 0.
    /// <para>设置增量 payload 的次版本号。默认为 0。</para>
    /// </summary>
    /// <param name="minorVersion">The minor version number. Supported values are 0, 2, 3, 4, 5, 6, 8, 9.
    /// <para>次版本号。支持的值为 0, 2, 3, 4, 5, 6, 8, 9。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="minorVersion"/> is not supported.
    /// <para>当 <paramref name="minorVersion"/> 不受支持时抛出。</para>
    /// </exception>
    public PayloadBuilder WithMinorVersion(uint minorVersion)
    {
        uint[] supportedVersions = [0, 2, 3, 4, 5, 6, 8, 9];
        foreach (var v in supportedVersions)
        {
            if (v == minorVersion)
            {
                MinorVersion = minorVersion;
                return this;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(minorVersion),
            $"Minor version {minorVersion} is not supported. Supported values are: 0, 2, 3, 4, 5, 6, 8, 9.");
    }

    /// <summary>
    /// Sets the maximum timestamp for the payload to prevent downgrading.
    /// <para>设置 payload 的最大时间戳以防止降级。</para>
    /// </summary>
    /// <param name="maxTimestamp">The maximum timestamp value.
    /// <para>最大时间戳值。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    public PayloadBuilder WithMaxTimestamp(long maxTimestamp)
    {
        _maxTimestamp = maxTimestamp;
        return this;
    }

    /// <summary>
    /// Adds a full (non-delta) partition to the payload using REPLACE operations.
    /// <para>使用 REPLACE 操作向 payload 添加完整（非增量）分区。</para>
    /// </summary>
    /// <param name="partitionName">The name of the partition (e.g., "system", "boot").
    /// <para>分区名称（例如 "system"、"boot"）。</para>
    /// </param>
    /// <param name="imagePath">The path to the partition image file.
    /// <para>分区镜像文件的路径。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partitionName"/> or <paramref name="imagePath"/> is null.
    /// <para>当 <paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="partitionName"/> or <paramref name="imagePath"/> is empty or whitespace.
    /// <para>当 <paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为空或空白字符时抛出。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the image file does not exist.
    /// <para>当镜像文件不存在时抛出。</para>
    /// </exception>
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
    /// <param name="partitionName">The name of the partition.
    /// <para>分区名称。</para>
    /// </param>
    /// <param name="imageStream">The stream containing the partition image data.
    /// <para>包含分区镜像数据的流。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partitionName"/> or <paramref name="imageStream"/> is null.
    /// <para>当 <paramref name="partitionName"/> 或 <paramref name="imageStream"/> 为 null 时抛出。</para>
    /// </exception>
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
    /// <param name="partitionName">The name of the partition.
    /// <para>分区名称。</para>
    /// </param>
    /// <param name="imagePath">The path to the partition image file.
    /// <para>分区镜像文件的路径。</para>
    /// </param>
    /// <param name="compressionType">The compression type to use.
    /// <para>要使用的压缩类型。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.
    /// <para>当任何参数为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when any parameter is empty or whitespace.
    /// <para>当任何参数为空或空白字符时抛出。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">Thrown when the image file does not exist.
    /// <para>当镜像文件不存在时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="compressionType"/> is not supported.
    /// <para>当 <paramref name="compressionType"/> 不受支持时抛出。</para>
    /// </exception>
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
            CompressionType.Zstd => (InstallOperation.Types.Type)14, // REPLACE_ZSTD (not in proto but supported)
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
    /// <param name="partitionName">The name of the partition.
    /// <para>分区名称。</para>
    /// </param>
    /// <param name="sizeBytes">The size of the partition in bytes.
    /// <para>分区大小（字节）。</para>
    /// </param>
    /// <returns>This <see cref="PayloadBuilder"/> instance for method chaining.
    /// <para>此 <see cref="PayloadBuilder"/> 实例，用于方法链。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="partitionName"/> is null.
    /// <para>当 <paramref name="partitionName"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="partitionName"/> is empty or whitespace.
    /// <para>当 <paramref name="partitionName"/> 为空或空白字符时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="sizeBytes"/> is negative.
    /// <para>当 <paramref name="sizeBytes"/> 为负时抛出。</para>
    /// </exception>
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
    /// <param name="outputStream">The stream to write the payload to. Must be writable and support seeking.
    /// <para>要写入 payload 的流。必须可写并支持寻址。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputStream"/> is null.
    /// <para>当 <paramref name="outputStream"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when no partitions have been added.
    /// <para>当未添加任何分区时抛出。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">Thrown when payload generation fails.
    /// <para>当 payload 生成失败时抛出。</para>
    /// </exception>
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
    /// <param name="outputStream">The stream to write the payload to. Must be writable and support seeking.
    /// <para>要写入 payload 的流。必须可写并支持寻址。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task representing the asynchronous operation.
    /// <para>表示异步操作的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputStream"/> is null.
    /// <para>当 <paramref name="outputStream"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">Thrown when no partitions have been added.
    /// <para>当未添加任何分区时抛出。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">Thrown when payload generation fails.
    /// <para>当 payload 生成失败时抛出。</para>
    /// </exception>
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
    /// <param name="outputPath">The path where the payload file will be saved.
    /// <para>将保存 payload 文件的路径。</para>
    /// </param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputPath"/> is null.
    /// <para>当 <paramref name="outputPath"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="outputPath"/> is empty or whitespace.
    /// <para>当 <paramref name="outputPath"/> 为空或空白字符时抛出。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">Thrown when payload generation fails.
    /// <para>当 payload 生成失败时抛出。</para>
    /// </exception>
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
    /// <param name="outputPath">The path where the payload file will be saved.
    /// <para>将保存 payload 文件的路径。</para>
    /// </param>
    /// <param name="cancellationToken">The token to monitor for cancellation requests.
    /// <para>用于监视取消请求的令牌。</para>
    /// </param>
    /// <returns>A task representing the asynchronous operation.
    /// <para>表示异步操作的任务。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="outputPath"/> is null.
    /// <para>当 <paramref name="outputPath"/> 为 null 时抛出。</para>
    /// </exception>
    /// <exception cref="ArgumentException">Thrown when <paramref name="outputPath"/> is empty or whitespace.
    /// <para>当 <paramref name="outputPath"/> 为空或空白字符时抛出。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">Thrown when payload generation fails.
    /// <para>当 payload 生成失败时抛出。</para>
    /// </exception>
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

        using (var sha = SHA256.Create())
        {
            srcHash = sha.ComputeHash(inputStream);

            inputStream.Seek(0, SeekOrigin.Begin);
            var compressedStream = new MemoryStream();
            CompressStream(inputStream, compressedStream, compressionType);

            sha.Initialize();

            if (compressedStream.TryGetBuffer(out var buffer))
            {
                dataHash = sha.ComputeHash(buffer.Array!, buffer.Offset, buffer.Count);
                compressedData = buffer;
            }
            else
            {
                var arr = compressedStream.ToArray();
                dataHash = sha.ComputeHash(arr);
                compressedData = new ArraySegment<byte>(arr);
            }
        }

        return new PartitionImageData
        {
            SrcHash = srcHash,
            DataHash = dataHash,
            CompressedData = compressedData,
            OriginalSize = (ulong)inputStream.Length
        };
    }

    private static void CompressStream(Stream inputStream, Stream outputStream, CompressionType compressionType)
    {
        switch (compressionType)
        {
            case CompressionType.None:
                inputStream.CopyTo(outputStream, 81920);
                break;
            case CompressionType.Brotli:
                {
                    // There is no Brotli full-partition REPLACE operation in the payload
                    // proto (brotli only appears in BROTLI_BSDIFF delta operations), so a
                    // brotli-compressed blob would be written as a raw REPLACE and come out
                    // corrupted on extraction. Store the data uncompressed instead.
                    // <para>payload 协议中没有 Brotli 整分区 REPLACE 操作（brotli 仅用于 BROTLI_BSDIFF
                    // 增量操作），若按 REPLACE 写入 brotli 压缩数据会在提取时损坏。因此此处按未压缩存储。</para>
                    inputStream.CopyTo(outputStream, 81920);
                }
                break;
            case CompressionType.BZip2:
                {
                    var bzData = CompressBZip2(ReadStreamFully(inputStream));
                    if (bzData.Array != null)
                    {
                        outputStream.Write(bzData.Array, bzData.Offset, bzData.Count);
                    }
                }
                break;
            case CompressionType.Xz:
                {
                    var xzData = CompressXz(ReadStreamFully(inputStream));
                    outputStream.Write(xzData, 0, xzData.Length);
                }
                break;
            case CompressionType.Zstd:
                {
                    var zstdData = CompressZstd(ReadStreamFully(inputStream));
                    outputStream.Write(zstdData, 0, zstdData.Length);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(compressionType), $"Compression type {compressionType} is not supported.");
        }
    }

    private static byte[] ReadStreamFully(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer))
        {
            var result = new byte[buffer.Count];
            Array.Copy(buffer.Array!, buffer.Offset, result, 0, buffer.Count);
            return result;
        }

        using var copyMs = new MemoryStream();
        stream.CopyTo(copyMs, 81920);
        return copyMs.ToArray();
    }

    private static ArraySegment<byte> CompressBZip2(byte[] data)
    {
        using var inputStream = new MemoryStream(data);
        var outputStream = new MemoryStream();
        using (var bzStream = BZip2Stream.Create(outputStream, SharpCompress.Compressors.CompressionMode.Compress, false))
        {
            inputStream.CopyTo(bzStream, 81920);
        }

        return outputStream.TryGetBuffer(out var buffer) ? buffer : new ArraySegment<byte>(outputStream.ToArray());
    }

    private static byte[] CompressXz(byte[] data)
    {
        var xzCompressor = new XzCompressor();
        return xzCompressor.Compress(data);
    }

    private static byte[] CompressZstd(byte[] data)
    {
        using var compressor = new ZstdSharp.Compressor();
        return compressor.Wrap(data).ToArray();
    }

    /// <summary>
    /// Releases all resources used by the <see cref="PayloadBuilder"/>.
    /// <para>释放 <see cref="PayloadBuilder"/> 使用的所有资源。</para>
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

/// <summary>
/// Specifies the compression type for partition data.
/// <para>指定分区数据的压缩类型。</para>
/// </summary>
public enum CompressionType
{
    /// <summary>
    /// No compression (raw data).
    /// <para>无压缩（原始数据）。</para>
    /// </summary>
    None = 0,

    /// <summary>
    /// Brotli compression.
    /// <para>Brotli 压缩。</para>
    /// </summary>
    Brotli = 1,

    /// <summary>
    /// BZip2 compression.
    /// <para>BZip2 压缩。</para>
    /// </summary>
    BZip2 = 2,

    /// <summary>
    /// XZ compression.
    /// <para>XZ 压缩。</para>
    /// </summary>
    Xz = 3,

    /// <summary>
    /// Zstandard compression.
    /// <para>Zstandard 压缩。</para>
    /// </summary>
    Zstd = 4
}