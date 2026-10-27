using System.Buffers;
using UotanStudio.Firmware.Core;
using UotanStudio.Firmware.Core.Repacking;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.HarmonyOS;

/// <summary>
/// Reads and extracts HarmonyOS <c>update.bin</c> packages: the component table is validated against its L1/L2
/// layout, every component is written under the package's own output name, and the recorded SHA-256 digest can be
/// verified while streaming. Packing is not supported because the container carries a signed metadata TLV.
/// <para>读取并解包 HarmonyOS <c>update.bin</c> 包：组件表按 L1/L2 布局校验，每个组件按包自身的输出名写出，
/// 并可在流式写出时验证已记录的 SHA-256 摘要。容器带有签名元数据 TLV，因此不支持打包。</para>
/// </summary>
internal sealed class HarmonyUpdateBinFormatHandler : IReadonlyFormatHandler, IRepackMetadataProvider
{
    private const int CopyBufferBytes = 128 * 1024;

    bool IRepackMetadataProvider.SupportsMetadataOnlyUnpack => true;

    void IRepackMetadataProvider.ConfigureRepackMetadata(RepackMetadata metadata, string sourcePath,
        string outputDirectory)
    {
        metadata.CanPackDirectory = false;
        metadata.PackingNotes =
            " The update.bin container carries a signed metadata TLV, so only extraction is supported.";
    }

    /// <summary>Display name of the HarmonyOS update.bin package family.
    /// <para>HarmonyOS update.bin 包族的显示名称。</para>
    /// </summary>
    /// <value>A fixed label shown in format lists.
    /// <para>格式列表中显示的固定名称。</para></value>
    public string FormatName => "HarmonyOS update.bin";

    /// <summary>File extensions owned by this handler.
    /// <para>本处理器负责的文件扩展名。</para>
    /// </summary>
    /// <value>The dot-less extension <c>bin</c>; whether a file is an update.bin package is confirmed by probing
    /// the header layout hint and component table, not by the extension alone.
    /// <para>不带点号的扩展名 <c>bin</c>；文件是否为 update.bin 包由头部布局提示与组件表探测确认，而非仅看扩展名。</para></value>
    public IReadOnlyList<string> FileExtensions => ["bin"];

