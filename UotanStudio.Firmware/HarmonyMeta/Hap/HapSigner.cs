using System.Buffers.Binary;
using System.Runtime.InteropServices;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Firmware.Core.IO;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Options controlling one HAP signing run.
/// <para>控制一次 HAP 签名的选项。</para>
/// </summary>
public sealed record HapSigningOptions
{
    /// <summary>Gets the minimum compatible SDK version carried by the HAP; values below 8 select scheme version 2,
    /// values from 8 select scheme version 3. <para>HAP 携带的最小兼容 SDK 版本；低于 8 走方案版本 2，
    /// 自 8 起走方案版本 3。</para></summary>
    public uint CompatibleSdkVersion { get; init; } = 9;

    /// <summary>Gets the digest/signature algorithm. <para>摘要与签名算法。</para></summary>
    public HapDigestAlgorithm Algorithm { get; init; } = HapDigestAlgorithm.EcdsaSha256;

    /// <summary>Gets the optional build property block value; when supplied it is emitted first and folded into the
    /// content digest. <para>可选的构建属性块值；提供时最先输出并计入内容摘要。</para></summary>
    public byte[]? PropertyBlock { get; init; }

    /// <summary>Gets a value indicating whether an fs-verity code-sign property block is generated and emitted.
    /// When enabled the signer builds the block from the aligned container via <see cref="HapCodeSigner"/> and folds
    /// it into the content digest, producing the property + profile + signature block set the official tool emits
    /// with <c>-signCode 1</c>. <para>是否生成并输出 fs-verity code-sign property 块。启用时签名器经
    /// <see cref="HapCodeSigner"/> 从对齐后的容器构建该块并折入内容摘要，产出与官方工具 <c>-signCode 1</c>
    /// 一致的 property + profile + signature 块集。</para></summary>
    /// <remarks>Disabled by default so existing callers keep the two-block signature-only output.
    /// <para>默认关闭，既有调用方继续得到只含两块的无 code-sign 输出。</para></remarks>
    public bool IncludeCodeSign { get; init; }

    /// <summary>Gets a value indicating whether the permission-signing sub-block is appended to the code-sign
    /// property value when a <c>module.json</c> is present. Only read when <see cref="IncludeCodeSign"/> is set.
    /// <para>当存在 <c>module.json</c> 时是否向 code-sign property 值追加权限签名子块。仅在
    /// <see cref="IncludeCodeSign"/> 启用时读取。</para></summary>
    public bool IncludePermissionBlock { get; init; } = true;

    /// <summary>Gets a value indicating whether an existing signing block is replaced; the default replaces it.
    /// <para>是否替换既有签名块；默认替换。</para></summary>
    public bool ReplaceExistingBlock { get; init; } = true;
}

/// <summary>
/// Result of one HAP signing run. <para>一次 HAP 签名的结果。</para></summary>
/// <param name="OutputPath">Path of the signed package. <para>已签包路径。</para></param>
/// <param name="SchemeVersion">Signing scheme version emitted (2 or 3). <para>输出的签名方案版本（2 或 3）。</para></param>
/// <param name="BlockIds">Block type IDs emitted in order. <para>按序输出的块类型 ID。</para></param>
public sealed record HapSigningResult(string OutputPath, uint SchemeVersion, IReadOnlyList<uint> BlockIds);

