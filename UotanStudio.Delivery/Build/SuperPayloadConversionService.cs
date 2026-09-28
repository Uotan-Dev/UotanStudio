using System.Text;
using UotanStudio.Firmware.BlockImage.Lp;
using UotanStudio.Firmware.OtaDelta.Payload;
using UotanStudio.Firmware.OtaDelta.Payload.Builder;

namespace UotanStudio.Services.Delivery;

/// <summary>
/// Options for converting a Super image into a full update_engine Payload.
/// <para>将 Super 镜像转换为完整 update_engine Payload 的选项。</para>
/// </summary>
public sealed record SuperPayloadConversionOptions
{
    /// <summary>
    /// Payload block size in bytes, which every extracted Super partition must be a multiple of.
    /// <para>Payload 块大小（字节），每个从 Super 解出的分区大小都必须是它的整数倍。</para>
    /// </summary>
    /// <value>Defaults to <c>4096</c>; accepted range is a power of two from 512 through 1048576.
    /// <para>默认为 <c>4096</c>；可接受的范围是 512 到 1048576 之间的 2 的幂。</para></value>
    /// <returns>The value forwarded to the Payload builder.
    /// <para>转发给 Payload 构建器的值。</para></returns>
    public uint BlockSize { get; init; } = 4096;

    /// <summary>
    /// Codec applied to Payload install operations, matched case-insensitively against the builder's accepted
    /// names.
    /// <para>应用于 Payload 安装操作的压缩算法，会与构建器接受的模式名按忽略大小写匹配。</para>
    /// </summary>
    /// <value><see cref="UotanStudio.Firmware.OtaDelta.Payload.Builder.CompressionType.None"/>
    /// by default; only <c>None</c>, <c>BZip2</c> and <c>Xz</c> pass the downstream plan validation.
    /// <para>默认为
    /// <see cref="UotanStudio.Firmware.OtaDelta.Payload.Builder.CompressionType.None"/>；
    /// 只有 <c>None</c>、<c>BZip2</c> 和 <c>Xz</c> 能通过下游的计划校验。</para></value>
    /// <returns>Converted to its name string for the delivery plan.
    /// <para>转换为名称字符串后写入交付计划。</para></returns>
    public CompressionType Compression { get; init; } = CompressionType.None;

    /// <summary>
    /// Upper bound, in seconds since the Unix epoch, written as the maximum timestamp of the generated
    /// Payload manifest.
    /// <para>以 Unix 纪元起的秒数表示，写入所生成 Payload 清单的最大时间戳。</para>
    /// </summary>
    /// <value><c>0</c> by default; negative values are rejected by plan validation.
    /// <para>默认为 <c>0</c>；负值会被计划校验拒绝。</para></value>
    /// <returns>The value forwarded to the Payload builder.
    /// <para>转发给 Payload 构建器的值。</para></returns>
    public long MaxTimestamp { get; init; }

    /// <summary>
    /// Android security patch level stamped into the Payload properties.
    /// <para>写入 Payload 属性中的 Android 安全补丁级别。</para>
    /// </summary>
    /// <value>An empty string omits it; otherwise the exact <c>yyyy-MM-dd</c> form is required.
    /// <para>空字符串表示省略；否则必须使用严格的 <c>yyyy-MM-dd</c> 格式。</para></value>
    /// <returns>The date text forwarded to the Payload builder.
    /// <para>转发给 Payload 构建器的日期文本。</para></returns>
    public string SecurityPatchLevel { get; init; } = string.Empty;

    /// <summary>
    /// Path identifying the RSA private key used to sign the Payload manifest. The public conversion entry
    /// point supplies no key provider, so a non-empty value here cannot resolve to a key.
    /// <para>标识用于签名 Payload 清单的 RSA 私钥路径。公开的转换入口不提供密钥供应器，因此此处的非空值无法解析为密钥。</para>
    /// </summary>
    /// <value>An empty string, the default, produces an unsigned full Payload.
    /// <para>默认为空字符串，产生未签名的完整 Payload。</para></value>
    /// <returns>The key reference passed through the delivery plan.
    /// <para>随交付计划传递的密钥引用。</para></returns>
    public string SigningKeyPath { get; init; } = string.Empty;

