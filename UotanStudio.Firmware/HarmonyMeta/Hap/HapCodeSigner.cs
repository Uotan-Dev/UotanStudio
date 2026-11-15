using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using UotanStudio.Compress;
using UotanStudio.Firmware.AndroidMeta.Apk.Signing;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.HarmonyMeta.Hap;

/// <summary>
/// Options controlling one HAP fs-verity code-sign property block build.
/// <para>控制一次 HAP fs-verity code-sign property 块构建的选项。</para>
/// </summary>
public sealed record HapCodeSignerOptions
{
    /// <summary>Gets the digest/signature algorithm used for every code-sign CMS and the permission signature.
    /// <para>全部 code-sign CMS 与权限签名所用的摘要/签名算法。</para></summary>
    public HapDigestAlgorithm Algorithm { get; init; } = HapDigestAlgorithm.EcdsaSha256;

    /// <summary>Gets the number of TLV entries the final signing block will carry, counting the property block and
    /// the main signature block; the official tool places the code-sign payload at
    /// <c>entriesEnd + 12 × count + 12</c>. <para>最终签名块将携带的 TLV 条目数（含 property 块与主签名块）；
    /// 官方工具把 code-sign 载荷放在 <c>entriesEnd + 12 × count + 12</c> 处。</para></summary>
    public int FinalBlockCount { get; init; } = 3;

    /// <summary>Gets a value indicating whether the <c>0x30000002</c> permission-signing sub-block is appended to the
    /// property value when <c>module.json</c> exists, matching <c>-permSign enable</c> of the official tool.
    /// <para>当 <c>module.json</c> 存在时是否向 property 值追加 <c>0x30000002</c> 权限签名子块，等价于官方工具的
    /// <c>-permSign enable</c>。</para></summary>
    public bool IncludePermissionBlock { get; init; } = true;
}

/// <summary>
/// Builds the OpenHarmony fs-verity code-sign payload carried as the value of the property block
/// (<c>0x20000003</c>): an fs-verity superblock with three segments (fs-verity info, HAP info, native library
/// info), Merkle trees over the aligned container prefix and native entries, one detached CMS per fs-verity
/// digest, and the optional permission-signing sub-block. The layout reproduces the official
/// <c>hap-sign-tool</c> byte for byte except for the inherently random CMS signatures and signing times.
/// <para>构建 OpenHarmony fs-verity code-sign 载荷（property 块 <c>0x20000003</c> 的值）：fs-verity
/// 超级块含三段（fs-verity 信息、HAP 信息、native 库信息），Merkle 树覆盖对齐后的容器前缀与 native 条目，
/// 每个 fs-verity 摘要各对应一个分离式 CMS，并可选附带权限签名子块。除 CMS 签名与签名时间的固有随机性外，
/// 布局与官方 <c>hap-sign-tool</c> 逐字节一致。</para>
/// </summary>
/// <remarks>
/// The container prefix digest follows the official quirk: the prefix ends at the data start of the first entry
/// that is neither a stored runnable artifact nor the page bitmap, so a HAP whose first such entry sits at offset
/// zero (the usual case after official alignment) digests zero bytes. Every hash goes through
/// <see cref="Sha256"/> per the repository digest boundary.
/// <para>容器前缀摘要沿用官方特性：前缀结束于第一个既非 stored 可运行产物也非页位图的条目的数据起点，
/// 因此首个此类条目位于偏移零的 HAP（官方对齐后的常见形态）摘要为零字节。按仓库摘要边界，全部哈希经
/// <see cref="Sha256"/> 计算。</para>
/// </remarks>
public static class HapCodeSigner
{
    private const int PageSize = HapSignatureConstants.AlignmentPage;
    private const int DigestSize = 32;
    private const byte FsVerityHashAlgorithmSha256 = 1;
    private const byte FsVerityLog2BlockSize = 12;
    private const int FsVerityDescriptorSize = 256;
    private const int FsVerityFlagStoreMerkleTreeOffset = 0x1;

    private const int CodeSignBlockHeaderSize = 32;
    private const int CodeSignSegmentHeaderSize = 12;
    private const int CodeSignSegmentCount = 3;
    private const int CodeSignSegmentListSize = CodeSignSegmentHeaderSize * CodeSignSegmentCount;
    private const ulong CodeSignBlockMagic = 0xE046C8C65389FCCD;
    private const uint CodeSignBlockVersion = 1;
    private const int CodeSignFlagMerkleTreeInlined = 0x1;
    private const int CodeSignFlagNativeLibIncluded = 0x2;
    private const int SignInfoFixedSize = 60;
    private const int SignInfoFlagMerkleTreeIncluded = 0x1;
    private const int ExtensionMerkleTreeType = 1;
    private const int ExtensionMerkleTreeDataSize = 80;
    private const int ExtensionPageInfoType = 2;
    private const int ExtensionPageInfoUnitSize = 4;

    private const uint SegmentFsVerityInfoMagic = 0x1E3831AB;
    private const uint SegmentHapInfoMagic = 0xC1B5CC66;
    private const uint SegmentNativeLibInfoMagic = 0x0ED2E720;

    private const ulong PermissionBlockMagic = 0x28E2450F93036A7D;
    private const uint PermissionBlockTypeId = HapSignatureConstants.BlockIdCodeSign + 1;
    private const int MaximumProfileBytes = 8 << 20;
    private const int MaximumModuleBytes = 4 << 20;
    private const long MaximumHnpBytes = 1 << 30;

    private const string ModuleEntryName = "module.json";
    private const string DebugOwnerId = "DEBUG_LIB_ID";
    private const string SharedOwnerId = "SHARED_LIB_ID";
    private const string BundleTypePlugin = "appPlugin";

    /// <summary>
    /// Builds the property block value from a container path and resolved signing material.
    /// <para>从容器路径与已解析的签名材料构建 property 块值。</para>
    /// </summary>
    /// <param name="containerPath">Unsigned (or block-replaced) container path; the code-sign payload digests the
    /// container after the official alignment is applied. <para>未签（或按替换语义处理既有块）的容器路径；
    /// code-sign 载荷对应用官方对齐策略后的容器做摘要。</para></param>
    /// <param name="material">Resolved signing material. <para>已解析的签名材料。</para></param>
    /// <param name="options">Build options. <para>构建选项。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The property block value, ready to be carried by
    /// <see cref="HapSigningOptions.PropertyBlock"/>. <para>property 块值，可直接交给
    /// <see cref="HapSigningOptions.PropertyBlock"/>。</para></returns>
    /// <exception cref="ArgumentException">A path or option is invalid.
    /// <para>路径或选项非法。</para></exception>
    /// <exception cref="InvalidDataException">The container is not a supported ZIP32 HAP, an entry escapes its
    /// bounds, the code-sign prefix is not page aligned, or the profile/module JSON is malformed.
    /// <para>容器不是受支持的 ZIP32 HAP、条目越界、code-sign 前缀未按页对齐，或 Profile/模块 JSON 非法。</para></exception>
    public static byte[] BuildPropertyBlock(string containerPath, HapSignerMaterial material,
        HapCodeSignerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(material);
        return BuildPropertyBlock(containerPath, material.SigningCertificate, material.Chain, material.Key,
            material.Profile, options, cancellationToken);
    }

