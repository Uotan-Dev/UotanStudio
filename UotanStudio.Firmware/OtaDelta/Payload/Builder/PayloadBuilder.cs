using System.Security.Cryptography;
using Google.Protobuf;
using UotanStudio.Firmware.Core.IO;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Models;
using UotanStudio.Firmware.OtaDelta.Payload.Proto;

namespace UotanStudio.Firmware.OtaDelta.Payload.Builder;

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

    /// <summary>Sets the target Android security patch level.
    /// <para>设置目标 Android 安全补丁级别。</para></summary>
    /// <param name="value">
    /// Text written verbatim into the manifest's security-patch-level field; an empty string leaves the field unset
    /// in the output. <para>原样写入清单安全补丁级别字段的文本；空串会使输出中该字段保持未设置。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="value"/> is <see langword="null"/>. <para><paramref name="value"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public PayloadBuilder WithSecurityPatchLevel(string value)
    {
        _securityPatchLevel = value ?? throw new ArgumentNullException(nameof(value));
        return this;
    }

    /// <summary>Copies only the postinstall fields for the named partition.
    /// <para>仅复制指定分区的 postinstall 字段。</para></summary>
    /// <param name="config">
    /// Update carrying the partition name together with its postinstall flags, path and filesystem type; operations,
    /// extents and partition info on the same object are ignored, and a later call for the same partition name
    /// replaces the earlier one.
    /// <para>携带分区名及其 postinstall 标志、路径与文件系统类型的更新项；同一对象上的操作、范围与分区信息会被忽略，
    /// 对同一分区名的后续调用会覆盖先前记录。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained.
    /// <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="config"/> is <see langword="null"/>. <para><paramref name="config"/> 为 <see langword="null"/>。</para>
    /// </exception>
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

    /// <summary>
    /// Adds a logical image stream with bounded REPLACE compression. The stream must stay readable until the build
    /// finishes and is released together with the builder.
    /// <para>以有界的 REPLACE 压缩添加逻辑镜像流。该流必须在构建结束前保持可读，并随构建器一并释放。</para>
    /// </summary>
    /// <param name="name">
    /// Partition name recorded for this update; it must not be empty or whitespace and must be unique within the
    /// builder. <para>本次更新记录的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para>
    /// </param>
    /// <param name="stream">
    /// Image content read from its current position; it must be readable, and its remaining length must be a
    /// multiple of the block size unless the stream cannot seek.
    /// <para>从其当前位置读取的镜像内容；必须可读，且除非流不可定位，其剩余长度必须是块大小的整数倍。</para>
    /// </param>
    /// <param name="compression">
    /// Codec for the REPLACE operations: <see cref="CompressionType.None"/>, <see cref="CompressionType.BZip2"/> or
    /// <see cref="CompressionType.Xz"/>. <para>REPLACE 操作的编解码器：<see cref="CompressionType.None"/>、
    /// <see cref="CompressionType.BZip2"/> 或 <see cref="CompressionType.Xz"/>。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="name"/> is empty or whitespace. <para><paramref name="name"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compression"/> requests a codec with no emitted REPLACE operation.
    /// <para><paramref name="compression"/> 请求了没有对应 REPLACE 输出的编解码器。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="name"/> or <paramref name="stream"/> is <see langword="null"/>.
    /// <para><paramref name="name"/> 或 <paramref name="stream"/> 为 <see langword="null"/>。</para>
    /// </exception>
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


    /// <summary>Signs metadata and payload with RSA SHA-256 PKCS#1 v1.5. The caller owns the key and must keep it alive throughout the build.
    /// <para>使用 RSA SHA-256 PKCS#1 v1.5 对元数据与载荷签名。密钥所有权归调用方，且必须在整个构建期间保持存活。</para></summary>
    /// <param name="key">
    /// Signing key with a 2048- or 4096-bit modulus; the builder never disposes it.
    /// <para>模数为 2048 或 4096 位的签名密钥；构建器不会释放该密钥。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. Every later build signs until the key is set again.
    /// <para>本构建器，便于链式调用。设置之后每次构建都会签名，除非再次设置密钥。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> has a modulus other than 2048 or 4096 bits.
    /// <para><paramref name="key"/> 的模数不是 2048 或 4096 位。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="key"/> is <see langword="null"/>. <para><paramref name="key"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public PayloadBuilder WithSigningKey(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.KeySize is not (2048 or 4096)) throw new ArgumentException("Payload signing requires RSA 2048 or 4096.", nameof(key));
        _signingKey = key;
        return this;
    }

    /// <summary>Copies the dynamic partition layout into the payload manifest.
    /// <para>将动态分区布局复制到 payload 清单中。</para></summary>
    /// <param name="metadata">
    /// Layout recorded with the payload; a deep copy is stored, so later changes to the caller's instance do not
    /// reach the built payload. <para>随载荷记录的布局；存储的是深拷贝，因此调用方实例之后的改动不会影响已构建的载荷。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="metadata"/> is <see langword="null"/>. <para><paramref name="metadata"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public PayloadBuilder WithDynamicPartitions(DynamicPartitionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        _dynamicPartitions = metadata.Clone();
        return this;
    }

    /// <summary>Block size in bytes that partition extents are expressed in.
    /// <para>分区范围所使用的块大小（字节）。</para></summary>
    /// <value>
    /// <c>4096</c> unless <see cref="WithBlockSize"/> changed it; the value must be greater than zero and no larger
    /// than 1 MiB for a build to succeed.
    /// <para>除非经 <see cref="WithBlockSize"/> 修改，否则为 <c>4096</c>；构建成功要求该值大于零且不超过 1 MiB。</para>
    /// </value>
    public uint BlockSize { get; private set; } = 4096;

    /// <summary>Payload minor version selecting the operation vocabulary.
    /// <para>决定操作词汇表的 payload 次版本号。</para></summary>
    /// <value>
    /// <c>0</c> unless set, and then one of <c>0</c> or <c>2</c> through <c>9</c>. Version <c>4</c> and above emit
    /// zero-filled regions as ZERO operations; adding an incremental partition raises a lower version to <c>2</c>.
    /// <para>未设置时为 <c>0</c>，设置后取 <c>0</c> 或 <c>2</c> 至 <c>9</c>。版本 <c>4</c> 及以上把零填充区域输出为
    /// ZERO 操作；添加增量分区会把较低版本提升到 <c>2</c>。</para>
    /// </value>
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
    /// <param name="blockSize">
    /// Block size in bytes recorded in the manifest and used for every extent; it must not be zero, and a build
    /// rejects a value above 1 MiB because one operation may not exceed that size.
    /// <para>记入清单并用于所有范围的块大小（字节）；不得为零，且构建时会拒绝超过 1 MiB 的值，因为单个操作不得超过该大小。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="blockSize"/> is <c>0</c>. <para><paramref name="blockSize"/> 为 <c>0</c>。</para>
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
    /// Sets the minor version for delta payloads.
    /// <para>设置增量 payload 的次版本号。</para>
    /// </summary>
    /// <param name="minorVersion">
    /// One of <c>0</c> or <c>2</c> through <c>9</c>; version <c>1</c> is not accepted. Version <c>4</c> and above
    /// switch zero-filled partitions from compressed REPLACE to ZERO operations.
    /// <para>取 <c>0</c> 或 <c>2</c> 至 <c>9</c> 之一；不接受版本 <c>1</c>。版本 <c>4</c> 及以上把零填充分区由压缩
    /// REPLACE 切换为 ZERO 操作。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="minorVersion"/> is outside the supported set. <para><paramref name="minorVersion"/> 超出支持集合。</para>
    /// </exception>
    public PayloadBuilder WithMinorVersion(uint minorVersion)
    {
        if (!PayloadHeader.SupportedMinorVersions.Contains(minorVersion))
        {
            throw new ArgumentOutOfRangeException(nameof(minorVersion),
                $"Minor version {minorVersion} is not supported. Supported values are: {string.Join(", ", PayloadHeader.SupportedMinorVersions.OrderBy(static v => v))}.");
        }

        MinorVersion = minorVersion;
        return this;
    }

    /// <summary>
    /// Sets the maximum timestamp for the payload to prevent downgrading.
    /// <para>设置 payload 的最大时间戳以防止降级。</para>
    /// </summary>
    /// <param name="maxTimestamp">
    /// Value written unchanged into the manifest's maximum-timestamp field; no range is validated here.
    /// <para>原样写入清单最大时间戳字段的值；此处不做范围校验。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
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
    /// <param name="partitionName">
    /// Partition written by this update; it must not be empty or whitespace and must be unique within the builder.
    /// <para>本次更新写入的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para>
    /// </param>
    /// <param name="imagePath">
    /// Image file to embed; existence is checked here and the file itself is opened by the build, so its content may
    /// still change until then.
    /// <para>待嵌入的镜像文件；此处仅检查存在性，实际打开发生在构建过程，因此在此之前其内容仍可能变化。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> or <paramref name="imagePath"/> is empty or whitespace.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/> or <paramref name="imagePath"/> is <see langword="null"/>.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="imagePath"/> does not exist. <para><paramref name="imagePath"/> 不存在。</para>
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
    /// <param name="partitionName">
    /// Partition written by this update; it must not be empty or whitespace and must be unique within the builder.
    /// <para>本次更新写入的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para>
    /// </param>
    /// <param name="imageStream">
    /// Image content read from its current position during the build; it must stay readable until then and is
    /// released when the builder is disposed.
    /// <para>构建时从其当前位置读取的镜像内容；在此之前必须保持可读，并在构建器释放时一并关闭。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> is empty or whitespace. <para><paramref name="partitionName"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/> or <paramref name="imageStream"/> is <see langword="null"/>.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imageStream"/> 为 <see langword="null"/>。</para>
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
    /// Adds a partition using the standard None, BZip2, or Xz operations. Zstd operation 14 is a
    /// read-only extension compatibility feature and is not emitted by this builder.
    /// <para>使用标准 None、BZip2 或 Xz 操作添加分区。Zstd 操作 14 仅作为扩展兼容读取能力，本构建器不生成。</para>
    /// </summary>
    /// <param name="partitionName">
    /// Partition written by this update; it must not be empty or whitespace and must be unique within the builder.
    /// <para>本次更新写入的分区名；不得为空或仅含空白，且在构建器内必须唯一。</para>
    /// </param>
    /// <param name="imagePath">
    /// Image file to compress into REPLACE operations; existence is checked here and the file is opened by the build.
    /// <para>待压缩为 REPLACE 操作的镜像文件；此处检查存在性，实际打开发生在构建过程。</para>
    /// </param>
    /// <param name="compressionType">
    /// Codec for the emitted operations; <see cref="CompressionType.None"/>, <see cref="CompressionType.BZip2"/> and
    /// <see cref="CompressionType.Xz"/> map to REPLACE, REPLACE_BZ and REPLACE_XZ.
    /// <para>输出操作使用的编解码器；<see cref="CompressionType.None"/>、<see cref="CompressionType.BZip2"/> 与
    /// <see cref="CompressionType.Xz"/> 分别映射为 REPLACE、REPLACE_BZ 与 REPLACE_XZ。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A name or path is empty or whitespace, or <paramref name="compressionType"/> names a codec with no emitted
    /// REPLACE operation. <para>分区名或路径为空/仅含空白，或 <paramref name="compressionType"/> 指定了没有对应
    /// REPLACE 输出的编解码器。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="compressionType"/> is not a defined member. <para><paramref name="compressionType"/> 不是已定义的成员。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/> or <paramref name="imagePath"/> is <see langword="null"/>.
    /// <para><paramref name="partitionName"/> 或 <paramref name="imagePath"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="imagePath"/> does not exist. <para><paramref name="imagePath"/> 不存在。</para>
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
            CompressionType.Brotli => throw new ArgumentException("Android payload has no Brotli REPLACE operation.", nameof(compressionType)),
            CompressionType.BZip2 => InstallOperation.Types.Type.ReplaceBz,
            CompressionType.Xz => InstallOperation.Types.Type.ReplaceXz,
            CompressionType.Zstd => throw new ArgumentException("Zstd REPLACE operation 14 is supported for extension reading only; this builder does not emit it.", nameof(compressionType)),
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
    /// <param name="partitionName">
    /// Partition to clear; it must not be empty or whitespace and must be unique within the builder.
    /// <para>待清零的分区；不得为空或仅含空白，且在构建器内必须唯一。</para>
    /// </param>
    /// <param name="sizeBytes">
    /// Number of bytes to zero, which must be a multiple of <see cref="BlockSize"/> by the time the build runs;
    /// <c>0</c> yields a partition update with no operations.
    /// <para>要清零的字节数，构建运行时必须是 <see cref="BlockSize"/> 的整数倍；为 <c>0</c> 时得到不含任何操作的分区更新项。</para>
    /// </param>
    /// <returns>
    /// This builder, so calls can be chained. <para>本构建器，便于链式调用。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="partitionName"/> is empty or whitespace. <para><paramref name="partitionName"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="partitionName"/> is <see langword="null"/>. <para><paramref name="partitionName"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="sizeBytes"/> is negative. <para><paramref name="sizeBytes"/> 为负数。</para>
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

    /// <summary>Builds a payload with optional signing using bounded operations and disk-backed layout. <para>通过有界操作和磁盘临时布局构建payload（按配置签名）。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    /// <param name="outputStream">
    /// Stream written from its current position; the caller keeps it open and the builder never disposes it.
    /// <para>从其当前位置写入的流；调用方保持其打开，构建器不会释放它。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputStream"/> cannot be written to. <para><paramref name="outputStream"/> 不可写。</para>
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="outputStream"/> is <see langword="null"/>. <para><paramref name="outputStream"/> 为 <see langword="null"/>。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No partition was added. <para>未添加任何分区。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This builder has already been disposed. <para>本构建器已被释放。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">
    /// Any structural problem in the inputs — unaligned size, duplicate partition name, a postinstall without a
    /// matching partition, an operation or manifest limit breach — or an I/O failure while writing.
    /// <para>输入中的任何结构问题——大小未块对齐、分区名重复、postinstall 无对应分区、超出操作或清单上限——或写出时的
    /// I/O 失败。</para>
    /// </exception>
    public void Build(Stream outputStream) => BuildCore(outputStream, CancellationToken.None);

    /// <summary>Builds a payload with optional signing with cancellation between bounded operations. <para>构建payload（按配置签名），并在有界操作之间响应取消。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    /// <param name="outputStream">
    /// Stream written from its current position; the caller keeps it open and the builder never disposes it.
    /// <para>从其当前位置写入的流；调用方保持其打开，构建器不会释放它。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels between partitions, between operations and inside compression; a cancelled build leaves whatever the
    /// stream already received but writes nothing further.
    /// <para>在分区间、操作间以及压缩过程中取消；被取消的构建会保留流已收到的内容，取消后不写入任何数据。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the header, manifest and data streams are written; it faults with
    /// <see cref="OperationCanceledException"/> on cancellation and with
    /// <see cref="PayloadGenerationException"/> on an invalid input.
    /// <para>在头部、清单与数据流写出后完成的任务；取消时以 <see cref="OperationCanceledException"/> 进入故障状态，
    /// 输入非法时以 <see cref="PayloadGenerationException"/> 进入故障状态。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputStream"/> cannot be written to. <para><paramref name="outputStream"/> 不可写。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No partition was added. <para>未添加任何分区。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This builder has already been disposed. <para>本构建器已被释放。</para>
    /// </exception>
    public Task BuildAsync(Stream outputStream, CancellationToken cancellationToken = default)
        => Task.Run(() => BuildCore(outputStream, cancellationToken), cancellationToken);

    /// <summary>Atomically builds a payload with optional signing from block-aligned partitions. <para>从块对齐分区原子构建payload（按配置签名）。</para></summary>
    /// <remarks>
    /// Unaligned inputs and more than 65536 operations of up to 1 MiB are rejected. The payload is written to a
    /// sibling temporary file and moved into place only after a complete build.
    /// <para>拒绝非块对齐输入，以及超过 65536 个、单个不大于 1 MiB 的操作。载荷先写入同目录临时文件，完整构建后才移动到
    /// 目标位置。</para>
    /// </remarks>
    /// <param name="outputPath">
    /// Destination file, replaced only after a complete build; the parent directory is created when missing.
    /// <para>目标文件，仅在完整构建后被替换；缺失的父目录会被创建。</para>
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputPath"/> is empty or whitespace. <para><paramref name="outputPath"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No partition was added. <para>未添加任何分区。</para>
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This builder has already been disposed. <para>本构建器已被释放。</para>
    /// </exception>
    /// <exception cref="PayloadGenerationException">
    /// Any limit or input problem reported by the stream overload; the previous file at
    /// <paramref name="outputPath"/> is left untouched.
    /// <para>流重载会上报的任何上限或输入问题；<paramref name="outputPath"/> 处的既有文件保持原样。</para>
    /// </exception>
    public void Build(string outputPath) => BuildFile(outputPath, CancellationToken.None);

    /// <summary>Atomically builds a payload with optional signing with cancellation support. <para>以支持取消的方式原子构建payload（按配置签名）。</para></summary>
    /// <remarks>Inputs must be block-aligned. At most 65536 operations, each at most 1 MiB, are accepted. <para>输入必须块对齐；最多接受 65536 个操作，每个操作不超过 1 MiB。</para></remarks>
    /// <param name="outputPath">
    /// Destination file, replaced only after a complete build; the parent directory is created when missing.
    /// <para>目标文件，仅在完整构建后被替换；缺失的父目录会被创建。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels the build before the file is committed; the temporary file is then removed and the existing
    /// destination is preserved.
    /// <para>在文件提交前取消构建；此时临时文件会被删除，已有目标文件得以保留。</para>
    /// </param>
    /// <returns>
    /// A task that completes once the payload has been written to a temporary file and moved into place; it faults
    /// with <see cref="OperationCanceledException"/> or <see cref="PayloadGenerationException"/>.
    /// <para>在载荷写入临时文件并原位替换后完成的任务；可能以 <see cref="OperationCanceledException"/> 或
    /// <see cref="PayloadGenerationException"/> 进入故障状态。</para>
    /// </returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="outputPath"/> is empty or whitespace. <para><paramref name="outputPath"/> 为空或仅含空白字符。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// No partition was added. <para>未添加任何分区。</para>
    /// </exception>
    public Task BuildAsync(string outputPath, CancellationToken cancellationToken = default)
        => Task.Run(() => BuildFile(outputPath, cancellationToken), cancellationToken);

    private void BuildFile(string outputPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var destination = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = TempFileHelper.CreateAdjacentPath(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                BuildCore(stream, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicPublish.Publish(temporary, destination);
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
            using var data = UotanStudio.Firmware.OtaDelta.Diff.BinaryDeltaPatch.CreateTemporaryStream();
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
            UotanStudio.Firmware.OtaDelta.Diff.BinaryDeltaPatch.CopyExactly(data, output, data.Length, cancellationToken);
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
        var compressionWorkers = _compressionWorkers ?? Core.FirmwareParallelism.Workers;
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
            if (partition.Incremental)
            {
                WriteIncrementalPartition(partition, data, update, chunkSize, ref operationCount, cancellationToken);
                manifest.Partitions.Add(update);
                continue;
            }
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
        using var hashing = new UotanStudio.Firmware.Core.Streams.HashingWriteStream(data, leaveOpen: true);
        using (var source = new MemoryStream(buffer, 0, count, writable: false))
        using (var bounded = new UotanStudio.Firmware.Core.Streams.CountingWriteStream(hashing, count * 2L + 65536, leaveOpen: true, cancellationToken: cancellationToken))
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
                partition.SourceStream?.Dispose();
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

        /// <summary>When true the partition is emitted as SOURCE_COPY (unchanged blocks) + REPLACE (changed blocks).</summary>
        public bool Incremental { get; set; }

        public bool Imagediff { get; set; }
        public string? SourcePartitionName { get; set; }
        public byte[]? ZstdDictionary { get; set; }
        public string? SourcePath { get; set; }
        public Stream? SourceStream { get; set; }
    }
}

/// <summary>
/// Codec a payload builder applies to the data of each REPLACE-family operation.
/// <para>载荷构建器对每条 REPLACE 系列操作的数据所采用的编解码器。</para>
/// </summary>
public enum CompressionType
{
    /// <summary>Store block bytes unchanged inside a REPLACE operation. <para>在 REPLACE 操作中原样保存块字节。</para></summary>
    None = 0,

    /// <summary>
    /// Brotli codec, used by DAT and transfer-list pipelines; the payload format defines no Brotli REPLACE operation.
    /// <para>Brotli 编解码器，用于 DAT 与传输列表流程；payload 格式未定义 Brotli 的 REPLACE 操作。</para>
    /// </summary>
    Brotli = 1,

    /// <summary>BZip2 codec, stored in REPLACE_BZ operations. <para>BZip2 编解码器，保存于 REPLACE_BZ 操作。</para></summary>
    BZip2 = 2,

    /// <summary>XZ codec, stored in REPLACE_XZ operations. <para>XZ 编解码器，保存于 REPLACE_XZ 操作。</para></summary>
    Xz = 3,

    /// <summary>
    /// Zstandard codec, recognized when reading extended operation 14 payloads and never emitted.
    /// <para>Zstandard 编解码器，读取扩展操作 14 的载荷时被识别，但从不生成。</para>
    /// </summary>
    Zstd = 4
}