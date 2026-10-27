using System.Buffers.Binary;
using System.Text;
using UotanStudio.Firmware.Core.Text;

namespace UotanStudio.Firmware.BootSecurity.Avb;

/// <summary>
/// Extracts AVB hash descriptors from a vbmeta image, accepting the full 256-byte header and the 80-byte
/// short header some truncated vendor images carry.
/// <para>从 vbmeta 镜像提取 AVB 哈希描述符，兼容完整的 256 字节头部与部分截断厂商镜像携带的 80 字节短头部。</para>
/// </summary>
/// <remarks>
/// Input is treated as untrusted: every offset, length, alignment and descriptor count is checked
/// against its containing region before a slice is taken. Invalid containers and malformed entries yield an
/// empty or partial list instead of an exception, so callers can report "no descriptors" without unwrapping
/// parse failures.
/// <para>输入按不可信处理：截取任何区间之前都会针对所在区域校验偏移、长度、对齐与描述符数量。非法容器与畸形条目
/// 返回空列表或部分列表而不抛出异常，调用方无需解包解析失败即可报告“没有描述符”。</para>
/// </remarks>
public static class AvbDescriptorReader
{
    private const int ShortHeaderSize = 80;
    private const int DescriptorHeaderSize = 16;
    private const int HashDescriptorBodySize = 116;
    private const int MaximumDescriptors = 4096;
    private const int MaximumDescriptorBytes = 16 * 1024 * 1024;
    private const int MaximumVariableFieldBytes = 255;
    private const ulong HashDescriptorTag = 2;

    /// <summary>
    /// Returns the tag-2 hash descriptors declared by a vbmeta image in wire order.
    /// <para>按线格式顺序返回 vbmeta 镜像声明的 tag-2 哈希描述符。</para>
    /// </summary>
    /// <param name="vbmeta">Complete vbmeta image bytes; only the header, authentication block and auxiliary
    /// descriptor region are read. <para>完整的 vbmeta 镜像字节；只读取头部、认证块与辅助描述符区。</para></param>
    /// <returns>The hash descriptors found; empty when the container is not AVB0, a region is out of range or
    /// the declared descriptor table is unusable. Malformed individual entries are skipped.
    /// <para>找到的哈希描述符；容器不是 AVB0、区域越界或声明的描述符表不可用时为空。畸形的单个条目会被跳过。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="vbmeta"/> is <see langword="null"/>.
    /// <para><paramref name="vbmeta"/> 为 <see langword="null"/>。</para></exception>
    public static IReadOnlyList<AvbHashDescriptor> ExtractHashDescriptors(byte[] vbmeta)
    {
        ArgumentNullException.ThrowIfNull(vbmeta);
        if (vbmeta.Length < ShortHeaderSize || !vbmeta.AsSpan(0, 4).SequenceEqual("AVB0"u8))
        {
            return [];
        }

        var headerSize = vbmeta.Length >= AvbVBMetaImageHeader.Size ? AvbVBMetaImageHeader.Size : ShortHeaderSize;
        var authentication = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan(12, 8));
        var auxiliary = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan(20, 8));
        var available = (ulong)vbmeta.Length - (ulong)headerSize;
        if (authentication > available || auxiliary > available - authentication || auxiliary > int.MaxValue)
        {
            return [];
        }

        var auxiliaryStart = (int)((ulong)headerSize + authentication);
        var auxiliaryEnd = auxiliaryStart + (int)auxiliary;
        var scanStart = auxiliaryStart;
        var scanEnd = auxiliaryEnd;
        if (headerSize == AvbVBMetaImageHeader.Size)
        {
            var descriptorsOffset = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan(96, 8));
            var descriptorsSize = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan(104, 8));
            if (descriptorsSize == 0)
            {
                return [];
            }
            if (descriptorsOffset > auxiliary || descriptorsSize > auxiliary - descriptorsOffset
                || descriptorsSize > MaximumDescriptorBytes)
            {
                return [];
            }
            scanStart = auxiliaryStart + (int)descriptorsOffset;
            scanEnd = scanStart + (int)descriptorsSize;
        }

        var result = new List<AvbHashDescriptor>();
        var cursor = (ulong)scanStart;
        var end = (ulong)scanEnd;
        var count = 0;
        while (end - cursor >= DescriptorHeaderSize)
        {
            if (++count > MaximumDescriptors)
            {
                break;
            }
            var tag = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan((int)cursor, 8));
            var length = BinaryPrimitives.ReadUInt64BigEndian(vbmeta.AsSpan((int)cursor + 8, 8));
            if (length % 8 != 0 || length > end - cursor - DescriptorHeaderSize)
            {
                break;
            }
            if (tag == HashDescriptorTag)
            {
                var descriptor = TryReadHashDescriptor(vbmeta, (int)cursor + DescriptorHeaderSize, (int)length);
                if (descriptor is not null)
                {
                    result.Add(descriptor);
                }
            }
            cursor += DescriptorHeaderSize + length;
        }
        return result;
    }

    private static AvbHashDescriptor? TryReadHashDescriptor(byte[] data, int bodyStart, int bodyLength)
    {
        if (bodyLength < HashDescriptorBodySize)
        {
            return null;
        }
        var nameLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 40, 4));
        var saltLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 44, 4));
        var digestLength = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 48, 4));
        if (nameLength > MaximumVariableFieldBytes || saltLength > MaximumVariableFieldBytes
            || digestLength > MaximumVariableFieldBytes)
        {
            return null;
        }
        var nameStart = bodyStart + HashDescriptorBodySize;
        var used = HashDescriptorBodySize + (long)nameLength + saltLength + digestLength;
        if (used > bodyLength)
        {
            return null;
        }
        var saltStart = nameStart + (int)nameLength;
        var digestStart = saltStart + (int)saltLength;
        return new AvbHashDescriptor
        {
            Tag = HashDescriptorTag,
            ImageSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(bodyStart, 8)),
            HashAlgorithm = NulText.ReadCString(data.AsSpan(bodyStart + 8, 32), Encoding.ASCII),
            Flags = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(bodyStart + 52, 4)),
            PartitionName = Encoding.UTF8.GetStringTrimNul(data, nameStart, (int)nameLength),
            Salt = data.AsSpan(saltStart, (int)saltLength).ToArray(),
            Digest = data.AsSpan(digestStart, (int)digestLength).ToArray(),
        };
    }
}