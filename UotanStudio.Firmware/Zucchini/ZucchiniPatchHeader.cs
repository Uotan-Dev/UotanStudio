namespace UotanStudio.Firmware.Zucchini;

/// <summary>
/// The fixed Zucchini patch header: seven little-endian uint32 fields.
/// <para>Zucchini 补丁定长头部：七个 little-endian uint32 字段。</para>
/// </summary>
internal readonly struct ZucchiniPatchHeader
{
    internal ZucchiniPatchHeader(
        uint magic,
        uint version,
        uint oldSize,
        uint oldCrc,
        uint newSize,
        uint newCrc,
        uint numElements)
    {
        Magic = magic;
        Version = version;
        OldSize = oldSize;
        OldCrc = oldCrc;
        NewSize = newSize;
        NewCrc = newCrc;
        NumElements = numElements;
    }

    /// <summary>The patch magic value. <para>补丁魔数。</para></summary>
    internal uint Magic { get; }

    /// <summary>The patch format version. <para>补丁格式版本。</para></summary>
    internal uint Version { get; }

    /// <summary>The size in bytes of the old (source) image. <para>旧（源）镜像字节数。</para></summary>
    internal uint OldSize { get; }

    /// <summary>The CRC-32 of the old image. <para>旧镜像的 CRC-32。</para></summary>
    internal uint OldCrc { get; }

    /// <summary>The size in bytes of the new (destination) image. <para>新（目标）镜像字节数。</para></summary>
    internal uint NewSize { get; }

    /// <summary>The CRC-32 of the new image. <para>新镜像的 CRC-32。</para></summary>
    internal uint NewCrc { get; }

    /// <summary>The number of patch elements. <para>补丁元素个数。</para></summary>
    internal uint NumElements { get; }
}
