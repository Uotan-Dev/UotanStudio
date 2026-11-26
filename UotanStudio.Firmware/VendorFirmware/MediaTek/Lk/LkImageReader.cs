using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using UotanStudio.Hash.Digests;

namespace UotanStudio.Firmware.VendorFirmware.MediaTek.Lk;

/// <summary>
/// Bounded reader for the MediaTek LK image chain: 512-byte headers carrying a 32-byte partition name,
/// a declared payload length and the address/alignment words, terminated by <c>image_list_end</c>.
/// <para>MediaTek LK 镜像链的有界读取器：512 字节头部携带 32 字节分区名、声明的负载长度与地址/对齐字段，
/// 由 <c>image_list_end</c> 终止。</para>
/// </summary>
public static class LkImageReader
{
    private const int DataSizeOffset = 0x04;
    private const int NameOffset = 0x08;
    private const int MemoryAddressOffset = 0x28;
    private const int AddressingModeOffset = 0x2C;
    private const int ExtendedMagicOffset = 0x30;
    private const int HeaderVersionOffset = 0x38;
    private const int ImageTypeOffset = 0x3C;
    private const int IsLastOffset = 0x40;
    private const int AlignmentOffset = 0x44;
    private const int DataSizeExtendedOffset = 0x48;
    private const int MemoryAddressExtendedOffset = 0x4C;

    /// <summary>
    /// Determines whether the supplied header bytes start a MediaTek LK image.
    /// <para>判断给定头部字节是否启动一个 MediaTek LK 镜像。</para>
    /// </summary>
    /// <param name="header">At least four leading bytes of the candidate.<para>候选文件至少四个前导字节。</para></param>
    /// <returns><see langword="true"/> when the little-endian magic matches.<para>小端魔数匹配时为 <see langword="true"/>。</para></returns>
    public static bool IsLkImage(ReadOnlySpan<byte> header) =>
        header.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(header) == LkImageConstants.Magic;

    /// <summary>
    /// Parses the entry chain of an LK image without loading payloads into memory.
    /// <para>解析 LK 镜像的条目链，不把负载载入内存。</para>
    /// </summary>
    /// <param name="path">Image path.<para>镜像路径。</para></param>
    /// <param name="cancellationToken">Cancels the scan.<para>取消本次扫描。</para></param>
    /// <returns>The parsed image.<para>解析后的镜像。</para></returns>
    /// <exception cref="ArgumentException"><paramref name="path"/> is blank.<para><paramref name="path"/> 为空白。</para></exception>
    /// <exception cref="FileNotFoundException">The image does not exist.<para>镜像不存在。</para></exception>
    /// <exception cref="InvalidDataException">The header magic is wrong, an entry overruns the file, the chain exceeds the entry budget, or a name is not valid ASCII.<para>头部魔数错误、条目越界、链超出条目预算，或分区名不是有效 ASCII。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<LkImage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await ReadAsync(stream, path, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Parses the entry chain from a caller-owned seekable stream whose position is restored afterwards.
    /// <para>从调用方持有的可寻址流解析条目链，结束后还原流位置。</para>
    /// </summary>
    /// <param name="source">Readable, seekable stream.<para>可读、可寻址的流。</para></param>
    /// <param name="path">Display path used in the result.<para>结果中使用的展示路径。</para></param>
    /// <param name="cancellationToken">Cancels the scan.<para>取消本次扫描。</para></param>
    /// <returns>The parsed image.<para>解析后的镜像。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> is <see langword="null"/>.<para><paramref name="source"/> 为 <see langword="null"/>。</para></exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> cannot be read or seeked.<para><paramref name="source"/> 不可读或不可寻址。</para></exception>
    /// <exception cref="InvalidDataException">The stream layout is not a supported LK chain.<para>流布局不是受支持的 LK 链。</para></exception>
    /// <exception cref="OperationCanceledException">Cancellation was requested.<para>已请求取消。</para></exception>
    public static async Task<LkImage> ReadAsync(Stream source, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("The LK image source must be readable and seekable.", nameof(source));
        }

        var origin = source.Position;
        try
        {
            var length = source.Length;
            if (length < LkImageConstants.HeaderSize)
            {
                throw new InvalidDataException("The file is shorter than one LK header.");
            }

            if (length > LkImageConstants.MaximumImageBytes)
            {
                throw new InvalidDataException("The LK image exceeds the supported size limit.");
            }

            var entries = new List<LkImageEntry>();
            var offset = 0L;
            var header = new byte[LkImageConstants.HeaderSize];
            string Fingerprint()
            {
                var builder = new StringBuilder();
                foreach (var entry in entries)
                {
                    builder.Append(entry.Name).Append(':').Append(entry.DataSize.ToString(CultureInfo.InvariantCulture))
                        .Append(':').Append(entry.MemoryAddress.ToString("X8", CultureInfo.InvariantCulture)).Append(';');
                }

                return builder.ToString();
            }

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= LkImageConstants.MaximumEntries)
                {
                    throw new InvalidDataException("The LK entry chain exceeds the supported entry count.");
                }

                if (offset < 0 || offset > length - LkImageConstants.HeaderSize)
                {
                    throw new InvalidDataException("An LK entry header starts outside the file.");
                }

