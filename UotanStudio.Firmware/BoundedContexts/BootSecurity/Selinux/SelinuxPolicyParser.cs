using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux.Models;
using UotanStudio.Firmware.SharedKernel.Streams;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux;

/// <summary>
/// Parses the bounded SELinux policy header and the first libsepol body sections without enumerating rules.
/// <para>解析有界 SELinux 策略头部与 libsepol 主体的前几个 section，不枚举规则。</para>
/// </summary>
/// <remarks>libsepol stores no section index table and reads the body as one sequential little-endian
/// stream, so this parser consumes only the leading prefix of that order and reports every remaining
/// byte as one phase-limited span.
/// <para>libsepol 不保存 section 索引表，而是将主体作为单条顺序小端流读取，因此本解析器只消费该顺序
/// 的前缀，并将全部剩余字节报告为一个 phase 受限区间。</para></remarks>
public static class SelinuxPolicyParser
{
    /// <summary>SELinux kernel policy magic defined by Linux and libsepol.
    /// <para>Linux 与 libsepol 定义的 SELinux 内核策略 magic。</para></summary>
    public const uint SelinuxMagic = 0xF97CFF8C;

    /// <summary>SELinux module policy magic used by libsepol.
    /// <para>libsepol 使用的 SELinux 模块策略 magic。</para></summary>
    public const uint SelinuxModuleMagic = 0xF97CFF8D;

    private const uint MaxPolicyVersion = 33;
    private const uint MaxModulePolicyVersion = 21;
    private const uint MaxTableCount = 1024;
    private const uint MaxIdentifierLength = 32;
    private const long MaxInputBytes = 64L * 1024 * 1024;
    private const uint MlsFlag = 1;

    /// <summary>libsepol <c>POLICYDB_VERSION_POLCAP</c>: the first kernel version serializing
    /// <c>policycaps</c>. <para>libsepol <c>POLICYDB_VERSION_POLCAP</c>：开始序列化 <c>policycaps</c>
    /// 的最小内核版本。</para></summary>
    private const uint KernelPolicyCapabilitiesVersion = 22;

    /// <summary>libsepol <c>POLICYDB_VERSION_PERMISSIVE</c>: the first kernel version serializing
    /// <c>permissive_map</c>. <para>libsepol <c>POLICYDB_VERSION_PERMISSIVE</c>：开始序列化
    /// <c>permissive_map</c> 的最小内核版本。</para></summary>
    private const uint KernelPermissiveMapVersion = 23;

    /// <summary>libsepol <c>MOD_POLICYDB_VERSION_POLCAP</c>: the first module version serializing
    /// <c>policycaps</c> for both base modules and modules.
    /// <para>libsepol <c>MOD_POLICYDB_VERSION_POLCAP</c>：基础模块与普通模块开始序列化
    /// <c>policycaps</c> 的最小模块版本。</para></summary>
    private const uint ModulePolicyCapabilitiesVersion = 7;

    /// <summary>Parser bound for the module <c>base_name</c> and <c>version</c> strings. libsepol only
    /// rejects zero and saturated lengths here, so this bound is imposed by this reader rather than by
    /// the format. <para>模块 <c>base_name</c> 与 <c>version</c> 字符串的解析器上限。libsepol 在此仅拒绝
    /// 零值与饱和值，因此该上限由本读取器施加而非格式本身。</para></summary>
    private const uint MaxModuleStringBytes = 1024;

    /// <summary>libsepol <c>MAPSIZE</c>: bits covered by one ebitmap node map word.
    /// <para>libsepol <c>MAPSIZE</c>：单个 ebitmap 节点映射字覆盖的位数。</para></summary>
    private const uint BitmapMapSize = 64;

    /// <summary>Parser bound on the node count declared by one ebitmap, checked before allocation.
    /// <para>单个 ebitmap 声明节点数的解析器上限，在分配前检查。</para></summary>
    private const uint MaxBitmapNodes = 4096;