    /// <summary>
    /// Expected key fingerprint, compared against the <c>SHA256:</c> digest of the loaded public key so a
    /// replaced key is rejected instead of signing with the wrong material.
    /// <para>期望的密钥指纹，与已加载公钥的 <c>SHA256:</c> 摘要比较，从而拒绝对已被替换的密钥继续签名。</para>
    /// </summary>
    /// <value>An empty string skips the comparison; the value only matters together with
    /// <see cref="SigningKeyPath"/>.
    /// <para>空字符串跳过比较；该值仅在配合 <see cref="SigningKeyPath"/> 时才有意义。</para></value>
    /// <returns>The fingerprint text forwarded to the Payload builder.
    /// <para>转发给 Payload 构建器的指纹文本。</para></returns>
    public string SigningKeyFingerprint { get; init; } = string.Empty;
}

/// <summary>
/// Describes the atomically published output of a Super-to-Payload conversion.
/// <para>描述 Super 转 Payload 转换原子发布后的产物。</para>
/// </summary>
/// <param name="PayloadPath">
/// Absolute path the staged Payload was moved to, typically identical to the requested output path.
/// <para>暂存 Payload 被移动到的绝对路径，通常与请求的输出路径相同。</para>
/// </param>
/// <param name="PropertiesPath">
/// Absolute path of the companion <c>payload_properties.txt</c>, which is always written into the Payload's
/// own directory under that fixed name.
/// <para>配套 <c>payload_properties.txt</c> 的绝对路径，它总是以该固定名称写入 Payload 自身所在目录。</para>
/// </param>
/// <param name="Partitions">
/// Names of the non-empty Super partitions placed into the Payload, in LP metadata order.
/// <para>放入 Payload 的非空 Super 分区名，按 LP 元数据顺序排列。</para>
/// </param>
/// <param name="VirtualAb">
/// <see langword="true"/> when the source Super declares the virtual-A/B header flag, mirrored into the
/// generated dynamic-partition configuration.
/// <para>源 Super 声明了 virtual-A/B 头部标志位时为 <see langword="true"/>，并镜像到生成的动态分区配置中。</para>
/// </param>
public sealed record SuperPayloadConversionResult(
    string PayloadPath,
    string PropertiesPath,
    IReadOnlyList<string> Partitions,
    bool VirtualAb);