    /// <summary>
    /// Builds the property block value from explicit signing pieces.
    /// <para>从显式签名要素构建 property 块值。</para>
    /// </summary>
    /// <param name="containerPath">Unsigned (or block-replaced) container path.
    /// <para>未签（或按替换语义处理既有块）的容器路径。</para></param>
    /// <param name="signingCertificate">The certificate carrying the signing key.
    /// <para>携带签名私钥的证书。</para></param>
    /// <param name="chainCertificates">Chain certificates to embed (leaf excluded).
    /// <para>需内嵌的链上证书（不含叶子）。</para></param>
    /// <param name="signingKey">The private key bound to the leaf certificate.
    /// <para>与叶子证书绑定的私钥。</para></param>
    /// <param name="profileBytes">Signed profile bytes (<c>.p7b</c> or raw JSON).
    /// <para>已签 Profile 字节（<c>.p7b</c> 或原始 JSON）。</para></param>
    /// <param name="options">Build options. <para>构建选项。</para></param>
    /// <param name="cancellationToken">Cancellation token. <para>取消令牌。</para></param>
    /// <returns>The property block value. <para>property 块值。</para></returns>
    /// <exception cref="ArgumentException">An argument is invalid. <para>参数非法。</para></exception>
    /// <exception cref="InvalidDataException">The container, profile or module metadata is malformed.
    /// <para>容器、Profile 或模块元数据非法。</para></exception>
    public static byte[] BuildPropertyBlock(string containerPath, X509Certificate2 signingCertificate,
        IReadOnlyList<X509Certificate2> chainCertificates, AsymmetricAlgorithm signingKey, byte[] profileBytes,
        HapCodeSignerOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerPath);
        ArgumentNullException.ThrowIfNull(signingCertificate);
        ArgumentNullException.ThrowIfNull(chainCertificates);
        ArgumentNullException.ThrowIfNull(signingKey);
        ArgumentNullException.ThrowIfNull(profileBytes);
        if (profileBytes.Length is 0 or > MaximumProfileBytes)
        {
            throw new InvalidDataException(
                $"Profile size {profileBytes.Length} is out of range. Profile 大小 {profileBytes.Length} 越界。");
        }

        var resolved = options ?? new HapCodeSignerOptions();
        if (resolved.FinalBlockCount is < 1 or > 64)
        {
            throw new ArgumentException(
                $"Final block count {resolved.FinalBlockCount} is out of range. 最终块数 {resolved.FinalBlockCount} 越界。",
                nameof(options));
        }

        var profile = ProfileFacts.FromBytes(profileBytes);
        using var container = File.OpenRead(containerPath);
        var sections = ApkZipSections.Locate(container);
        var entries = HapZipArchive.Read(container, sections.CentralDirectoryOffset, sections.CentralDirectorySize);
        HapZipEntry.ApplyAlignment(entries);

        var entriesEnd = 0L;
        foreach (var entry in entries)
        {
            checked
            {
                entriesEnd += HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length + entry.Data.Length;
            }
        }

        var codeSignOffset = entriesEnd + 12L * resolved.FinalBlockCount + 12L;
        if (codeSignOffset > uint.MaxValue)
        {
            throw new InvalidDataException("The code-sign offset exceeds the ZIP32 range. code-sign 偏移超出 ZIP32 范围。");
        }

        var moduleContent = ReadModuleContent(entries, cancellationToken);
        var moduleFacts = moduleContent is null ? ModuleFacts.Empty : ModuleFacts.FromJson(moduleContent);
        var shareFileContent = ReadShareFileContent(entries, moduleFacts.ShareFilesPath, cancellationToken);
        var pluginId = ResolvePluginId(moduleFacts, profile);

        var block = BuildCodeSignBlock(entries, codeSignOffset, moduleFacts, shareFileContent, resolved, pluginId,
            profile, signingCertificate, chainCertificates, signingKey, cancellationToken);

        var value = new List<byte>(12 + block.Length + (moduleContent is null ? 0 : 256))
        {
            Capacity = 12 + block.Length + (moduleContent is null ? 0 : 256),
        };
        AppendUInt32(value, HapSignatureConstants.BlockIdCodeSign);
        AppendUInt32(value, checked((uint)block.Length));
        AppendUInt32(value, (uint)codeSignOffset);
        value.AddRange(block);
        if (moduleContent is not null && resolved.IncludePermissionBlock)
        {
            value.AddRange(BuildPermissionBlock(profile, moduleContent, shareFileContent, block, resolved,
                signingKey, cancellationToken));
        }