    /// <summary>Bytes per serialized ebitmap node: a little-endian <c>u32</c> start bit and
    /// <c>u64</c> map. <para>序列化 ebitmap 节点的字节数：小端 <c>u32</c> 起始位与 <c>u64</c> 映射。</para></summary>
    private const int BitmapNodeBytes = sizeof(uint) + sizeof(ulong);

    /// <summary>Name of the single span covering everything the phase-one cursor did not parse.
    /// <para>覆盖 phase-one 游标未解析内容的唯一区间名称。</para></summary>
    private const string PhaseLimitedTailName = "symbol-tables-and-later";

    /// <summary>Message reported when the policy header ends before its fixed fields are complete.
    /// <para>策略头部在固定字段读完前提前的提示信息。</para></summary>
    private const string HeaderTruncatedMessage = "SELinux policy ended before its metadata header was complete.";

    /// <summary>Message reported when the policy body ends inside a section.
    /// <para>策略 body 在 section 内部结束时的提示信息。</para></summary>
    private const string BodyTruncatedMessage = "SELinux policy body ended inside a section.";

    /// <summary>
    /// Reads policy header and bounded section metadata from a file without enumerating or modifying rules.
    /// <para>从文件读取策略头部与有界 section 元数据，不枚举或修改规则。</para>
    /// </summary>
    /// <param name="path">Policy file path. <para>策略文件路径。</para></param>
    /// <param name="cancellationToken">Cancellation token checked during bounded reads.
    /// <para>在有界读取期间检查的取消令牌。</para></param>
    /// <returns>Parsed read-only metadata. <para>解析出的只读元数据。</para></returns>
    public static async Task<SelinuxPolicyMetadata> ReadMetadataAsync(
        string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxInputBytes)
            throw new InvalidDataException("SELinux policy exceeds the bounded input limit.");
        return await ReadMetadataAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads policy header metadata from a seekable stream.
    /// <para>从可定位流读取策略头部元数据。</para>
    /// </summary>
    /// <param name="stream">Seekable readable policy stream. <para>可定位且可读的策略流。</para></param>
    /// <param name="cancellationToken">Cancellation token checked before every read.
    /// <para>每次读取前检查的取消令牌。</para></param>
    /// <returns>Parsed read-only metadata. <para>解析出的只读元数据。</para></returns>
    public static async Task<SelinuxPolicyMetadata> ReadMetadataAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("The policy stream must be readable and seekable.", nameof(stream));
        if (stream.Length > MaxInputBytes)
            throw new InvalidDataException("SELinux policy exceeds the bounded input limit.");

        stream.Position = 0;
        var magic = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (magic is not SelinuxMagic and not SelinuxModuleMagic)
            throw new InvalidDataException($"Unsupported SELinux policy magic 0x{magic:X8}.");

        var identifierLength = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (identifierLength is 0 or > MaxIdentifierLength)
            throw new InvalidDataException("SELinux policy identifier length is outside the supported bound.");

        var identifierBytes = await ReadExactAsync(stream, checked((int)identifierLength), cancellationToken)
            .ConfigureAwait(false);
        var identifier = Encoding.ASCII.GetString(identifierBytes);
        if (identifier.Length == 0 || identifier.Any(c => c is < (char)32 or > (char)126))
            throw new InvalidDataException("SELinux policy identifier is not printable ASCII.");

        var first = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        var policyType = magic == SelinuxMagic ? "kernel" : first switch
        {
            1 => "base-module",
            2 => "module",
            _ => throw new InvalidDataException("Unsupported SELinux module policy type.")
        };
        var version = magic == SelinuxMagic
            ? first
            : await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (version is 0 or > MaxPolicyVersion ||
            (magic == SelinuxModuleMagic && version > MaxModulePolicyVersion))
            throw new InvalidDataException($"Unsupported SELinux policy version {version}.");

        var configuration = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        var symbolTableCount = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        var objectContextCount = await ReadUInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (symbolTableCount > MaxTableCount || objectContextCount > MaxTableCount)
            throw new InvalidDataException("SELinux policy section count exceeds the supported bound.");

        if (identifier != "SE Linux" && identifier != "XenFlask" && identifier != "SE Linux Module")
            throw new InvalidDataException($"Unsupported SELinux policy target '{identifier}'.");