/// <summary>
/// Transactional Super-to-Payload conversion used by desktop and headless hosts. The Super image is opened
/// read-only, its logical partitions are extracted into a GUID-suffixed staging directory, and both the
/// Payload and its properties file are published only after the generated manifest is re-parsed and shown to
/// contain every extracted partition; publication failures restore the previous files.
/// <para>供桌面宿主与无头宿主使用的 transactional Super 转 Payload 流程。Super 镜像以只读方式打开，其逻辑分区被
/// 解出到带 GUID 后缀的暂存目录，只有在重新解析生成的清单并确认包含每个解出的分区之后，才发布 Payload 与其属性
/// 文件；发布失败时会恢复此前的文件。</para>
/// </summary>
public static class SuperPayloadConversionService
{
    /// <summary>
    /// Extracts every non-empty logical partition from a Super image and builds a full update_engine Payload
    /// plus its <c>payload_properties.txt</c> next to the requested output path.
    /// <para>从 Super 镜像中提取所有非空逻辑分区，并在请求的输出路径旁构建完整的 update_engine Payload
    /// 及其 <c>payload_properties.txt</c>。</para>
    /// </summary>
    /// <param name="superPath">
    /// Source LP Super image; opened read-only and never modified.
    /// <para>源 LP Super 镜像；以只读方式打开，绝不被修改。</para>
    /// </param>
    /// <param name="payloadPath">
    /// Destination Payload. Its directory is created when missing, and the fixed-name properties file is
    /// published into the same directory. The path may not be the source Super image nor the properties file.
    /// <para>目标 Payload。其目录不存在时会被创建，固定名称的属性文件会发布到同一目录。该路径既不能是源 Super
    /// 镜像，也不能是属性文件。</para>
    /// </param>
    /// <param name="options">
    /// Block size, compression, timestamp, patch level and signing references; <see langword="null"/> uses the
    /// default 4096-byte uncompressed full Payload.
    /// <para>块大小、压缩、时间戳、补丁级别与签名引用；为 <see langword="null"/> 时使用默认的 4096 字节不压缩完整 Payload。</para>
    /// </param>
    /// <param name="progress">
    /// Receives human-readable progress lines formatted as <c>percent% message</c>; may be
    /// <see langword="null"/>. Reports are raised on the thread the builder uses, so UI hosts must marshal.
    /// <para>接收格式为 <c>percent% message</c> 的人类可读进度行；可以为 <see langword="null"/>。
    /// 回调在构建器所用线程上触发，因此 UI 宿主需要自行切换调度。</para>
    /// </param>
    /// <param name="cancellationToken">
    /// Cancels extraction, hashing and publishing; a cancelled run leaves the previous Payload and properties
    /// files in place and removes the staging directory.
    /// <para>取消解包、哈希与发布操作；被取消的运行会保留原有的 Payload 与属性文件，并删除暂存目录。</para>
    /// </param>
    /// <returns>
    /// The published Payload path, the properties path, the partition names carried by the Payload, and the
    /// virtual-A/B state read from the Super header.
    /// <para>已发布的 Payload 路径、属性文件路径、Payload 承载的分区名，以及从 Super 头部读到的 virtual-A/B 状态。</para>
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="superPath"/> or <paramref name="payloadPath"/> is
    /// <see langword="null"/> or blank.
    /// <para><paramref name="superPath"/> 或 <paramref name="payloadPath"/> 为 <see langword="null"/> 或空白。</para>
    /// </exception>
    /// <exception cref="FileNotFoundException"><paramref name="superPath"/> does not exist, or
    /// <see cref="SuperPayloadConversionOptions.SigningKeyPath"/> names a key that cannot be read.
    /// <para><paramref name="superPath"/> 不存在，或 <see cref="SuperPayloadConversionOptions.SigningKeyPath"/>
    /// 指向无法读取的密钥。</para></exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="options"/> block size is not a power of
    /// two between 512 and 1048576. <para><paramref name="options"/> 的块大小不是 512 到 1048576 之间的 2 的幂。</para>
    /// </exception>
    /// <exception cref="InvalidOperationException">The output collides with the source Super image or with
    /// <c>payload_properties.txt</c>, or <see cref="SuperPayloadConversionOptions.SigningKeyPath"/> is set
    /// because this overload provides no signing-key provider.
    /// <para>输出与源 Super 镜像或 <c>payload_properties.txt</c> 冲突，或设置了
    /// <see cref="SuperPayloadConversionOptions.SigningKeyPath"/>（此重载不提供签名密钥供应器）。</para>
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// Super extraction fails, the Super holds no non-empty partition, an extracted partition is not aligned to
    /// the block size, the dynamic-partition groups are empty or unnamed, the builder omits its properties
    /// file, the re-parsed manifest lacks an extracted partition, or the compression, timestamp or patch level
    /// is rejected by plan validation.
    /// <para>Super 解包失败、Super 中没有非空分区、解出的分区未按块大小对齐、动态分区组为空或未命名、
    /// 构建器未生成属性文件、重新解析的清单缺少某个解出的分区，或压缩方式、时间戳、补丁级别被计划校验拒绝。</para>
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signaled.
    /// <para><paramref name="cancellationToken"/> 已收到取消信号。</para></exception>
    /// <example>
    /// <code>
    /// var built = await SuperPayloadConversionService.ConvertAsync(
    ///     "super.img",
    ///     "out/payload.bin",
    ///     new SuperPayloadConversionOptions { Compression = CompressionType.Xz });
    /// </code>
    /// </example>
    public static Task<SuperPayloadConversionResult> ConvertAsync(
        string superPath,
        string payloadPath,
        SuperPayloadConversionOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
        => ConvertAsync(superPath, payloadPath, options,
            progress is null ? null : new TextProgressAdapter(progress), cancellationToken, signingKeys: null);

    internal static async Task<SuperPayloadConversionResult> ConvertAsync(
        string superPath,
        string payloadPath,
        SuperPayloadConversionOptions? options,
        IProgress<DeliveryProgress>? progress,
        CancellationToken cancellationToken,
        IDeliverySigningKeyProvider? signingKeys)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(superPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadPath);
        options ??= new SuperPayloadConversionOptions();
        if (!File.Exists(superPath))
        {
            throw new FileNotFoundException("Super image was not found.", superPath);
        }
        if (options.BlockSize is < 512 or > 1024 * 1024 || (options.BlockSize & (options.BlockSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Payload block size must be a power of two from 512 to 1048576.");
        }

        var output = Path.GetFullPath(payloadPath);
        var outputDirectory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(outputDirectory);
        var properties = Path.Combine(outputDirectory, "payload_properties.txt");
        var source = Path.GetFullPath(superPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(output, properties, comparison))
        {
            throw new InvalidOperationException("The Payload output path cannot be payload_properties.txt.");
        }
        if (string.Equals(source, output, comparison) || string.Equals(source, properties, comparison))
        {
            throw new InvalidOperationException("Super-to-Payload conversion cannot replace its source Super image.");
        }
        var transactionId = Guid.NewGuid().ToString("N");
        var transaction = Path.Combine(outputDirectory, $".super-payload-{transactionId}.staging");
        var images = Path.Combine(transaction, "images");
        var stagedPayload = Path.Combine(transaction, "payload.bin");
        var stagedProperties = Path.Combine(transaction, "payload_properties.txt");
        var backup = Path.Combine(outputDirectory, $".super-payload-{transactionId}.backup");
        Directory.CreateDirectory(images);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            LpMetadata metadata;
            await using (var super = MetadataReader.OpenImage(superPath))
            {
                metadata = MetadataReader.ReadFromImageStream(super);
            }

            var unpack = await new LpFormatHandler().UnpackAsync(superPath, images,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!unpack.Success)
            {
                throw new InvalidDataException("Super extraction failed: " + unpack.Summary);
            }

            var selected = metadata.Partitions
                .Select(partition => new
                {
                    Metadata = partition,
                    Name = partition.NameString,
                    Path = Path.Combine(images, partition.NameString + ".img")
                })
                .Where(item => File.Exists(item.Path) && new FileInfo(item.Path).Length > 0)
                .ToArray();
            if (selected.Length == 0)
            {
                throw new InvalidDataException("Super contains no non-empty partitions that can be placed in a Payload.");
            }
            if (selected.Any(item => new FileInfo(item.Path).Length % options.BlockSize != 0))
            {
                throw new InvalidDataException("Every extracted Super partition must be aligned to the Payload block size.");
            }

            var dynamicConfig = Path.Combine(transaction, "dynamic_partitions_info.txt");
            await File.WriteAllTextAsync(dynamicConfig, BuildDynamicConfiguration(metadata, selected.Select(item => item.Metadata).ToArray()),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);

            var plan = new DeliveryPlan
            {
                PackPayload = true,
                ContainerConfirmed = true,
                Payload = new DeliveryPayloadOptions
                {
                    BlockSize = options.BlockSize,
                    Compression = options.Compression.ToString(),
                    DynamicPartitionsFile = dynamicConfig,
                    MaxTimestamp = options.MaxTimestamp,
                    SecurityPatchLevel = options.SecurityPatchLevel,
                    SigningKeyPath = options.SigningKeyPath,
                    SigningKeyFingerprint = options.SigningKeyFingerprint
                },
                Container = selected.Select(item => new DeliveryFile
                {
                    Partition = item.Name,
                    FileName = item.Name + ".img",
                    SourcePath = item.Path,
                    SourceLength = new FileInfo(item.Path).Length,
                    IncludeInSuper = true
                }).ToList()
            };
            await DeliveryPayloadBuilder.BuildAsync(plan, stagedPayload, progress, cancellationToken, signingKeys)
                .ConfigureAwait(false);
            if (!File.Exists(stagedProperties))
            {
                throw new InvalidDataException("Payload builder did not produce payload_properties.txt.");
            }

            await using (var payload = File.OpenRead(stagedPayload))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var parsed = PayloadParser.GetPayloadMetadata(payload);
                var names = parsed.Manifest.Partitions.Select(partition => partition.PartitionName).ToHashSet(StringComparer.Ordinal);
                if (selected.Any(item => !names.Contains(item.Name)))
                {
                    throw new InvalidDataException("The generated Payload is missing an extracted Super partition.");
                }
            }

            PublishPair(stagedPayload, output, stagedProperties, properties, backup, cancellationToken);
            return new(output, properties, selected.Select(item => item.Name).ToArray(), (metadata.Header.Flags & 1) != 0);
        }
        finally
        {
            TryDeleteDirectory(transaction);
            TryDeleteEmptyDirectory(backup);
        }
    }