                source.Position = offset;
                await source.ReadExactlyAsync(header.AsMemory(), cancellationToken).ConfigureAwait(false);
                var magic = LkImageConstants.ReadUInt32(header, 0);
                if (magic != LkImageConstants.Magic)
                {
                    throw new InvalidDataException("The LK entry header magic is invalid.");
                }

                var name = ReadName(header);
                var kind = ReadKind(header.AsSpan(ImageTypeOffset));
                var declared = LkImageConstants.ReadUInt32(header, DataSizeOffset);
                var extended = LkImageConstants.ReadUInt32(header, ExtendedMagicOffset) == LkImageConstants.ExtendedMagic;
                long dataSize = extended
                    ? ((long)LkImageConstants.ReadUInt32(header, DataSizeExtendedOffset) << 32) | declared
                    : declared;
                var memoryAddress = LkImageConstants.ReadUInt32(header, MemoryAddressOffset);
                var dataOffset = offset + LkImageConstants.HeaderSize;
                if (dataSize < 0 || dataOffset > length || dataSize > length - dataOffset)
                {
                    throw new InvalidDataException("An LK entry payload overruns the file.");
                }

                var isLast = LkImageConstants.ReadUInt32(header, IsLastOffset) != 0;
                entries.Add(new LkImageEntry(
                    name,
                    kind,
                    offset,
                    dataOffset,
                    dataSize,
                    memoryAddress,
                    LkImageConstants.ReadUInt32(header, AddressingModeOffset),
                    extended,
                    LkImageConstants.ReadUInt32(header, HeaderVersionOffset),
                    LkImageConstants.ReadUInt32(header, AlignmentOffset),
                    isLast,
                    CertificateName: null,
                    CertificateSize: 0));

                var alignment = LkImageConstants.ReadUInt32(header, AlignmentOffset);
                var rawNext = dataOffset + dataSize;
                offset = alignment <= 1
                    ? rawNext
                    : rawNext + ((alignment - (rawNext % alignment)) % alignment);
                if (isLast)
                {
                    break;
                }
            }

            var withCertificates = BindCertificates(entries);
            return new LkImage(path, length, withCertificates, Fingerprint());
        }
        finally
        {
            source.Position = origin;
        }
    }

    private static IReadOnlyList<LkImageEntry> BindCertificates(List<LkImageEntry> entries)
    {
        if (entries.Count == 0)
        {
            return entries;
        }

        var bound = new List<LkImageEntry>(entries.Count);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            string? certificateName = null;
            long certificateSize = 0;
            for (var probe = index + 1; probe < entries.Count; probe++)
            {
                var candidate = entries[probe];
                if (candidate.Kind != LkImageEntryKind.Certificate)
                {
                    continue;
                }

                if (!IsCertificateOf(candidate.Name, entry.Name))
                {
                    continue;
                }

                certificateName = candidate.Name;
                certificateSize = candidate.DataSize;
                break;
            }

            bound.Add(entry with { CertificateName = certificateName, CertificateSize = certificateSize });
        }

        return bound;
    }

    private static bool IsCertificateOf(string candidate, string owner) =>
        string.Equals(candidate, owner, StringComparison.Ordinal)
        || string.Equals(candidate, "cert1_" + owner, StringComparison.Ordinal)
        || string.Equals(candidate, "cert2_" + owner, StringComparison.Ordinal)
        || (string.Equals(owner, "lk", StringComparison.Ordinal)
            && (string.Equals(candidate, "cert1", StringComparison.Ordinal)
                || string.Equals(candidate, "cert2", StringComparison.Ordinal)));

    private static string ReadName(ReadOnlySpan<byte> header)
    {
        var raw = header.Slice(NameOffset, LkImageConstants.NameLength);
        var end = raw.IndexOf((byte)0);
        if (end < 0)
        {
            end = raw.Length;
        }

        var name = raw[..end];
        if (name.IsEmpty)
        {
            throw new InvalidDataException("An LK entry carries an empty partition name.");
        }

        foreach (var value in name)
        {
            if (value < 0x20 || value > 0x7E)
            {
                throw new InvalidDataException("An LK entry partition name is not printable ASCII.");
            }
        }

        return Encoding.ASCII.GetString(name);
    }

    private static LkImageEntryKind ReadKind(ReadOnlySpan<byte> imageType)
    {
        if (imageType.Length < 4)
        {
            return LkImageEntryKind.Application;
        }

        return imageType[3] switch
        {
            LkImageConstants.GroupModem => LkImageEntryKind.Modem,
            LkImageConstants.GroupCertificate => LkImageEntryKind.Certificate,
            _ => LkImageEntryKind.Application,
        };
    }

    internal static byte[] ReadPayloadHash(string path, LkImageEntry entry, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        stream.Position = entry.DataOffset;
        using var hash = Sha256.CreateAccumulator();
        var buffer = new byte[81920];
        var remaining = entry.DataSize;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var want = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, want);
            if (read <= 0)
            {
                throw new InvalidDataException("An LK entry payload ended before its declared length.");
            }

            hash.Append(buffer.AsSpan(0, read));
            remaining -= read;
        }

        return hash.GetHashAndReset();
    }
}