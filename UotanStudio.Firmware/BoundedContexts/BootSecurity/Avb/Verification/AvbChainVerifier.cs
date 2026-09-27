using System.Security.Cryptography;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Avb.Verification;

/// <summary>Verifies an explicitly mapped AVB graph, including signer delegation and partition content.
/// <para>验证显式映射的 AVB 图，包括签名者委托与分区内容。</para></summary>
/// <remarks>Reads files only; limits traversal to 64 metadata images, depth 16, 16384 descriptors and 64 MiB of loaded metadata.
/// Android Sparse inputs are checksum-checked and limited to 262144 chunks and 1 TiB of logical bytes.
/// Declared FEC parity is checked separately from content; hardware acceptance remains outside this result.
/// <para>仅读取文件；遍历限制为 64 个元数据镜像、深度 16、16384 个描述符及累计 64 MiB 元数据。
/// Android Sparse 输入检查校验和，限制为 262144 个块和 1 TiB 逻辑字节。声明的 FEC 校验码会独立验证；结果不代表硬件接受。</para></remarks>
public static partial class AvbChainVerifier
{
    /// <summary>Runs cancellable offline verification using exact partition-map identities.
    /// <para>使用精确分区映射身份运行可取消的离线验证。</para></summary>
    /// <param name="options">Partition mapping, external trust and optional rollback policy. <para>分区映射、外部信任与可选回滚策略。</para></param>
    /// <param name="cancellationToken">Cancels file reads and graph traversal. <para>取消文件读取与图遍历。</para></param>
    /// <returns>Completed checks and explicit failures; absent trust and rollback inputs remain unverified.
    /// <para>已完成的检查与明确失败；未提供的信任和回滚输入保持未验证。</para></returns>
    /// <exception cref="ArgumentNullException">Options or required mapping is null. <para>选项或必需映射为 null。</para></exception>
    /// <exception cref="ArgumentException">A root name, suffix, map size or key size is invalid.
    /// <para>根名称、后缀、映射大小或密钥大小无效。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested. <para>请求了取消。</para></exception>
    public static async Task<AvbChainVerifyResult> VerifyAsync(AvbChainVerifyOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.PartitionPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.RootPartitionName);
        ArgumentNullException.ThrowIfNull(options.SlotSuffix);
        if (options.PartitionPaths.Count > 4096 || options.TrustedRootPublicKey is { Length: > 2056 }
            || options.MinimumRollbackIndexes is { Count: > 32 }
            || options.MinimumRollbackIndexes?.Keys.Any(slot => slot >= 32) == true)
        {
            throw new ArgumentException("The partition map, rollback map or public key exceeds its limit.", nameof(options));
        }
        var suffix = options.SlotSuffix;
        if (suffix.Length > 0 && (suffix.Length is < 2 or > 16 || suffix[0] != '_' || suffix.AsSpan(1).ContainsAnyExcept(
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")))
        {
            throw new ArgumentException("Invalid AVB slot suffix.", nameof(options));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var context = new Context(options, cancellationToken);
        await context.VisitAsync(options.RootPartitionName, context.RootKey, null, 0).ConfigureAwait(false);
        return context.Result();
    }

    private sealed partial class Context
    {
        private readonly Dictionary<string, string> _paths;
        private readonly Dictionary<uint, ulong>? _rollback;
        private readonly string _suffix;
        private readonly bool _inspectNested;
        private readonly CancellationToken _token;
        private readonly HashSet<string> _activePaths = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        private readonly HashSet<uint> _rollbackLocations = [];
        private readonly List<AvbChainImageResult> _images = [];
        private readonly List<AvbChainPartitionResult> _partitions = [];
        private readonly List<string> _errors = [];
        private bool _nested;
        private bool? _rootTrusted;
        private uint? _rootFlags;
        private long _metadataBytes;
        private int _descriptorCount;

        internal Context(AvbChainVerifyOptions options, CancellationToken token)
        {
            _paths = new Dictionary<string, string>(options.PartitionPaths, StringComparer.Ordinal);
            _rollback = options.MinimumRollbackIndexes is null ? null : new Dictionary<uint, ulong>(options.MinimumRollbackIndexes);
            _suffix = options.SlotSuffix;
            _inspectNested = options.InspectNestedChains;
            RootKey = options.TrustedRootPublicKey?.ToArray();
            _rootTrusted = RootKey is null ? null : false;
            _token = token;
        }

        internal byte[]? RootKey { get; }

        internal AvbChainVerifyResult Result() => new()
        {
            Success = _errors.Count == 0,
            RootKeyIsTrusted = _rootTrusted,
            RollbackIndexesAreValid = _rollback is null ? null : _errors.Count == 0 && _images.All(image => image.RollbackIndexIsValid == true),
            ContainsNestedChains = _nested,
            RootFlags = _rootFlags,
            Images = _images.ToArray(),
            Partitions = _partitions.ToArray(),
            Errors = _errors.ToArray()
        };

        internal async Task<bool> VisitAsync(string name, byte[]? expectedKey, uint? parentSlot, int depth)
        {
            _token.ThrowIfCancellationRequested();
            if (depth >= 16 || _images.Count >= 64)
            {
                return Fail(name, "AVB graph depth or metadata image count exceeds its limit.");
            }
            var node = new AvbChainImageResult { PartitionName = name };
            _images.Add(node);
            string? path = null;
            var active = false;
            try
            {
                path = ResolvePath(name);
                active = _activePaths.Add(path);
                if (!active)
                {
                    return Fail(name, "A cycle or active file alias was found in the AVB graph.");
                }
                await using var image = await AvbImageSource.OpenAsync(path, _token).ConfigureAwait(false);
                var stream = image.Stream;
                var loaded = await ReadMetadataAsync(stream).ConfigureAwait(false);
                var header = loaded.Header;
                node.Header = header;
                if (depth == 0)
                {
                    _rootFlags = header.Flags;
                }
                if ((header.Flags & ~3U) != 0 || depth != 0 && header.Flags != 0)
                {
                    return Fail(name, "Unsupported root flags or nonzero flags in a chained VBMeta image.");
                }
                var signature = AvbSignatureVerifier.Verify(loaded.Bytes);
                node.SignatureIsValid = signature.HashIsValid && signature.SignatureIsValid;
                if (!node.SignatureIsValid)
                {
                    return Fail(name, signature.ErrorMessage ?? "VBMeta hash or signature verification failed.");
                }
                if (expectedKey is not null)
                {
                    var keyOffset = checked(AvbVBMetaImageHeader.Size + (int)header.AuthenticationDataBlockSize + (int)header.PublicKeyOffset);
                    node.ExpectedKeyMatches = CryptographicOperations.FixedTimeEquals(expectedKey,
                        loaded.Bytes.AsSpan(keyOffset, (int)header.PublicKeySize));
                    if (node.ExpectedKeyMatches != true)
                    {
                        return Fail(name, "The embedded signer does not match the expected root or chain public key.");
                    }
                    if (depth == 0)
                    {
                        _rootTrusted = true;
                    }
                }
                var slot = parentSlot ?? header.RollbackIndexLocation;
                node.RollbackIndexLocation = slot;
                if (slot >= 32 || depth != 0 && slot == 0 || !_rollbackLocations.Add(slot))
                {
                    return Fail(name, "The rollback slot is invalid or is reused by another metadata image.");
                }
                if (_rollback is not null)
                {
                    node.RollbackIndexIsValid = _rollback.TryGetValue(slot, out var minimum) && header.RollbackIndex >= minimum;
                    if (node.RollbackIndexIsValid != true)
                    {
                        return Fail(name, "The rollback slot is missing from the supplied policy or the image counter is too old.");
                    }
                }
                _descriptorCount = checked(_descriptorCount + signature.Descriptors.Count);
                if (_descriptorCount > 16384)
                {
                    return Fail(name, "The AVB graph exceeds 16384 descriptors.");
                }
                foreach (var descriptor in signature.Descriptors)
                {
                    _token.ThrowIfCancellationRequested();
                    switch (descriptor.Tag)
                    {
                        case 0:
                        case 3:
                            break;
                        case 1:
                        case 2:
                            {
                                var target = ResolveName(descriptor, name);
                                var targetPath = ResolvePath(target);
                                AvbPartitionVerifyResult content;
                                if (string.Equals(targetPath, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                                {
                                    content = await AvbPartitionVerifier.VerifyWithFecAsync(stream, descriptor, _token).ConfigureAwait(false);
                                }
                                else
                                {
                                    await using var partition = await AvbImageSource.OpenAsync(targetPath, _token).ConfigureAwait(false);
                                    content = await AvbPartitionVerifier.VerifyWithFecAsync(partition.Stream, descriptor, _token).ConfigureAwait(false);
                                }
                                _partitions.Add(new AvbChainPartitionResult { MetadataPartition = name, PartitionName = target, Content = content });
                                if (!content.ContentIsValid || content.FecIsValid == false)
                                {
                                    return Fail(name, $"{target}: {content.ErrorMessage}");
                                }
                                break;
                            }
                        case 4:
                            {
                                if (depth > 0)
                                {
                                    _nested = true;
                                    if (!_inspectNested)
                                    {
                                        return Fail(name, "libavb permits chain descriptors only in the root VBMeta image.");
                                    }
                                }
                                if (string.IsNullOrEmpty(descriptor.PartitionName) || descriptor.PublicKey is not { Length: > 0 }
                                    || (descriptor.Flags & ~1U) != 0 || descriptor.RollbackIndexLocation is 0 or >= 32)
                                {
                                    return Fail(name, "Invalid chain partition name, key, flags or rollback slot.");
                                }
                                if (!await VisitAsync(ResolveName(descriptor, name), descriptor.PublicKey, descriptor.RollbackIndexLocation, depth + 1).ConfigureAwait(false))
                                {
                                    return false;
                                }
                                break;
                            }
                        default:
                            return Fail(name, $"Descriptor tag {descriptor.Tag} is not supported for graph verification.");
                    }
                }
                _token.ThrowIfCancellationRequested();
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CryptographicException or ArgumentException or OverflowException)
            {
                return Fail(name, ex.Message);
            }
            finally
            {
                if (active)
                {
                    _activePaths.Remove(path!);
                }
            }
        }

        private bool Fail(string name, string message)
        {
            _errors.Add(name + ": " + message);
            return false;
        }

        private string ResolveName(AvbDescriptorInfo descriptor, string containing)
            => string.IsNullOrEmpty(descriptor.PartitionName) ? containing
                : descriptor.PartitionName + ((descriptor.Flags & 1) == 0 ? _suffix : string.Empty);

        private string ResolvePath(string name)
        {
            if (!_paths.TryGetValue(name, out var path) || string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidDataException($"No image was explicitly mapped for partition '{name}'.");
            }
            return Path.GetFullPath(path);
        }

    }
}