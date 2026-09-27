using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux.Models;

namespace UotanStudio.Firmware.BoundedContexts.BootSecurity.Selinux;

/// <summary>
/// Parses only the bounded SELinux policy database header and section-count metadata.
/// <para>仅解析有界 SELinux policy database 头部与 section 数量元数据。</para>
/// </summary>
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

    /// <summary>
    /// Reads policy header metadata from a file without parsing or modifying rules.
    /// <para>从文件读取策略头部元数据，不解析或修改规则。</para>
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

        var bodyLength = checked(stream.Length - stream.Position);
        if (bodyLength <= 0)
            throw new InvalidDataException("SELinux policy has no serialized body.");

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
            BodyOffset = stream.Position,
            BodyLength = bodyLength
        };
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
        var bytes = await ReadExactAsync(stream, sizeof(uint), cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static async Task<byte[]> ReadExactAsync(
        Stream stream, int count, CancellationToken cancellationToken)
    {
        var bytes = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(offset, count - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
                throw new EndOfStreamException("SELinux policy ended before its metadata header was complete.");
            offset += read;
        }
        return bytes;
    }
}