        return value.ToArray();
    }

    // ── code-sign block ────────────────────────────────────────────────────

    private static byte[] BuildCodeSignBlock(IReadOnlyList<HapZipEntry> entries, long codeSignOffset,
        ModuleFacts moduleFacts, byte[]? shareFileContent, HapCodeSignerOptions options, string? pluginId,
        ProfileFacts profile, X509Certificate2 signingCertificate, IReadOnlyList<X509Certificate2> chainCertificates,
        AsymmetricAlgorithm signingKey, CancellationToken cancellationToken)
    {
        var padding = ComputeMerkleTreePadding(codeSignOffset);
        var fsvTreeOffset = codeSignOffset + CodeSignBlockHeaderSize + CodeSignSegmentListSize + padding;

        var dataSize = ComputeCodeSignDataSize(entries, out var pageInfo);
        if (dataSize % PageSize != 0)
        {
            throw new InvalidDataException(
                $"The code-sign data offset {dataSize} is not 4096-byte aligned. code-sign 数据偏移 {dataSize} 未按 4096 字节对齐。");
        }

        var hapTree = ComputeFsVerityOfPrefix(entries, dataSize, fsvTreeOffset, cancellationToken);
        var fsVerityDigest = BuildFsVerityDigest(dataSize, hapTree.RootHash,
            fsvTreeOffset == 0 ? 0 : FsVerityFlagStoreMerkleTreeOffset, fsvTreeOffset);
        var hapSignature = BuildCodeSignCms(fsVerityDigest, options.Algorithm, signingCertificate,
            chainCertificates, signingKey, profile.OwnerId, pluginId, cancellationToken);
        var hapSignInfo = BuildSignInfo(SignInfoFlagMerkleTreeIncluded, dataSize, hapSignature,
            pageInfo is null ? 1 : 2);
        AppendMerkleTreeExtension(hapSignInfo, hapTree.Tree.Length, fsvTreeOffset, hapTree.RootHash);
        if (pageInfo is not null)
        {
            ValidatePageInfo(pageInfo, dataSize);
            var digestV2 = BuildFsVerityDigestV2(dataSize, hapTree.RootHash, fsvTreeOffset, pageInfo);
            var signatureV2 = BuildCodeSignCms(digestV2, options.Algorithm, signingCertificate, chainCertificates,
                signingKey, profile.OwnerId, pluginId, cancellationToken);
            AppendPageInfoExtension(hapSignInfo, pageInfo, signatureV2);
        }

        var nativeEntries = BuildNativeSignInfos(entries, moduleFacts, options, profile, signingCertificate,
            chainCertificates, signingKey, cancellationToken);

        var fsVeritySegment = BuildFsVerityInfoSegment();
        var hapInfoSegment = BuildHapInfoSegment(hapSignInfo);
        var nativeLibInfoSegment = BuildNativeLibInfoSegment(nativeEntries);

        var segmentStart = CodeSignBlockHeaderSize + CodeSignSegmentListSize + padding + hapTree.Tree.Length;
        var blockSize = segmentStart
            + fsVeritySegment.Length
            + hapInfoSegment.Length
            + nativeLibInfoSegment.Length;
        var flags = CodeSignFlagMerkleTreeInlined
            | (nativeEntries.Count > 0 ? CodeSignFlagNativeLibIncluded : 0);

        var output = new List<byte>(blockSize) { Capacity = blockSize };
        AppendUInt64(output, CodeSignBlockMagic);
        AppendUInt32(output, CodeSignBlockVersion);
        AppendUInt32(output, checked((uint)blockSize));
        AppendUInt32(output, CodeSignSegmentCount);
        AppendUInt32(output, (uint)flags);
        AppendZeros(output, 8);
        AppendSegmentHeader(output, 1, segmentStart, fsVeritySegment.Length);
        AppendSegmentHeader(output, 2, segmentStart + fsVeritySegment.Length, hapInfoSegment.Length);
        AppendSegmentHeader(output, 3, segmentStart + fsVeritySegment.Length + hapInfoSegment.Length,
            nativeLibInfoSegment.Length);
        AppendZeros(output, (int)padding);
        output.AddRange(hapTree.Tree);
        output.AddRange(fsVeritySegment);
        output.AddRange(hapInfoSegment);
        output.AddRange(nativeLibInfoSegment);
        return output.ToArray();
    }

    private static int ComputeMerkleTreePadding(long codeSignOffset)
    {
        var residual = (codeSignOffset + CodeSignBlockHeaderSize + CodeSignSegmentListSize) % PageSize;
        return residual == 0 ? 0 : (int)(PageSize - residual);
    }

    /// <summary>
    /// Replicates the official prefix rule over the aligned layout: stored runnable entries and the page bitmap are
    /// skipped, and the prefix ends at the data start of the first remaining entry, or zero when that entry is the
    /// first one in the file. <para>复刻官方前缀规则（按对齐后布局）：跳过 stored 可运行条目与页位图，前缀结束于
    /// 第一个其余条目的数据起点；该条目是文件首条目时前缀为零。</para>
    /// </summary>
    private static long ComputeCodeSignDataSize(IReadOnlyList<HapZipEntry> entries, out PageInfoFacts? pageInfo)
    {
        pageInfo = null;
        var currentOffset = 0L;
        foreach (var entry in entries)
        {
            var dataOffset = currentOffset + HapZipEntry.LocalHeaderFixed + entry.Name.Length + entry.LocalExtra.Length;
            if (entry.IsBitmap)
            {
                pageInfo = new PageInfoFacts(dataOffset, dataOffset / PageSize * ExtensionPageInfoUnitSize);
            }
            else if (!(entry.IsRunnable && entry.Method == 0))
            {
                // The first entry always sits at file offset zero, so a prefix rooted there is empty.
                if (currentOffset == 0)
                {
                    return 0;
                }

                return dataOffset;
            }

            currentOffset = dataOffset + entry.Data.Length;
        }

        return 0;
    }

    private static FsVerityTree ComputeFsVerityOfPrefix(IReadOnlyList<HapZipEntry> entries, long dataSize,
        long fsvTreeOffset, CancellationToken cancellationToken)
    {
        var accumulator = new MerkleAccumulator();
        var remaining = dataSize;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (remaining <= 0)
            {
                break;
            }

            PushPiece(accumulator, BuildLocalHeader(entry), ref remaining);
            PushPiece(accumulator, entry.Name, ref remaining);
            PushPiece(accumulator, entry.LocalExtra, ref remaining);
            PushPiece(accumulator, entry.Data, ref remaining);
        }

        if (remaining != 0)
        {
            throw new InvalidDataException(
                $"The container prefix is shorter than the code-sign data size {dataSize}. 容器前缀短于 code-sign 数据大小 {dataSize}。");
        }

        return accumulator.Finish(dataSize, retainTree: fsvTreeOffset != 0);
    }

    private static void PushPiece(MerkleAccumulator accumulator, ReadOnlySpan<byte> piece, ref long remaining)
    {
        if (remaining <= 0)
        {
            return;
        }

        var take = (int)Math.Min(piece.Length, remaining);
        accumulator.Push(piece[..take]);
        remaining -= take;
    }

    private static byte[] BuildLocalHeader(HapZipEntry entry)
    {
        var header = new byte[HapZipEntry.LocalHeaderFixed];
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
        return header;
    }

    // ── fs-verity ──────────────────────────────────────────────────────────

    private readonly record struct FsVerityTree(byte[] Tree, byte[] RootHash);

    private sealed record PageInfoFacts(long MapOffset, long MapSize);

    /// <summary>Streaming fs-verity Merkle tree builder: page digests feed higher levels, the last page is
    /// zero-padded, and the tree bytes order levels top-down like the official generator.
    /// <para>流式 fs-verity Merkle 树构建器：页摘要喂入上层，末页补零，树字节按官方生成器的自顶向下顺序排列。</para></summary>
    private sealed class MerkleAccumulator
    {
        private readonly List<byte> _leaves = new();
        private readonly byte[] _page = new byte[PageSize];
        private int _pageLength;
        private long _observed;

        internal void Push(ReadOnlySpan<byte> piece)
        {
            _observed += piece.Length;
            while (!piece.IsEmpty)
            {
                var take = Math.Min(PageSize - _pageLength, piece.Length);
                piece[..take].CopyTo(_page.AsSpan(_pageLength));
                _pageLength += take;
                piece = piece[take..];
                if (_pageLength == PageSize)
                {
                    _leaves.AddRange(Sha256.ComputeHash(_page));
                    Array.Fill(_page, (byte)0);
                    _pageLength = 0;
                }
            }
        }

        internal FsVerityTree Finish(long dataSize, bool retainTree)
        {
            if (_observed != dataSize)
            {
                throw new InvalidDataException(
                    $"The fs-verity input length {_observed} disagrees with the expected size {dataSize}. fs-verity 输入长度 {_observed} 与期望大小 {dataSize} 不一致。");
            }

            if (_pageLength != 0)
            {
                Array.Fill(_page, (byte)0, _pageLength, PageSize - _pageLength);
                _leaves.AddRange(Sha256.ComputeHash(_page));
            }

            if (dataSize == 0)
            {
                return new FsVerityTree([], new byte[DigestSize]);
            }

            if (dataSize <= PageSize)
            {
                var root = new byte[DigestSize];
                _leaves.CopyTo(0, root, 0, DigestSize);
                return new FsVerityTree([], root);
            }

            if (!retainTree)
            {
                var level = _leaves.ToArray();
                while (level.Length > PageSize)
                {
                    level = HashLevel(level);
                }

                return new FsVerityTree([], Sha256.ComputeHash(level.AsSpan(0, PageSize)));
            }

            var levels = new List<byte[]> { _leaves.ToArray() };
            while (levels[^1].Length > PageSize)
            {
                levels.Add(HashLevel(levels[^1]));
            }

            levels.Reverse();
            var tree = new byte[checked(levels.Sum(static level => level.Length))];
            var offset = 0;
            foreach (var level in levels)
            {
                level.CopyTo(tree, offset);
                offset += level.Length;
            }

            return new FsVerityTree(tree, Sha256.ComputeHash(tree.AsSpan(0, PageSize)));
        }

        private static byte[] HashLevel(byte[] level)
        {
            var chunkCount = (level.Length + PageSize - 1) / PageSize;
            var paddedLength = (chunkCount * DigestSize + PageSize - 1) / PageSize * PageSize;
            var output = new List<byte>(paddedLength) { Capacity = paddedLength };
            Span<byte> page = stackalloc byte[PageSize];
            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                var start = chunkIndex * PageSize;
                var length = Math.Min(PageSize, level.Length - start);
                page.Fill(0);
                level.AsSpan(start, length).CopyTo(page);
                output.AddRange(Sha256.ComputeHash(page));
            }

            return output.ToArray();
        }
    }

    private static byte[] BuildFsVerityDigest(long fileSize, byte[] rootHash, int flags, long merkleTreeOffset)
    {
        var descriptor = new byte[FsVerityDescriptorSize];
        descriptor[0] = 1;
        descriptor[1] = FsVerityHashAlgorithmSha256;
        descriptor[2] = FsVerityLog2BlockSize;
        descriptor[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)fileSize);
        rootHash.AsSpan(0, DigestSize).CopyTo(descriptor.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(112), (uint)flags);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(116), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(120), (ulong)merkleTreeOffset);
        return WrapFsVerityDigest(Sha256.ComputeHash(descriptor));
    }

    private static byte[] BuildFsVerityDigestV2(long dataSize, byte[] rootHash, long merkleTreeOffset,
        PageInfoFacts pageInfo)
    {
        var descriptor = new byte[FsVerityDescriptorSize];
        descriptor[0] = 1;
        descriptor[1] = FsVerityHashAlgorithmSha256;
        descriptor[2] = FsVerityLog2BlockSize;
        descriptor[3] = 0;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 0);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(8), (ulong)dataSize);
        rootHash.AsSpan(0, DigestSize).CopyTo(descriptor.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(112),
            (uint)((ExtensionPageInfoUnitSize << 1) | FsVerityFlagStoreMerkleTreeOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(116), checked((uint)pageInfo.MapSize));
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(120), (ulong)merkleTreeOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor.AsSpan(128), (ulong)pageInfo.MapOffset);
        descriptor[FsVerityDescriptorSize - 1] = 2;
        return WrapFsVerityDigest(Sha256.ComputeHash(descriptor));
    }

    private static byte[] WrapFsVerityDigest(byte[] descriptorDigest)
    {
        var output = new byte[12 + descriptorDigest.Length];
        "FSVerity"u8.CopyTo(output);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(8), FsVerityHashAlgorithmSha256);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(10), (ushort)descriptorDigest.Length);
        descriptorDigest.CopyTo(output, 12);
        return output;
    }

    private static void ValidatePageInfo(PageInfoFacts pageInfo, long dataSize)
    {
        if (pageInfo.MapOffset % PageSize != 0 || pageInfo.MapOffset < 0 || pageInfo.MapSize < 0)
        {
            throw new InvalidDataException(
                $"The page bitmap offset {pageInfo.MapOffset} or size {pageInfo.MapSize} is invalid. 页位图偏移 {pageInfo.MapOffset} 或大小 {pageInfo.MapSize} 非法。");
        }

        if (pageInfo.MapSize % ExtensionPageInfoUnitSize != 0
            || pageInfo.MapOffset > dataSize - pageInfo.MapSize / 8
            || pageInfo.MapSize / ExtensionPageInfoUnitSize >= dataSize / PageSize)
        {
            throw new InvalidDataException(
                $"The page bitmap size {pageInfo.MapSize} escapes the code-sign data size {dataSize}. 页位图大小 {pageInfo.MapSize} 越出 code-sign 数据大小 {dataSize}。");
        }
    }

    // ── sign info, extensions and segments ─────────────────────────────────

    private static List<byte> BuildSignInfo(int flags, long dataSize, byte[] signature, int extensionCount)
    {
        var zeroPaddingLength = (4 - signature.Length % 4) % 4;
        var extensionOffset = SignInfoFixedSize + signature.Length + zeroPaddingLength;
        var output = new List<byte>(SignInfoFixedSize + signature.Length + zeroPaddingLength)
        {
            Capacity = SignInfoFixedSize + signature.Length + zeroPaddingLength,
        };
        AppendUInt32(output, 0);
        AppendUInt32(output, checked((uint)signature.Length));
        AppendUInt32(output, (uint)flags);
        AppendUInt64(output, (ulong)dataSize);
        AppendZeros(output, 32);
        // The official layout records how many extensions follow the fixed part; the tool writes 1 for a HAP sign
        // info carrying only the Merkle-tree extension and 0 for native entries that carry none.
        AppendUInt32(output, checked((uint)extensionCount));
        AppendUInt32(output, (uint)extensionOffset);
        output.AddRange(signature);
        AppendZeros(output, zeroPaddingLength);
        return output;
    }

    private static void AppendMerkleTreeExtension(List<byte> signInfo, long treeSize, long treeOffset, byte[] rootHash)
    {
        AppendUInt32(signInfo, ExtensionMerkleTreeType);
        AppendUInt32(signInfo, ExtensionMerkleTreeDataSize);
        AppendUInt64(signInfo, (ulong)treeSize);
        AppendUInt64(signInfo, (ulong)treeOffset);
        signInfo.AddRange(rootHash);
        AppendZeros(signInfo, 64 - DigestSize);
    }

    private static void AppendPageInfoExtension(List<byte> signInfo, PageInfoFacts pageInfo, byte[] signature)
    {
        var zeroPaddingLength = (4 - signature.Length % 4) % 4;
        AppendUInt32(signInfo, ExtensionPageInfoType);
        AppendUInt32(signInfo, checked((uint)(24 + signature.Length + zeroPaddingLength)));
        AppendUInt64(signInfo, (ulong)pageInfo.MapOffset);
        AppendUInt64(signInfo, (ulong)pageInfo.MapSize);
        signInfo.Add(ExtensionPageInfoUnitSize);
        AppendZeros(signInfo, 3);
        AppendUInt32(signInfo, checked((uint)signature.Length));
        signInfo.AddRange(signature);
        AppendZeros(signInfo, zeroPaddingLength);
    }

    private static byte[] BuildFsVerityInfoSegment()
    {
        // Official layout: magic, then the fs-verity superblock fields start at offset 4 — version, hash algorithm
        // and log2 block size — followed by zeroed reserved space up to the 64-byte segment size.
        var output = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(output, SegmentFsVerityInfoMagic);
        output[4] = 1;
        output[5] = FsVerityHashAlgorithmSha256;
        output[6] = FsVerityLog2BlockSize;
        return output;
    }

    private static byte[] BuildHapInfoSegment(List<byte> hapSignInfo)
    {
        var output = new List<byte>(4 + hapSignInfo.Count) { Capacity = 4 + hapSignInfo.Count };
        AppendUInt32(output, SegmentHapInfoMagic);
        output.AddRange(hapSignInfo);
        return output.ToArray();
    }

    private sealed record NativeSignInfo(string Name, long DataSize, byte[] Signature);

    private static List<NativeSignInfo> BuildNativeSignInfos(IReadOnlyList<HapZipEntry> entries,
        ModuleFacts moduleFacts, HapCodeSignerOptions options, ProfileFacts profile,
        X509Certificate2 signingCertificate, IReadOnlyList<X509Certificate2> chainCertificates,
        AsymmetricAlgorithm signingKey, CancellationToken cancellationToken)
    {
        var result = new List<NativeSignInfo>();
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.NameText;
            if (!name.EndsWith(".an", StringComparison.Ordinal) && !name.StartsWith("libs/", StringComparison.Ordinal))
            {
                continue;
            }

            var content = ReadEntryContent(entry, cancellationToken);
            var rootHash = ComputeContentRootHash(content);
            var signature = BuildCodeSignCms(
                BuildFsVerityDigest(content.Length, rootHash, flags: 0, merkleTreeOffset: 0), options.Algorithm,
                signingCertificate, chainCertificates, signingKey, profile.OwnerId, pluginId: null, cancellationToken);
            result.Add(new NativeSignInfo(name, content.Length, signature));
        }

        AppendHnpSignInfos(entries, moduleFacts, options, profile, signingCertificate, chainCertificates,
            signingKey, result, cancellationToken);
        return result;
    }

    private static void AppendHnpSignInfos(IReadOnlyList<HapZipEntry> entries, ModuleFacts moduleFacts,
        HapCodeSignerOptions options, ProfileFacts profile, X509Certificate2 signingCertificate,
        IReadOnlyList<X509Certificate2> chainCertificates, AsymmetricAlgorithm signingKey,
        List<NativeSignInfo> result, CancellationToken cancellationToken)
    {
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.NameText;
            if (!name.StartsWith("hnp/", StringComparison.Ordinal)
                || !name.EndsWith(".hnp", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var packageName = string.Join('/', name.Split('/').Skip(2));
            if (!moduleFacts.HnpPackages.TryGetValue(packageName, out var packageType))
            {
                throw new InvalidDataException(
                    $"The HNP '{name}' is not declared in module.json. HNP '{name}' 未在 module.json 中声明。");
            }

            var ownerId = packageType == "public" ? profile.PublicHnpOwnerId : profile.OwnerId;
            var hnpBytes = ReadEntryContent(entry, cancellationToken);
            if (hnpBytes.Length > MaximumHnpBytes)
            {
                throw new InvalidDataException(
                    $"The HNP '{name}' exceeds {MaximumHnpBytes} bytes. HNP '{name}' 超过 {MaximumHnpBytes} 字节。");
            }

            using var hnpStream = new MemoryStream(hnpBytes, writable: false);
            var sections = ApkZipSections.Locate(hnpStream);
            var innerEntries = HapZipArchive.Read(hnpStream, sections.CentralDirectoryOffset, sections.CentralDirectorySize);
            foreach (var inner in innerEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = ReadEntryContent(inner, cancellationToken);
                if (content.Length < 4
                    || content[0] != 0x7F || content[1] != (byte)'E' || content[2] != (byte)'L' || content[3] != (byte)'F')
                {
                    continue;
                }

                var rootHash = ComputeContentRootHash(content);
                var signature = BuildCodeSignCms(
                    BuildFsVerityDigest(content.Length, rootHash, flags: 0, merkleTreeOffset: 0), options.Algorithm,
                    signingCertificate, chainCertificates, signingKey, ownerId, pluginId: null, cancellationToken);
                result.Add(new NativeSignInfo($"{name}!/{inner.NameText}", content.Length, signature));
            }
        }
    }

    private static byte[] ComputeContentRootHash(byte[] content)
    {
        var accumulator = new MerkleAccumulator();
        accumulator.Push(content);
        return accumulator.Finish(content.Length, retainTree: false).RootHash;
    }

    private static byte[] ReadEntryContent(HapZipEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Method == 0)
        {
            return entry.Data;
        }

        if (entry.Method != 8)
        {
            throw new InvalidDataException(
                $"The entry '{entry.NameText}' uses unsupported ZIP method {entry.Method}. 条目 '{entry.NameText}' 使用不受支持的 ZIP 方法 {entry.Method}。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var source = new MemoryStream(entry.Data, writable: false);
        using var output = new MemoryStream();
        CompressionService.Decompress(source, output, CompressionFormat.Deflate, entry.UncompressedSize, cancellationToken);
        var content = output.ToArray();
        if (content.Length != entry.UncompressedSize)
        {
            throw new InvalidDataException(
                $"The entry '{entry.NameText}' decompressed to {content.Length} bytes, expected {entry.UncompressedSize}. 条目 '{entry.NameText}' 解压为 {content.Length} 字节，期望 {entry.UncompressedSize}。");
        }

        return content;
    }

    private static byte[] BuildNativeLibInfoSegment(List<NativeSignInfo> entries)
    {
        var names = entries.Select(static entry => Encoding.UTF8.GetBytes(entry.Name)).ToArray();
        var signInfos = entries.Select(static entry => BuildSignInfo(0, entry.DataSize, entry.Signature, 0)).ToArray();
        var namesLength = names.Sum(static name => name.Length);
        var signInfosLength = signInfos.Sum(static info => info.Count);
        var zeroPaddingLength = (4 - namesLength % 4) % 4;
        var nameBase = 12 + entries.Count * 16;
        var signInfoBase = nameBase + namesLength + zeroPaddingLength;
        var segmentSize = 12 + entries.Count * 16 + namesLength + zeroPaddingLength + signInfosLength;

        var output = new List<byte>(segmentSize) { Capacity = segmentSize };
        AppendUInt32(output, SegmentNativeLibInfoMagic);
        AppendUInt32(output, checked((uint)segmentSize));
        AppendUInt32(output, checked((uint)entries.Count));
        var nameOffset = 0;
        var signInfoOffset = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            AppendUInt32(output, checked((uint)(nameBase + nameOffset)));
            AppendUInt32(output, checked((uint)names[index].Length));
            AppendUInt32(output, checked((uint)(signInfoBase + signInfoOffset)));
            AppendUInt32(output, checked((uint)signInfos[index].Count));
            nameOffset += names[index].Length;
            signInfoOffset += signInfos[index].Count;
        }

        foreach (var name in names)
        {
            output.AddRange(name);
        }

        AppendZeros(output, zeroPaddingLength);
        foreach (var signInfo in signInfos)
        {
            output.AddRange(signInfo);
        }

        return output.ToArray();
    }

    // ── permission block (0x30000002) ──────────────────────────────────────

    private static byte[] BuildPermissionBlock(ProfileFacts profile, byte[] moduleContent, byte[]? shareFileContent,
        byte[] codeSignBlock, HapCodeSignerOptions options, AsymmetricAlgorithm signingKey,
        CancellationToken cancellationToken)
    {
        var digests = new List<(int Type, byte[] Digest)>(4);
        if (profile.Json.Length > 0)
        {
            digests.Add((1, Sha256.ComputeHash(profile.Json)));
        }

        if (moduleContent.Length > 0)
        {
            digests.Add((2, Sha256.ComputeHash(moduleContent)));
        }

        digests.Add((3, Sha256.ComputeHash(codeSignBlock)));
        if (shareFileContent is { Length: > 0 })
        {
            digests.Add((4, Sha256.ComputeHash(shareFileContent)));
        }

        var digestContents = new List<byte>(digests.Count * (4 + DigestSize));
        foreach (var (type, digest) in digests)
        {
            AppendUInt32(digestContents, (uint)type);
            digestContents.AddRange(digest);
        }

        var block = new List<byte>(18 + digestContents.Count + 80);
        AppendUInt64(block, PermissionBlockMagic);
        AppendUInt32(block, (uint)options.Algorithm);
        AppendUInt32(block, checked((uint)digestContents.Count));
        block.Add((byte)digests.Count);
        block.Add(0);
        block.AddRange(digestContents);
        var unsigned = block.ToArray();
        var signature = SignRaw(signingKey, options.Algorithm, unsigned, cancellationToken);
        AppendUInt32(block, checked((uint)signature.Length));
        block.AddRange(signature);

        var output = new List<byte>(12 + block.Count) { Capacity = 12 + block.Count };
        AppendUInt32(output, PermissionBlockTypeId);
        AppendUInt32(output, checked((uint)block.Count));
        AppendUInt32(output, 24 + checked((uint)codeSignBlock.Length));
        output.AddRange(block);
        return output.ToArray();
    }

    private static byte[] SignRaw(AsymmetricAlgorithm signingKey, HapDigestAlgorithm algorithm,
        ReadOnlySpan<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (hash, _) = DigestOf(algorithm);
        return signingKey switch
        {
            // The official tool emits the RFC 3279 DER SEQUENCE form of an ECDSA signature, never the raw
            // IEEE P1363 r||s pair; a raw pair makes the CMS 10 bytes short and the signature unreadable to verifiers.
            ECDsa ecdsa => ecdsa.SignData(data, hash, DSASignatureFormat.Rfc3279DerSequence),
            RSA rsa => rsa.SignData(data, hash, RSASignaturePadding.Pss),
            _ => throw new InvalidOperationException(
                "The signing key is neither RSA nor ECDSA. 签名密钥既非 RSA 也非 ECDSA。"),
        };
    }

    // ── profile and module facts ───────────────────────────────────────────

    private sealed class ProfileFacts
    {
        private ProfileFacts(byte[] json, string? ownerId, string publicHnpOwnerId)
        {
            Json = json;
            OwnerId = ownerId;
            PublicHnpOwnerId = publicHnpOwnerId;
        }

        internal byte[] Json { get; }

        internal string? OwnerId { get; }

        internal string PublicHnpOwnerId { get; }

        internal static ProfileFacts FromBytes(byte[] bytes)
        {
            var content = ExtractContent(bytes);
            var json = ExtractJsonObject(content);
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(json);
                root = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Profile JSON is malformed: {exception.Message}. Profile JSON 非法：{exception.Message}。");
            }
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Profile content carries no type field. Profile 内容缺少 type 字段。");
            }

            var profileType = type.GetString() ?? string.Empty;
            string? ownerId;
            string publicOwnerId;
            if (string.Equals(profileType, "debug", StringComparison.OrdinalIgnoreCase))
            {
                ownerId = DebugOwnerId;
                publicOwnerId = DebugOwnerId;
            }
            else if (string.Equals(profileType, "release", StringComparison.OrdinalIgnoreCase))
            {
                if (root.TryGetProperty("bundle-info", out var bundle)
                    && bundle.ValueKind == JsonValueKind.Object
                    && bundle.TryGetProperty("app-identifier", out var identifier)
                    && identifier.ValueKind == JsonValueKind.String)
                {
                    ownerId = identifier.GetString();
                    if (ownerId is { Length: 0 or > 32 })
                    {
                        throw new InvalidDataException(
                            "Profile app-identifier length is invalid. Profile app-identifier 长度非法。");
                    }
                }
                else
                {
                    ownerId = null;
                }

                publicOwnerId = SharedOwnerId;
            }
            else
            {
                throw new InvalidDataException(
                    $"Profile type '{profileType}' is not supported for code signing. Profile 类型 '{profileType}' 不支持 code signing。");
            }

            return new ProfileFacts(json, ownerId, publicOwnerId);
        }

        internal string ResolvePluginId()
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(Json);
                root = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Profile JSON is malformed: {exception.Message}. Profile JSON 非法：{exception.Message}。");
            }

            if (!root.TryGetProperty("app-services-capabilities", out var capabilities)
                || capabilities.ValueKind != JsonValueKind.Object
                || !capabilities.TryGetProperty("ohos.permission.kernel.SUPPORT_PLUGIN", out var permission)
                || permission.ValueKind != JsonValueKind.Object
                || !permission.TryGetProperty("pluginDistributionIDs", out var ids)
                || ids.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    "Profile pluginDistributionIDs is missing. Profile 缺少 pluginDistributionIDs。");
            }

            var pluginId = ids.GetString();
            if (string.IsNullOrEmpty(pluginId))
            {
                throw new InvalidDataException(
                    "Profile pluginDistributionIDs is empty. Profile pluginDistributionIDs 为空。");
            }

            return pluginId;
        }
    }

    private sealed class ModuleFacts
    {
        internal static readonly ModuleFacts Empty = new(null, null,
            new Dictionary<string, string>(StringComparer.Ordinal));

        private ModuleFacts(string? bundleType, string? shareFilesPath, Dictionary<string, string> hnpPackages)
        {
            BundleType = bundleType;
            ShareFilesPath = shareFilesPath;
            HnpPackages = hnpPackages;
        }

        internal string? BundleType { get; }

        internal string? ShareFilesPath { get; }

        internal Dictionary<string, string> HnpPackages { get; }

        internal static ModuleFacts FromJson(byte[] json)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(json);
                root = document.RootElement.Clone();
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"module.json is not valid JSON: {exception.Message}. module.json 不是合法 JSON：{exception.Message}。");
            }
            string? bundleType = null;
            if (root.TryGetProperty("app", out var app)
                && app.ValueKind == JsonValueKind.Object
                && app.TryGetProperty("bundleType", out var type)
                && type.ValueKind == JsonValueKind.String)
            {
                bundleType = type.GetString();
            }

            string? shareFilesPath = null;
            var hnpPackages = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("module", out var module)
                && module.ValueKind == JsonValueKind.Object)
            {
                if (module.TryGetProperty("shareFiles", out var shareFiles)
                    && shareFiles.ValueKind == JsonValueKind.String)
                {
                    shareFilesPath = shareFiles.GetString();
                }

                if (module.TryGetProperty("hnpPackages", out var packages)
                    && packages.ValueKind == JsonValueKind.Array)
                {
                    foreach (var package in packages.EnumerateArray())
                    {
                        if (package.ValueKind != JsonValueKind.Object
                            || !package.TryGetProperty("package", out var name)
                            || name.ValueKind != JsonValueKind.String)
                        {
                            continue;
                        }

                        var packageName = name.GetString() ?? string.Empty;
                        if (packageName.Length == 0)
                        {
                            continue;
                        }

                        var packageType = "private";
                        if (package.TryGetProperty("type", out var packageTypeElement)
                            && packageTypeElement.ValueKind == JsonValueKind.String
                            && packageTypeElement.GetString() is { Length: > 0 } value)
                        {
                            packageType = value;
                        }

                        hnpPackages[packageName] = packageType;
                    }
                }
            }

            return new ModuleFacts(bundleType, shareFilesPath, hnpPackages);
        }
    }

    private static string? ResolvePluginId(ModuleFacts moduleFacts, ProfileFacts profile)
    {
        if (!string.Equals(moduleFacts.BundleType, BundleTypePlugin, StringComparison.Ordinal))
        {
            return null;
        }

        return profile.ResolvePluginId();
    }

    private static byte[]? ReadModuleContent(IReadOnlyList<HapZipEntry> entries, CancellationToken cancellationToken)
    {
        HapZipEntry? module = null;
        foreach (var entry in entries)
        {
            if (entry.NameText.AsSpan().SequenceEqual(ModuleEntryName))
            {
                module = entry;
                break;
            }
        }

        if (module is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var content = ReadEntryContent(module, cancellationToken);
        if (content.Length == 0)
        {
            throw new InvalidDataException("The module.json content is empty. module.json 内容为空。");
        }

        if (content.Length > MaximumModuleBytes)
        {
            throw new InvalidDataException(
                $"The module.json content exceeds {MaximumModuleBytes} bytes. module.json 内容超过 {MaximumModuleBytes} 字节。");
        }

        return content;
    }

    private static byte[]? ReadShareFileContent(IReadOnlyList<HapZipEntry> entries, string? shareFilesPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(shareFilesPath) || !shareFilesPath.StartsWith("$profile:", StringComparison.Ordinal))
        {
            return null;
        }

        var fullName = "resources/base/profile/" + shareFilesPath["$profile:".Length..];
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.NameText;
            if (!fullName.Equals(name, StringComparison.Ordinal)
                && !(name.Length > fullName.Length + 1 && name.StartsWith(fullName + ".", StringComparison.Ordinal)))
            {
                continue;
            }

            var content = ReadEntryContent(entry, cancellationToken);
            if (content.Length > MaximumModuleBytes)
            {
                throw new InvalidDataException(
                    $"The share profile '{name}' exceeds {MaximumModuleBytes} bytes. 共享 Profile '{name}' 超过 {MaximumModuleBytes} 字节。");
            }

            return content;
        }

        return null;
    }

    private static byte[] ExtractContent(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf("-----BEGIN"u8.ToArray()) >= 0 || (bytes.Length > 1 && bytes[0] == (byte)'{'))
        {
            return bytes;
        }

        try
        {
            var cms = new SignedCms();
            cms.Decode(bytes);
            return cms.ContentInfo.Content;
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                $"Profile is neither PEM, JSON nor a CMS document: {exception.Message}. Profile 既非 PEM、JSON 也非 CMS 文档：{exception.Message}。");
        }
    }

    private static byte[] ExtractJsonObject(byte[] content)
    {
        var start = Array.IndexOf(content, (byte)'{');
        if (start < 0)
        {
            throw new InvalidDataException("Profile content carries no JSON object. Profile 内容不含 JSON 对象。");
        }

        var depth = 0;
        for (var index = start; index < content.Length; index++)
        {
            if (content[index] == (byte)'{')
            {
                depth++;
            }
            else if (content[index] == (byte)'}')
            {
                depth--;
                if (depth == 0)
                {
                    return content[start..(index + 1)];
                }
            }
        }

        throw new InvalidDataException("Profile JSON object is unbalanced. Profile JSON 对象不配平。");
    }

    // ── code-sign CMS (detached, owner-id attribute) ───────────────────────

    private static byte[] BuildCodeSignCms(byte[] content, HapDigestAlgorithm algorithm,
        X509Certificate2 signingCertificate, IReadOnlyList<X509Certificate2> chainCertificates,
        AsymmetricAlgorithm signingKey, string? ownerId, string? pluginId, CancellationToken cancellationToken)
    {
        var (hashName, digestOid) = DigestOf(algorithm);
        cancellationToken.ThrowIfCancellationRequested();
        var contentDigest = hashName == HashAlgorithmName.SHA256
            ? Sha256.ComputeHash(content)
            : hashName == HashAlgorithmName.SHA384
                ? Sha384.ComputeHash(content)
                : Sha512.ComputeHash(content);

        var attributes = new List<byte[]>(5)
        {
            Der.Sequence(
                Der.Oid("1.2.840.113549.1.9.3"),
                Der.SetOf([Der.Oid("1.2.840.113549.1.7.1")])),
            Der.Sequence(
                Der.Oid("1.2.840.113549.1.9.5"),
                Der.SetOf([Der.UtcTime(DateTime.UtcNow)])),
        };
        if (ownerId is not null)
        {
            attributes.Add(Der.Sequence(
                Der.Oid("1.3.6.1.4.1.2011.2.376.1.4.1"),
                Der.SetOf([Der.Utf8String(ownerId)])));
        }

        if (pluginId is not null)
        {
            attributes.Add(Der.Sequence(
                Der.Oid("1.3.6.1.4.1.2011.2.376.1.4.2"),
                Der.SetOf([Der.Utf8String(pluginId)])));
        }

        attributes.Add(Der.Sequence(
            Der.Oid("1.2.840.113549.1.9.4"),
            Der.SetOf([Der.OctetString(contentDigest)])));
        var signedAttributes = Der.SetOf([.. attributes]);
        var signature = SignRaw(signingKey, algorithm, signedAttributes, cancellationToken);
        var signedAttributesImplicit = Der.RetagSetToContextZero(signedAttributes);

        var certificates = OrderCertificates(signingCertificate, chainCertificates);
        var certificateSet = Der.ImplicitConstructedSet(
            [.. certificates.Select(static certificate => certificate.RawData)]);

        var signerInfo = Der.Sequence(
            Der.Integer(1),
            Der.Sequence(
                signingCertificate.IssuerName.RawData,
                Der.IntegerFromUnsignedBigEndian(signingCertificate.SerialNumberBytes.Span)),
            Der.Sequence(Der.Oid(digestOid)),
            signedAttributesImplicit,
            BuildSignatureAlgorithmIdentifier(algorithm),
            Der.OctetString(signature));

        var signedData = Der.Sequence(
            Der.Integer(1),
            Der.SetOf([Der.Sequence(Der.Oid(digestOid))]),
            Der.Sequence(Der.Oid("1.2.840.113549.1.7.1")),
            certificateSet,
            Der.SetOf([signerInfo]));
        return Der.Sequence(Der.Oid("1.2.840.113549.1.7.2"), Der.ContextExplicit(0, signedData));
    }

    private static List<X509Certificate2> OrderCertificates(X509Certificate2 signingCertificate,
        IReadOnlyList<X509Certificate2> chainCertificates)
    {
        // The official signer embeds the certificate set root-first: the chain walks inwards from the topmost
        // issuer down to the signing certificate, which comes last. Verified byte-for-byte against hap-sign-tool
        // output, where a root/sub/leaf chain appears in exactly that order.
        var certificates = new List<X509Certificate2>(chainCertificates.Count + 1) { signingCertificate };
        var remaining = new List<X509Certificate2>(chainCertificates);
        while (remaining.Count > 0)
        {
            // The next certificate below the current head is the one whose subject issued it.
            var issuerIndex = remaining.FindIndex(candidate =>
                certificates[^1].IssuerName.RawData.AsSpan().SequenceEqual(candidate.SubjectName.RawData));
            if (issuerIndex < 0)
            {
                break;
            }

            certificates.Insert(0, remaining[issuerIndex]);
            remaining.RemoveAt(issuerIndex);
        }

        // Anything not linked into the chain is still carried, in the caller's order, at the front.
        certificates.InsertRange(0, remaining);
        return certificates;
    }

    private static byte[] BuildSignatureAlgorithmIdentifier(HapDigestAlgorithm algorithm) => algorithm switch
    {
        HapDigestAlgorithm.EcdsaSha256 => Der.Sequence(Der.Oid("1.2.840.10045.4.3.2")),
        HapDigestAlgorithm.EcdsaSha384 => Der.Sequence(Der.Oid("1.2.840.10045.4.3.3")),
        HapDigestAlgorithm.EcdsaSha512 => Der.Sequence(Der.Oid("1.2.840.10045.4.3.4")),
        HapDigestAlgorithm.RsaPssSha256 => Der.Sequence(Der.Oid("1.2.840.113549.1.1.10"), Der.RsaPssParameters("2.16.840.1.101.3.4.2.1", 32)),
        HapDigestAlgorithm.RsaPssSha384 => Der.Sequence(Der.Oid("1.2.840.113549.1.1.10"), Der.RsaPssParameters("2.16.840.1.101.3.4.2.2", 48)),
        HapDigestAlgorithm.RsaPssSha512 => Der.Sequence(Der.Oid("1.2.840.113549.1.1.10"), Der.RsaPssParameters("2.16.840.1.101.3.4.2.3", 64)),
        _ => throw new InvalidOperationException(
            $"Unsupported HAP digest algorithm {algorithm}. 不支持的 HAP 摘要算法 {algorithm}。"),
    };

    private static (HashAlgorithmName Name, string DigestOid) DigestOf(HapDigestAlgorithm algorithm) => algorithm switch
    {
        HapDigestAlgorithm.EcdsaSha256 or HapDigestAlgorithm.RsaPssSha256 => (HashAlgorithmName.SHA256, "2.16.840.1.101.3.4.2.1"),
        HapDigestAlgorithm.EcdsaSha384 or HapDigestAlgorithm.RsaPssSha384 => (HashAlgorithmName.SHA384, "2.16.840.1.101.3.4.2.2"),
        HapDigestAlgorithm.EcdsaSha512 or HapDigestAlgorithm.RsaPssSha512 => (HashAlgorithmName.SHA512, "2.16.840.1.101.3.4.2.3"),
        _ => throw new InvalidOperationException(
            $"Unsupported HAP digest algorithm {algorithm}. 不支持的 HAP 摘要算法 {algorithm}。"),
    };

    // ── byte writers ───────────────────────────────────────────────────────

    private static void AppendUInt32(List<byte> output, uint value)
    {
        Span<byte> scratch = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(scratch, value);
        output.AddRange(scratch.ToArray());
    }

    private static void AppendUInt64(List<byte> output, ulong value)
    {
        Span<byte> scratch = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(scratch, value);
        output.AddRange(scratch.ToArray());
    }

    private static void AppendZeros(List<byte> output, int count)
    {
        for (var index = 0; index < count; index++)
        {
            output.Add(0);
        }
    }

    private static void AppendSegmentHeader(List<byte> output, uint type, long offset, int size)
    {
        AppendUInt32(output, type);
        AppendUInt32(output, checked((uint)offset));
        AppendUInt32(output, checked((uint)size));
    }

    /// <summary>Minimal DER primitives used by the code-sign CMS builder; SET OF values are DER-sorted while the
    /// certificate set preserves the official insertion order. <para>code-sign CMS 构建器使用的最小 DER 原语集；
    /// SET OF 按 DER 排序，而证书集合保持官方的插入顺序。</para></summary>
    private static class Der
    {
        internal static byte[] Combine(params byte[][] parts)
        {
            var total = 0;
            foreach (var part in parts)
            {
                checked
                {
                    total += part.Length;
                }
            }

            var output = new byte[total];
            var offset = 0;
            foreach (var part in parts)
            {
                part.CopyTo(output, offset);
                offset += part.Length;
            }

            return output;
        }

        internal static byte[] Tlv(byte tag, byte[] content)
        {
            var headerLength = 1 + LengthFieldSize(content.Length);
            var output = new byte[headerLength + content.Length];
            output[0] = tag;
            WriteLength(output.AsSpan(1), content.Length);
            content.CopyTo(output, headerLength);
            return output;
        }

        internal static byte[] Sequence(params byte[][] parts) => Tlv(0x30, Combine(parts));

        internal static byte[] SetOf(params byte[][] parts)
        {
            Array.Sort(parts, CompareEncoded);
            return Tlv(0x31, Combine(parts));
        }

        /// <summary>Re-encodes the concatenated members of a SET OF as the CMS implicit <c>[0]</c> constructed
        /// form. The wrapper tag replaces the SET tag, so the members are passed in already concatenated rather than
        /// as a finished SET OF; keeping the inner SET would nest an extra SEQUENCE family into the certificate set.
        /// <para>把 SET OF 的成员串接后重编码为 CMS 隐式 <c>[0]</c> 构造形态。外层标签替换 SET 标签，
        /// 因此成员必须是已串接的字节而不是完整的 SET OF —— 保留内层 SET 会让证书集多套一层 SEQUENCE。</para>
        /// </summary>
        internal static byte[] ImplicitConstructedSet(params byte[][] members) => Tlv(0xA0, Combine(members));

        /// <summary>Re-tags a complete SET OF as the CMS implicit <c>[0]</c> constructed form by swapping only the
        /// outer tag, keeping the original length field and members intact.
        /// <para>把完整的 SET OF 重标为 CMS 隐式 <c>[0]</c> 构造形态：仅替换外层标签，保留原长度字段与成员。</para>
        /// </summary>
        /// <param name="setOf">A complete SET OF value. <para>完整的 SET OF 值。</para></param>
        /// <returns>The same encoding with an <c>[0]</c> tag. <para>把标签换成 <c>[0]</c> 的同一编码。</para></returns>
        internal static byte[] RetagSetToContextZero(byte[] setOf)
        {
            ArgumentNullException.ThrowIfNull(setOf);
            var output = (byte[])setOf.Clone();
            output[0] = 0xA0;
            return output;
        }

        internal static byte[] ContextExplicit(byte tagNumber, byte[] content) => Tlv((byte)(0xA0 | tagNumber), content);

        /// <summary>Strips the outer SET tag of an encoded SET OF and returns its member concatenation, ready for
        /// the implicit <c>[0]</c> re-tagging. <para>剥离已编码 SET OF 的外层 SET 标签，返回成员串接字节，
        /// 供隐式 <c>[0]</c> 重标注使用。</para>
        /// </summary>
        /// <param name="setOf">A complete SET OF value. <para>完整的 SET OF 值。</para></param>
        /// <returns>The concatenated member bytes. <para>成员串接字节。</para></returns>
        internal static byte[] Members(byte[] setOf)
        {
            ArgumentNullException.ThrowIfNull(setOf);
            if (setOf.Length < 2)
            {
                throw new InvalidOperationException("The encoded SET OF is shorter than its header. 已编码 SET OF 短于头部。");
            }

            var lengthByte = setOf[1];
            var headerLength = (lengthByte & 0x80) == 0 ? 2 : 2 + (lengthByte & 0x7F);
            if (headerLength > setOf.Length)
            {
                throw new InvalidOperationException(
                    "The encoded SET OF declares a length header beyond its own size. 已编码 SET OF 的长度头超出自身大小。");
            }

            return setOf[headerLength..];
        }

        internal static byte[] Oid(string dotted)
        {
            var parts = dotted.Split('.');
            var body = new List<byte>(parts.Length)
            {
                (byte)(int.Parse(parts[0], CultureInfo.InvariantCulture) * 40
                    + int.Parse(parts[1], CultureInfo.InvariantCulture)),
            };
            for (var index = 2; index < parts.Length; index++)
            {
                WriteBase128(body, ulong.Parse(parts[index], CultureInfo.InvariantCulture));
            }

            return Tlv(0x06, [.. body]);
        }

        internal static byte[] Integer(long value)
        {
            if (value == 0)
            {
                return [0x02, 0x01, 0x00];
            }

            Span<byte> scratch = stackalloc byte[9];
            var length = 0;
            var remaining = (ulong)value;
            while (remaining != 0)
            {
                scratch[length++] = (byte)(remaining & 0xFF);
                remaining >>= 8;
            }

            var body = new List<byte>(length + 1);
            if ((scratch[length - 1] & 0x80) != 0)
            {
                body.Add(0);
            }

            for (var index = length - 1; index >= 0; index--)
            {
                body.Add(scratch[index]);
            }

            return Tlv(0x02, [.. body]);
        }

        internal static byte[] IntegerFromUnsignedBigEndian(ReadOnlySpan<byte> magnitude)
        {
            var start = 0;
            while (start < magnitude.Length && magnitude[start] == 0)
            {
                start++;
            }

            var body = magnitude[start..].ToArray();
            if (body.Length == 0)
            {
                body = [0];
            }
            else if ((body[0] & 0x80) != 0)
            {
                body = [0, .. body];
            }

            return Tlv(0x02, body);
        }

        internal static byte[] OctetString(byte[] content) => Tlv(0x04, content);

        internal static byte[] Utf8String(string value) => Tlv(0x0C, Encoding.UTF8.GetBytes(value));

        internal static byte[] UtcTime(DateTime time)
        {
            var universal = time.ToUniversalTime();
            if (universal.Year is < 1950 or >= 2050)
            {
                return Tlv(0x18, Encoding.ASCII.GetBytes(universal.ToString("yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));
            }

            return Tlv(0x17, Encoding.ASCII.GetBytes(universal.ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture)));
        }

        internal static byte[] RsaPssParameters(string digestOid, int saltLength)
        {
            var hash = Sequence(Oid(digestOid), Tlv(0x05, []));
            return Sequence(
                hash,
                Sequence(Oid("1.2.840.113549.1.1.8"), hash),
                Integer(saltLength),
                Integer(1));
        }

        private static int CompareEncoded(byte[] left, byte[] right)
        {
            var common = Math.Min(left.Length, right.Length);
            for (var index = 0; index < common; index++)
            {
                var difference = left[index].CompareTo(right[index]);
                if (difference != 0)
                {
                    return difference;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        private static int LengthFieldSize(int length) => length switch
        {
            < 0x80 => 1,
            <= 0xFF => 2,
            <= 0xFFFF => 3,
            <= 0xFFFFFF => 4,
            _ => 5,
        };

        private static void WriteLength(Span<byte> output, int length)
        {
            switch (LengthFieldSize(length))
            {
                case 1:
                    output[0] = (byte)length;
                    break;
                case 2:
                    output[0] = 0x81;
                    output[1] = (byte)length;
                    break;
                case 3:
                    output[0] = 0x82;
                    output[1] = (byte)(length >> 8);
                    output[2] = (byte)length;
                    break;
                case 4:
                    output[0] = 0x83;
                    output[1] = (byte)(length >> 16);
                    output[2] = (byte)(length >> 8);
                    output[3] = (byte)length;
                    break;
                default:
                    output[0] = 0x84;
                    output[1] = (byte)(length >> 24);
                    output[2] = (byte)(length >> 16);
                    output[3] = (byte)(length >> 8);
                    output[4] = (byte)length;
                    break;
            }
        }

        private static void WriteBase128(List<byte> output, ulong value)
        {
            Span<byte> scratch = stackalloc byte[10];
            var length = 0;
            scratch[length++] = (byte)(value & 0x7F);
            value >>= 7;
            while (value != 0)
            {
                scratch[length++] = (byte)((value & 0x7F) | 0x80);
                value >>= 7;
            }

            for (var index = length - 1; index >= 0; index--)
            {
                output.Add(scratch[index]);
            }
        }
    }
}