/// <summary>
/// Signs a HAP/HSP container with the OpenHarmony application signature scheme: the archive is aligned per the
/// official policy, the content digest is computed over the entries, central directory and a pre-block EOCD, the
/// digest message is signed as attached CMS data, and the signing block is inserted before the central directory.
/// <para>以 OpenHarmony 应用签名方案签署 HAP/HSP 容器：档案按官方策略对齐，内容摘要跨条目、中央目录与
/// 前置块 EOCD 计算，摘要消息以 attached CMS 签名，签名块插入中央目录之前。</para>
/// </summary>
/// <remarks>
/// The input file is never modified; output is staged next to the destination and atomically published. The
/// signature covers the digest message only — the digest itself already folds in the profile and property values.
/// <para>输入文件从不修改；输出先落临时文件再原子发布。签名只覆盖摘要消息——摘要本身已折入 Profile 与
/// property 值。</para>
/// </remarks>
public static class HapSigner
{
    /// <summary>
    /// Signs the container at <paramref name="inputPath"/> into <paramref name="outputPath"/>.
    /// <para>把 <paramref name="inputPath"/> 的容器签署到 <paramref name="outputPath"/>。</para>
    /// </summary>
    /// <param name="inputPath">Unsigned (or previously signed) container path. <para>未签（或已签）容器路径。</para></param>
    /// <param name="outputPath">Destination path, never equal to the input. <para>目标路径，不得等于输入。</para></param>
    /// <param name="material">Resolved signing material. <para>已解析的签名材料。</para></param>
    /// <param name="options">Signing options. <para>签名选项。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The signing result. <para>签名结果。</para></returns>
    /// <exception cref="ArgumentException">The paths coincide. <para>两路径相同。</para></exception>
    /// <exception cref="InvalidDataException">The container is not a supported ZIP32 HAP.
    /// <para>容器不是受支持的 ZIP32 HAP。</para></exception>
    public static HapSigningResult Sign(string inputPath, string outputPath, HapSignerMaterial material,
        HapSigningOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (string.Equals(inputPath, outputPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new ArgumentException("Output path must differ from the input path. 输出路径不得与输入路径相同。", nameof(outputPath));
        }

        var resolvedOptions = options ?? new HapSigningOptions();
        var staged = TempFileHelper.CreateAdjacentPath(outputPath);
        try
        {
            var schemeVersion = SignCore(inputPath, staged, material, resolvedOptions, cancellationToken);
            AtomicPublish.Publish(staged, outputPath);
            staged = null!;
            var blockIds = new List<uint>();
            if (resolvedOptions.PropertyBlock is not null || resolvedOptions.IncludeCodeSign)
            {
                blockIds.Add(HapSignatureConstants.BlockIdProperty);
            }

            blockIds.Add(HapSignatureConstants.BlockIdProfile);
            blockIds.Add(HapSignatureConstants.BlockIdSignature);
            return new HapSigningResult(outputPath, schemeVersion, blockIds);
        }
        finally
        {
            if (staged is not null)
            {
                TempFileHelper.SafeDelete(staged);
            }
        }
    }

    private static uint SignCore(string inputPath, string stagedPath, HapSignerMaterial material,
        HapSigningOptions options, CancellationToken cancellationToken)
    {
        using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var output = new FileStream(stagedPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);

        var sections = ApkZipSections.Locate(input);
        long entriesEnd = sections.CentralDirectoryOffset;
        if (options.ReplaceExistingBlock)
        {
            var existing = HapSigningBlock.Find(input, sections.CentralDirectoryOffset);
            if (existing is not null)
            {
                entriesEnd = existing.Value.Start;
            }
        }

        var entries = HapZipArchive.Read(input, sections.CentralDirectoryOffset, sections.CentralDirectorySize);
        HapZipEntry.ApplyAlignment(entries);

        var entriesLength = 0L;
        foreach (var entry in entries)
        {
            checked
            {
                entriesLength += HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length + entry.Data.Length;
            }
        }

        // The code-sign payload digests the aligned container and must be built before the content digest is
        // sealed, because its value is folded into that digest as the leading optional block value.
        byte[]? propertyValue = options.PropertyBlock;
        if (options.IncludeCodeSign)
        {
            propertyValue = HapCodeSigner.BuildPropertyBlock(inputPath, material, new HapCodeSignerOptions
            {
                Algorithm = options.Algorithm,
                FinalBlockCount = options.PropertyBlock is { Length: > 0 } ? 3 : 2,
                IncludePermissionBlock = options.IncludePermissionBlock,
            }, cancellationToken);
        }

        var directoryBytes = HapZipArchive.BuildCentralDirectory(entries);
        var digestEocd = HapZipArchive.BuildEocd(entriesLength, directoryBytes.Length);

        Span<int> digestSections = [checked((int)entriesLength), 0, directoryBytes.Length, digestEocd.Length];
        var digest = HapContentDigestComputer.Create(digestSections, options.Algorithm);

        digest.BeginSection();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteAndDigest(output, digest, BuildLocalHeader(entry));
            WriteAndDigest(output, digest, entry.Data);
        }

        digest.EndSection();
        digest.BeginSection();
        digest.EndSection();
        // The digest feeds the central directory and EOCD bytes without writing them: they are emitted after the
        // signing block, which is assembled only once the digest is signed.
        digest.UpdateSection(directoryBytes);
        digest.UpdateSection(digestEocd);

        var profileValue = material.Profile;
        if (propertyValue is { Length: > 0 } property)
        {
            digest.UpdateOptionalBlock(property);
        }

        digest.UpdateOptionalBlock(profileValue);

        var contentDigest = digest.Finalize();
        var digestMessage = HapContentDigestComputer.BuildDigestMessage(options.Algorithm, contentDigest);
        var signature = HapCmsSigner.Sign(digestMessage, material.SigningCertificate, material.Chain);

        var blocks = new List<HapBlockEntry>(3);
        if (propertyValue is { Length: > 0 })
        {
            blocks.Add(new HapBlockEntry(HapSignatureConstants.BlockIdProperty, propertyValue));
        }

        blocks.Add(new HapBlockEntry(HapSignatureConstants.BlockIdProfile, profileValue));
        blocks.Add(new HapBlockEntry(HapSignatureConstants.BlockIdSignature, signature));
        var signingBlock = HapSigningBlock.Build(CollectionsMarshal.AsSpan(blocks), options.CompatibleSdkVersion);

        var finalEocd = HapZipArchive.BuildEocd(entriesLength + signingBlock.Length, directoryBytes.Length);
        output.Write(signingBlock);
        output.Write(directoryBytes);
        output.Write(finalEocd);
        return options.CompatibleSdkVersion >= HapSignatureConstants.SchemeVersionBoundary
            ? HapSignatureConstants.SchemeVersionV3
            : HapSignatureConstants.SchemeVersionV2;
    }

    private static byte[] BuildLocalHeader(HapZipEntry entry)
    {
        var header = new byte[HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x04034b50);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), entry.VersionNeeded);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), entry.Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), entry.Method);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), entry.DosTime);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(12), entry.DosDate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(14), entry.Crc);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(18), entry.CompressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(22), entry.UncompressedSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26), (ushort)entry.Name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28), (ushort)entry.LocalExtra.Length);
        entry.Name.CopyTo(header, HapZipEntry.LocalHeaderFixed);
        entry.LocalExtra.CopyTo(header, HapZipEntry.LocalHeaderFixed + entry.Name.Length);
        return header;
    }

    private static void WriteAndDigest(FileStream output, HapContentDigestComputer digest, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        output.Write(bytes);
        digest.UpdateSectionPiece(bytes);
    }
}