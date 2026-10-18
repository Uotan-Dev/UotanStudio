using UotanStudio.Firmware.DeviceTree.Dt;

namespace UotanStudio.Firmware.BootSecurity.BootImage.Components;

/// <summary>
/// Locates flat device tree (FDT) blobs inside a concatenated kernel-plus-DTB image and splits the two parts.
/// <para>在内核与 DTB 拼接的镜像中定位扁平设备树（FDT）块，并拆分两部分。</para>
/// </summary>
public static class KernelDtbTool
{
    /// <summary>
    /// A named binary component extracted from a kernel/DTB image.
    /// <para>从内核/DTB 镜像中提取的具名二进制组件。</para>
    /// </summary>
    public class KernelDtbComponent
    {
        /// <summary>Component identifier. <para>组件标识。</para></summary>
        /// <value>Display name identifying the component. <para>标识该组件的显示名称。</para></value>
        public string Name { get; set; } = string.Empty;

        /// <summary>Component payload. <para>组件负载。</para></summary>
        /// <value>Raw bytes of the component; empty until populated. <para>组件的原始字节；填充前为空。</para></value>
        public byte[] Data { get; set; } = [];

        /// <summary>Component description. <para>组件描述。</para></summary>
        /// <value>Human-readable description of the component. <para>组件的人类可读描述。</para></value>
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// How the kernel and its DTB blobs are laid out within a boot image.
    /// <para>内核及其 DTB 块在启动镜像中的排布方式。</para>
    /// </summary>
    public enum KernelDtbType
    {
        /// <summary>Layout not determined. <para>排布方式未确定。</para></summary>
        Unknown = 0,

        /// <summary>Kernel and DTB stored as separate components. <para>内核与 DTB 作为独立组件分别存储。</para></summary>
        Separate = 1,

        /// <summary>DTB blob(s) appended after the kernel in one buffer. <para>DTB 块追加在内核之后，共处同一缓冲区。</para></summary>
        Merged = 2
    }

    /// <summary>
    /// Extracts the kernel data (the part before the first valid FDT in the merged format).
    /// <para>提取内核数据（合并格式中首个有效 FDT 之前的部分）。</para>
    /// </summary>
    /// <param name="data">
    /// Merged kernel-plus-DTB buffer scanned byte by byte for a valid FDT header.
    /// <para>逐字节扫描有效 FDT 头的内核加 DTB 合并缓冲区。</para>
    /// </param>
    /// <returns>The bytes preceding the first valid FDT, or an empty array when no FDT is found or it starts at offset 0.
    /// <para>首个有效 FDT 之前的字节；未找到 FDT 或其起始于偏移 0 时返回空数组。</para>
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para>
    /// </exception>
    public static byte[] ExtractKernel(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryFindFdt(data, out var offset, out _) && offset > 0
            ? data.AsSpan(0, offset).ToArray()
            : [];
    }

    /// <summary>Extracts the first DTB (the first valid FDT in the merged format).
    /// <para>提取第一个 DTB（合并格式中的首个有效 FDT）。</para></summary>
    /// <param name="data">Merged kernel-plus-DTB buffer scanned byte by byte for a valid FDT header.
    /// <para>逐字节扫描有效 FDT 头的内核加 DTB 合并缓冲区。</para></param>
    /// <returns>The full first FDT blob spanning its total size, or an empty array when no valid FDT is present.
    /// <para>覆盖其总长度的首个完整 FDT 块；不存在有效 FDT 时返回空数组。</para></returns>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.
    /// <para><paramref name="data"/> 为 <see langword="null"/>。</para></exception>
    public static byte[] ExtractFirstDtb(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return TryFindFdt(data, out var offset, out var header)
            ? data.AsSpan(offset, checked((int)header.TotalSize)).ToArray()
            : [];
    }

    private static bool TryFindFdt(ReadOnlySpan<byte> data, out int offset, out FdtHeader header)
    {
        offset = 0;
        header = new();

        for (var i = 0; i + FdtConstants.HeaderSize <= data.Length; i++)
        {
            if (ReadUInt32BigEndian(data.Slice(i, 4)) != FdtConstants.FdtMagic)
            {
                continue;
            }

            FdtHeader candidate;
            try
            {
                candidate = FdtHeader.ReadFrom(data[i..]);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (candidate.Version < FdtConstants.FdtLastCompVersion || candidate.TotalSize < FdtConstants.HeaderSize)
            {
                continue;
            }

            if (candidate.TotalSize > data.Length - i)
            {
                continue;
            }

            offset = i;
            header = candidate;
            return true;
        }

        return false;
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> data) => System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data);
}