    private static string BuildDynamicConfiguration(LpMetadata metadata, IReadOnlyCollection<LpMetadataPartition> selected)
    {
        var selectedNames = selected.Select(partition => partition.NameString).ToHashSet(StringComparer.Ordinal);
        var groups = metadata.Groups.Select((group, index) => new
        {
            Group = group,
            Index = index,
            Partitions = selected.Where(partition => partition.GroupIndex == index).Select(partition => partition.NameString).ToArray()
        }).Where(item => item.Partitions.Length > 0).ToArray();
        if (groups.Length == 0 || selectedNames.Count == 0)
        {
            throw new InvalidDataException("Super dynamic partition groups are empty or inconsistent.");
        }

        var text = new StringBuilder();
        text.Append("super_partition_groups=").AppendJoin(' ', groups.Select(item => item.Group.NameString)).Append('\n');
        foreach (var item in groups)
        {
            if (string.IsNullOrWhiteSpace(item.Group.NameString))
            {
                throw new InvalidDataException("Super contains an unnamed dynamic partition group.");
            }
            ulong used = 0;
            foreach (var partition in selected.Where(partition => partition.GroupIndex == item.Index))
            {
                foreach (var extent in metadata.Extents.GetRange(checked((int)partition.FirstExtentIndex), checked((int)partition.NumExtents)))
                {
                    used = checked(used + extent.NumSectors * 512);
                }
            }
            var size = Math.Max(item.Group.MaximumSize, Math.Max(used, metadata.Geometry.LogicalBlockSize));
            text.Append("super_").Append(item.Group.NameString).Append("_group_size=").Append(size).Append('\n');
            text.Append("super_").Append(item.Group.NameString).Append("_partition_list=")
                .AppendJoin(' ', item.Partitions).Append('\n');
        }
        text.Append("virtual_ab=").Append(((metadata.Header.Flags & 1) != 0).ToString().ToLowerInvariant()).Append('\n');
        return text.ToString();
    }