        if (identifier == "SE Linux")
        {
            var expected = GetExpectedSectionCounts(magic, policyType, version);
            if (symbolTableCount != expected.SymbolTables ||
                objectContextCount != expected.ObjectContexts)
            {
                throw new InvalidDataException(
                    $"SELinux policy section counts ({symbolTableCount},{objectContextCount}) " +
                    $"do not match the policy compatibility layout ({expected.SymbolTables},{expected.ObjectContexts}).");
            }
        }

        var bodyOffset = stream.Position;
        var bodyLength = checked(stream.Length - bodyOffset);
        if (bodyLength <= 0)
            throw new InvalidDataException("SELinux policy has no serialized body.");

        await using var body = new BoundedReadStream(stream, bodyOffset, bodyLength, leaveOpen: true);
        var (sections, bodyFullyConsumed) = await ReadPhaseSectionsAsync(
            body, bodyOffset, magic, policyType, version, cancellationToken).ConfigureAwait(false);

        return new SelinuxPolicyMetadata
        {
            Magic = magic,
            PolicyType = policyType,
            Version = version,
            Target = identifier,
            Configuration = configuration,
            MlsEnabled = (configuration & MlsFlag) != 0,
            SymbolTableCount = symbolTableCount,
            ObjectContextTableCount = objectContextCount,
            BodyOffset = bodyOffset,
            BodyLength = bodyLength,
            Sections = sections,
            BodyFullyConsumed = bodyFullyConsumed
        };
    }

    /// <summary>
    /// Consumes the leading sections libsepol reads before the symbol tables and reports every
    /// remaining byte as one phase-limited span. Section failures are recorded instead of thrown, so a
    /// truncated or malformed body still yields header metadata.
    /// <para>消费 libsepol 在符号表之前读取的前导 section，并把全部剩余字节报告为一个 phase 受限区间。
    /// section 失败会被记录而非抛出，因此截断或格式错误的 body 仍会返回头部元数据。</para>
    /// </summary>
    private static async Task<(IReadOnlyList<SelinuxPolicySection> Sections, bool BodyFullyConsumed)> ReadPhaseSectionsAsync(
        BoundedReadStream body, long bodyOffset, uint magic, string policyType, uint version,
        CancellationToken cancellationToken)
    {
        var sections = new List<SelinuxPolicySection>();
        var stopped = false;

        if (magic == SelinuxModuleMagic && policyType == "module")
        {
            stopped = !await TryConsumeSectionStringAsync(
                sections, body, bodyOffset, "module-name", cancellationToken).ConfigureAwait(false);
            if (!stopped)
            {
                stopped = !await TryConsumeSectionStringAsync(
                    sections, body, bodyOffset, "module-version", cancellationToken).ConfigureAwait(false);
            }
        }

        if (!stopped)
        {
            var declaresPolicyCapabilities = magic == SelinuxMagic
                ? version >= KernelPolicyCapabilitiesVersion
                : version >= ModulePolicyCapabilitiesVersion;
            if (declaresPolicyCapabilities)
            {
                stopped = !await TryConsumeBitmapAsync(
                    sections, body, bodyOffset, "policycaps", cancellationToken).ConfigureAwait(false);
            }
        }

        if (!stopped && magic == SelinuxMagic && version >= KernelPermissiveMapVersion)
        {
            stopped = !await TryConsumeBitmapAsync(
                sections, body, bodyOffset, "permissive-map", cancellationToken).ConfigureAwait(false);
        }

        var bodyEnd = checked(bodyOffset + body.Length);
        var cursorEnd = checked(bodyOffset + body.Position);
        sections.Add(new SelinuxPolicySection
        {
            Name = PhaseLimitedTailName,
            StartOffset = cursorEnd,
            EndOffset = cursorEnd < bodyEnd ? bodyEnd : null,
            Status = SelinuxSectionStatus.PhaseLimited
        });
        return (sections, !stopped && cursorEnd == bodyEnd);
    }

    /// <summary>
    /// Reads one length-prefixed module string and records the span it covers.
    /// <para>读取一个带长度前缀的模块字符串并记录其覆盖的区间。</para>
    /// </summary>
    private static async Task<bool> TryConsumeSectionStringAsync(
        List<SelinuxPolicySection> sections, BoundedReadStream body, long bodyOffset, string name,
        CancellationToken cancellationToken)
    {
        var startOffset = checked(bodyOffset + body.Position);
        uint? declaredLength = null;
        try
        {
            declaredLength = await ReadUInt32Async(body, BodyTruncatedMessage, cancellationToken)
                .ConfigureAwait(false);
            if (declaredLength is 0 or > MaxModuleStringBytes)
            {
                throw new InvalidDataException(
                    $"SELinux policy section '{name}' declares string length {declaredLength} " +
                    $"outside the supported bound of {MaxModuleStringBytes}.");
            }
            _ = await ReadExactAsync(body, checked((int)declaredLength.Value), BodyTruncatedMessage, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (EndOfStreamException)
        {
            AddStoppedSection(sections, name, startOffset, bodyOffset, body, declaredLength, SelinuxSectionStatus.Truncated);
            return false;
        }
        catch (InvalidDataException)
        {
            AddStoppedSection(sections, name, startOffset, bodyOffset, body, declaredLength, SelinuxSectionStatus.Rejected);
            return false;
        }

        sections.Add(CreateSection(name, startOffset, bodyOffset, body, declaredLength, SelinuxSectionStatus.Consumed));
        return true;
    }

    /// <summary>
    /// Reads one serialized ebitmap and records the span it covers. The node count is bounded before
    /// any node buffer is allocated.
    /// <para>读取一个序列化 ebitmap 并记录其覆盖的区间。节点数在分配任何节点缓冲区之前已被限制。</para>
    /// </summary>
    private static async Task<bool> TryConsumeBitmapAsync(
        List<SelinuxPolicySection> sections, BoundedReadStream body, long bodyOffset, string name,
        CancellationToken cancellationToken)
    {
        var startOffset = checked(bodyOffset + body.Position);
        uint? declaredNodes = null;
        try
        {
            var mapSize = await ReadUInt32Async(body, BodyTruncatedMessage, cancellationToken).ConfigureAwait(false);
            var highBit = await ReadUInt32Async(body, BodyTruncatedMessage, cancellationToken).ConfigureAwait(false);
            declaredNodes = await ReadUInt32Async(body, BodyTruncatedMessage, cancellationToken).ConfigureAwait(false);
            if (mapSize != BitmapMapSize)
            {
                throw new InvalidDataException(
                    $"SELinux policy section '{name}' declares ebitmap map size {mapSize} " +
                    $"instead of {BitmapMapSize}.");
            }
            if (highBit != 0)
            {
                if ((highBit % BitmapMapSize) != 0)
                {
                    throw new InvalidDataException(
                        $"SELinux policy section '{name}' declares ebitmap high bit {highBit} " +
                        $"that is not a multiple of {BitmapMapSize}.");
                }
                var nodeCount = declaredNodes.GetValueOrDefault();
                if (nodeCount is 0 or > MaxBitmapNodes)
                {
                    throw new InvalidDataException(
                        $"SELinux policy section '{name}' declares ebitmap node count {nodeCount} " +
                        $"outside the supported bound of {MaxBitmapNodes}.");
                }
                var nodes = await ReadExactAsync(
                    body, checked((int)nodeCount * BitmapNodeBytes), BodyTruncatedMessage, cancellationToken)
                    .ConfigureAwait(false);
                var lastStartBit = ValidateBitmapNodes(name, highBit, nodes);
                if (checked(lastStartBit + BitmapMapSize) != highBit)
                {
                    throw new InvalidDataException(
                        $"SELinux policy section '{name}' declares ebitmap high bit {highBit} " +
                        $"that does not follow start bit {lastStartBit}.");
                }
            }
        }
        catch (EndOfStreamException)
        {
            AddStoppedSection(sections, name, startOffset, bodyOffset, body, declaredNodes, SelinuxSectionStatus.Truncated);
            return false;
        }
        catch (InvalidDataException)
        {
            AddStoppedSection(sections, name, startOffset, bodyOffset, body, declaredNodes, SelinuxSectionStatus.Rejected);
            return false;
        }

        sections.Add(CreateSection(name, startOffset, bodyOffset, body, declaredNodes, SelinuxSectionStatus.Consumed));
        return true;
    }

    /// <summary>
    /// Checks every serialized ebitmap node against the layout libsepol accepts and returns the start
    /// bit of the last node.
    /// <para>按 libsepol 接受的布局检查全部序列化 ebitmap 节点，并返回最后一个节点的起始位。</para>
    /// </summary>
    private static uint ValidateBitmapNodes(string name, uint highBit, ReadOnlySpan<byte> nodes)
    {
        var lastStartBit = 0u;
        var hasPrevious = false;
        for (var offset = 0; offset < nodes.Length; offset += BitmapNodeBytes)
        {
            var startBit = BinaryPrimitives.ReadUInt32LittleEndian(nodes[offset..]);
            var map = BinaryPrimitives.ReadUInt64LittleEndian(nodes[(offset + sizeof(uint))..]);
            if ((startBit % BitmapMapSize) != 0 || startBit > checked(highBit - BitmapMapSize))
            {
                throw new InvalidDataException(
                    $"SELinux policy section '{name}' declares ebitmap start bit {startBit} " +
                    $"for high bit {highBit}.");
            }
            if (map == 0)
            {
                throw new InvalidDataException(
                    $"SELinux policy section '{name}' declares an empty ebitmap map at start bit {startBit}.");
            }
            if (hasPrevious && startBit <= lastStartBit)
            {
                throw new InvalidDataException(
                    $"SELinux policy section '{name}' declares ebitmap start bit {startBit} " +
                    $"after start bit {lastStartBit}.");
            }
            lastStartBit = startBit;
            hasPrevious = true;
        }
        return lastStartBit;
    }

    private static SelinuxPolicySection CreateSection(
        string name, long startOffset, long bodyOffset, BoundedReadStream body, uint? declaredCount,
        SelinuxSectionStatus status)
    {
        return new SelinuxPolicySection
        {
            Name = name,
            StartOffset = startOffset,
            EndOffset = checked(bodyOffset + body.Position),
            DeclaredCount = declaredCount,
            Status = status
        };
    }

    private static void AddStoppedSection(
        List<SelinuxPolicySection> sections, string name, long startOffset, long bodyOffset,
        BoundedReadStream body, uint? declaredCount, SelinuxSectionStatus status)
    {
        var endOffset = checked(bodyOffset + body.Position);
        sections.Add(new SelinuxPolicySection
        {
            Name = name,
            StartOffset = startOffset,
            EndOffset = endOffset > startOffset ? endOffset : null,
            DeclaredCount = declaredCount,
            Status = status
        });
    }

    private static (uint SymbolTables, uint ObjectContexts) GetExpectedSectionCounts(
        uint magic, string policyType, uint version)
    {
        if (magic == SelinuxMagic)
        {
            var symbolTables = version switch
            {
                15 => 5u,
                >= 16 and <= 18 => 6u,
                _ => 8u
            };
            var objectContexts = version switch
            {
                <= 16 => 6u,
                <= 30 => 7u,
                _ => 9u
            };
            return (symbolTables, objectContexts);
        }

        if (policyType == "base-module")
            return (8u, version <= 18 ? 7u : 9u);

        return (8u, 0u);
    }

    private static async Task<uint> ReadUInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        return await ReadUInt32Async(stream, HeaderTruncatedMessage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<uint> ReadUInt32Async(
        Stream stream, string endMessage, CancellationToken cancellationToken)
    {
        var bytes = await ReadExactAsync(stream, sizeof(uint), endMessage, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream, int count, CancellationToken cancellationToken)
    {
        return await ReadExactAsync(stream, count, HeaderTruncatedMessage, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream, int count, string endMessage, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException(endMessage);
            offset += read;
        }
        return bytes;
    }
}