    /// <summary>
    /// Determines whether the file is a supported HarmonyOS <c>update.bin</c> package.
    /// <para>判断文件是否为受支持的 HarmonyOS <c>update.bin</c> 包。</para>
    /// </summary>
    /// <param name="filePath">Path whose first word must carry the L1 (<c>0x11</c>) or L2 (<c>0x01</c>) layout hint
    /// and whose component table must parse with matching payload bounds.
    /// <para>路径；其首个字必须携带 L1（<c>0x11</c>）或 L2（<c>0x01</c>）布局提示，且组件表必须解析成功并与载荷边界一致。</para></param>
    /// <returns><see langword="true"/> when the header and component table are accepted; <see langword="false"/> for
    /// blank paths, missing files, short headers, unknown layout hints and any read error.
    /// <para>头部与组件表被接受时为 <see langword="true"/>；路径为空、文件缺失、头部过短、布局提示未知或读取异常时为
    /// <see langword="false"/>。</para></returns>
    public bool CanHandle(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.SequentialScan);
            return HarmonyUpdateBinReader.TryRead(stream, stream.Length, requireLayoutHint: true, out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reads the component table and metadata TLV shape without extracting any payload.
    /// <para>读取组件表与元数据 TLV 形态，不提取任何载荷。</para>
    /// </summary>
    /// <param name="filePath">Package to inspect; the file is opened read-only and never modified.
    /// <para>待检查的包；文件以只读方式打开且不会被修改。</para></param>
    /// <param name="cancellationToken">Checked before parsing and between table records; cancellation is rethrown.
    /// <para>在解析前与记录之间检查；取消会向上抛出。</para></param>
    /// <returns>An <see cref="FirmwareOpResult"/> whose entries are the component output names and whose metadata
    /// reports the layout, component and payload counts, the payload offset, the metadata TLV generation and, when
    /// present, the decoded package version; structural failures return a failed result carrying the message.
    /// <para>返回的 <see cref="FirmwareOpResult"/> 条目为组件输出名，元数据报告布局、组件数与载荷长度、载荷偏移、
    /// 元数据 TLV 形态，以及存在时解码出的包版本；结构错误返回携带信息的失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> ReadMetadataAsync(string filePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = OpenRead(filePath);
            var package = HarmonyUpdateBinReader.Read(stream, stream.Length, cancellationToken);
            var version = HarmonyUpdateBinReader.ReadPackageVersion(stream, package, cancellationToken);
            var metadata = new Dictionary<string, string>
            {
                ["Layout"] = package.Layout.ToString(),
                ["ComponentCount"] = package.Components.Count.ToString(),
                ["DataOffset"] = package.DataOffset.ToString(),
                ["PayloadBytes"] = package.PayloadLength.ToString(),
                ["SignatureTlv"] = package.SignatureKind switch
                {
                    HarmonyUpdateBinSignatureKind.Signature => "signature",
                    _ => "hash+signature"
                }
            };
            if (version is not null)
            {
                metadata["PackageVersion"] = version;
            }

            return Task.FromResult(FirmwareOpResult.Ok(
                $"HarmonyOS update.bin ({package.Layout}): {package.Components.Count} components",
                entries: [.. package.Components.Select(component => component.OutputName)],
                metadata: metadata,
                components: [.. package.Components.Select(component =>
                    FirmwareComponent.Of(component.OutputName, component.Size))]));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Task.FromResult(FirmwareOpResult.Fail(exception.Message));
        }
    }

    /// <summary>Extracts components into the output directory, applying the package's own output names.
    /// <para>按包自身的输出名把组件解包到输出目录。</para>
    /// </summary>
    /// <param name="filePath">Package to unpack; the input is opened read-only and never modified.
    /// <para>待解包的包；输入以只读方式打开且不会被修改。</para></param>
    /// <param name="outputDirectory">Directory receiving one file per component; it is created when absent.
    /// Existing files are replaced only when <see cref="UnpackOptions.Overwrite"/> permits it.
    /// <para>为每个组件接收一个文件的目录；不存在时创建。仅当 <see cref="UnpackOptions.Overwrite"/> 允许时替换已有文件。</para></param>
    /// <param name="options">Extraction policy; <see langword="null"/> uses defaults. <see cref="UnpackOptions.SelectedEntries"/>
    /// restricts extraction to exact component names or output names, <see cref="UnpackOptions.VerifyChecksums"/>
    /// requires every recorded SHA-256 digest to match, and <see cref="UnpackOptions.MaximumOutputBytes"/> caps the
    /// total declared payload. A request name that matches no component fails instead of extracting nothing.
    /// <para>解包策略；为 <see langword="null"/> 时使用默认值。<see cref="UnpackOptions.SelectedEntries"/> 按精确组件名或
    /// 输出名筛选，<see cref="UnpackOptions.VerifyChecksums"/> 要求每条已记录 SHA-256 摘要一致，
    /// <see cref="UnpackOptions.MaximumOutputBytes"/> 限制声明的载荷总量。请求的名称未匹配任何组件时直接失败，而不是静默解出空内容。</para></param>
    /// <param name="cancellationToken">Checked before and during every component copy, and honored by the async reads and writes; cancellation is rethrown.
    /// <para>在每个组件复制前后与过程中检查，并由异步读写遵守；取消会向上抛出。</para></param>
    /// <returns>An <see cref="FirmwareOpResult"/> reporting the extracted component output names, or a failed result
    /// for an invalid structure, a missing digest, a digest mismatch or a bounded write failure.
    /// <para>返回报告已解出组件输出名的 <see cref="FirmwareOpResult"/>；结构无效、缺少摘要、摘要不符或有界写入失败时
    /// 返回失败结果。</para></returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was signalled.
    /// <para><paramref name="cancellationToken"/> 被触发。</para></exception>
    public Task<FirmwareOpResult> UnpackAsync(string filePath, string outputDirectory,
        UnpackOptions? options = null, CancellationToken cancellationToken = default)
        => RepackMetadata.UnpackAsync(this, filePath, outputDirectory, options, cancellationToken,
            () => UnpackComponentsAsync(filePath, outputDirectory, options, cancellationToken));

    /// <summary>
    /// Extracts components without publishing repack metadata, for container workflows that own the output
    /// directory and must not leave an extra metadata file among the extracted images.
    /// <para>不发布重打包元数据地解出组件，供自行管理输出目录、且不得在解出镜像中留下额外元数据文件的容器流程使用。</para>
    /// </summary>
    /// <param name="filePath">Package to unpack; the input is opened read-only.
    /// <para>待解包的包；输入以只读方式打开。</para></param>
    /// <param name="outputDirectory">Directory receiving the selected components.
    /// <para>接收选中组件的目录。</para></param>
    /// <param name="options">Extraction policy; <see langword="null"/> uses defaults.
    /// <para>解包策略；为 <see langword="null"/> 时使用默认值。</para></param>
    /// <param name="cancellationToken">Checked before and during each component copy.
    /// <para>在每个组件复制前后与过程中检查。</para></param>
    /// <returns>The extraction outcome without repack-metadata publication.
    /// <para>不发布重打包元数据的解包结果。</para></returns>
    internal async Task<FirmwareOpResult> UnpackComponentsAsync(string filePath, string outputDirectory,
        UnpackOptions? options, CancellationToken cancellationToken)
    {
        options ??= new UnpackOptions();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferBytes, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var package = HarmonyUpdateBinReader.Read(input, input.Length, cancellationToken);
            var selected = options.CreateSelectedEntrySet();
            HarmonyUpdateBinComponent[] components = selected is null
                ? [.. package.Components]
                : SelectComponents(package.Components, selected);
            Directory.CreateDirectory(outputDirectory);
            var targets = components
                .Select(component => (Component: component,
                    Path: FileExtraction.ResolvePath(outputDirectory, component.OutputName)))
                .ToArray();
            if (!options.Overwrite)
            {
                foreach (var (_, path) in targets)
                {
                    if (File.Exists(path))
                    {
                        throw new IOException($"Extraction output already exists: {path}");
                    }
                }
            }

            var total = components.Sum(component => component.Size);
            if (total > options.MaximumOutputBytes)
            {
                throw new InvalidDataException(
                    $"The HarmonyOS update.bin declares {total} payload bytes, above the {options.MaximumOutputBytes}-byte output budget.");
            }

            if (options.VerifyChecksums)
            {
                foreach (var component in components)
                {
                    if (component.Sha256.All(static value => value == 0))
                    {
                        throw new InvalidDataException(
                            $"The HarmonyOS update.bin component '{component.Name}' has no recorded SHA-256 digest.");
                    }
                }
            }

            var copied = 0L;
            options.Progress?.Report(0, $"Extracting {components.Length} components");
            foreach (var (component, path) in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                input.Position = component.DataOffset;
                await FileExtraction.WriteAtomicAsync(path,
                    output => CopyComponentAsync(input, output, component, options.VerifyChecksums,
                        cancellationToken),
                    options.Overwrite, cancellationToken).ConfigureAwait(false);
                copied += component.Size;
                options.Progress?.Report(total == 0 ? 100 : copied * 100.0 / total,
                    $"Extracted {component.OutputName}");
            }

            return FirmwareOpResult.Ok(
                $"Extracted {components.Length} components to {outputDirectory}",
                entries: [.. components.Select(component => component.OutputName)],
                metadata: new Dictionary<string, string>
                {
                    ["Layout"] = package.Layout.ToString(),
                    ["PayloadBytes"] = total.ToString(),
                    ["ChecksumsVerified"] = options.VerifyChecksums.ToString()
                },
                components: [.. components.Select(component =>
                    FirmwareComponent.Of(component.OutputName, component.Size))]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return FirmwareOpResult.Fail(exception.Message);
        }
    }

    private static FileStream OpenRead(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("The HarmonyOS update.bin path is empty.", nameof(filePath));
        }

        return new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes,
            FileOptions.RandomAccess);
    }

    private static HarmonyUpdateBinComponent[] SelectComponents(
        IReadOnlyList<HarmonyUpdateBinComponent> components, HashSet<string> selected)
    {
        var missing = selected
            .Where(name => !components.Any(component =>
                component.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || component.OutputName.Equals(name, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException(
                "The HarmonyOS update.bin does not contain the requested component(s): "
                + string.Join(", ", missing) + ".");
        }

        return [.. components.Where(component =>
            selected.Contains(component.Name) || selected.Contains(component.OutputName))];
    }

    private static async Task CopyComponentAsync(FileStream input, FileStream output,
        HarmonyUpdateBinComponent component, bool verifyChecksum, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            using var digest = verifyChecksum ? Sha256.CreateAccumulator() : null;
            var remaining = component.Size;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        $"The HarmonyOS update.bin component '{component.Name}' ended before its declared {component.Size} bytes.");
                }

                digest?.Append(buffer.AsSpan(0, read));
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }

            if (digest is not null)
            {
                var actual = digest.GetHashAndReset();
                if (!actual.AsSpan().SequenceEqual(component.Sha256))
                {
                    throw new InvalidDataException(
                        $"The HarmonyOS update.bin component '{component.Name}' does not match its recorded SHA-256 digest.");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}