    private static void PublishPair(string stagedPayload, string payload, string stagedProperties, string properties,
        string backupDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        var pairs = new[] { (Staged: stagedPayload, Target: payload), (Staged: stagedProperties, Target: properties) };
        var published = new List<(string Target, string? Backup)>();
        try
        {
            foreach (var pair in pairs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? saved = null;
                if (File.Exists(pair.Target))
                {
                    saved = Path.Combine(backupDirectory, Path.GetFileName(pair.Target));
                    File.Move(pair.Target, saved, overwrite: false);
                }
                try
                {
                    File.Move(pair.Staged, pair.Target, overwrite: false);
                }
                catch
                {
                    if (saved is not null)
                    {
                        File.Move(saved, pair.Target, overwrite: false);
                    }
                    throw;
                }
                published.Add((pair.Target, saved));
            }
            TryDeleteDirectory(backupDirectory);
        }
        catch (Exception publicationError)
        {
            List<Exception>? rollbackErrors = null;
            for (var i = published.Count - 1; i >= 0; i--)
            {
                try
                {
                    File.Delete(published[i].Target);
                    if (published[i].Backup is { } saved)
                    {
                        File.Move(saved, published[i].Target, overwrite: false);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors ??= [];
                    rollbackErrors.Add(rollbackError);
                }
            }
            if (rollbackErrors is not null)
            {
                rollbackErrors.Insert(0, publicationError);
                throw new AggregateException("Payload publication failed and rollback was incomplete.", rollbackErrors);
            }
            throw;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteEmptyDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed class TextProgressAdapter(IProgress<string> progress) : IProgress<DeliveryProgress>
    {
        public void Report(DeliveryProgress value) => progress.Report($"{value.Percent:0}% {value.Message}");
    }
}